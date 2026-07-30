using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// A bounded, short-lived VM-read capability that must be revalidated before
/// every read. The handle opens only with PROCESS_VM_READ and is disposed
/// immediately after each operation.
/// </summary>
internal interface IAuthorizedMemoryReader
{
    ValueTask<OperationResult<byte[]>> ReadAsync(
        nint address,
        int length,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates bounded memory readers only from a positively verified observation.
/// Each reader opens its own handle and disposes it after each read, then
/// revalidates the observation before the next read. No persistent
/// PROCESS_VM_READ handle survives beyond a single bounded operation.
/// </summary>
internal interface IGuardedMemoryReaderFactory
{
    ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
        AuthorizedMemoryObservation observation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Immutable evidence that a specific process is in a verified offline replay
/// state. This record is the only input the memory reader factory accepts.
/// </summary>
internal sealed record AuthorizedMemoryObservation(
    int ProcessId,
    long ProcessStartIdentity,
    string CanonicalExecutablePath,
    string ProductVersion,
    ContentHash ExecutableSha256,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Opens a process with only PROCESS_VM_READ, performs one bounded read, and
/// immediately closes the handle. Revalidates the observation before every
/// read — no persistent handle survives.
/// </summary>
internal sealed class GuardedMemoryReaderFactory(
    TimeProvider timeProvider)
    : IGuardedMemoryReaderFactory
{
    private const uint VmRead = 0x0010;
    private const int MaxReadLength = 64 * 1024;

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
        AuthorizedMemoryObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ProcessId <= 0
            || observation.ProcessStartIdentity <= 0
            || string.IsNullOrWhiteSpace(observation.CanonicalExecutablePath)
            || string.IsNullOrWhiteSpace(observation.ProductVersion)
            || observation.ExecutableSha256.Value is not { Length: 64 }
            || observation.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return new ValueTask<OperationResult<IAuthorizedMemoryReader>>(
                OperationResult.Failure<IAuthorizedMemoryReader>(
                    new ApplicationError(
                        "game.memory.observation_invalid",
                        "The authorized observation is invalid or expired.",
                        Retryable: false)));
        }

        var reader = new EphemeralMemoryReader(observation, _timeProvider);
        return new ValueTask<OperationResult<IAuthorizedMemoryReader>>(
            OperationResult.Success<IAuthorizedMemoryReader>(reader));
    }

    public override string ToString() => nameof(GuardedMemoryReaderFactory);

    private sealed class EphemeralMemoryReader(
        AuthorizedMemoryObservation observation,
        TimeProvider timeProvider)
        : IAuthorizedMemoryReader
    {
        private readonly AuthorizedMemoryObservation _observation = observation;
        private readonly TimeProvider _timeProvider = timeProvider;

        public ValueTask<OperationResult<byte[]>> ReadAsync(
            nint address,
            int length,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (length <= 0 || length > MaxReadLength)
            {
                return new ValueTask<OperationResult<byte[]>>(InvalidRange());
            }

            if (_observation.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                return new ValueTask<OperationResult<byte[]>>(Expired());
            }

            if (!OperatingSystem.IsWindows())
            {
                return new ValueTask<OperationResult<byte[]>>(UnsupportedPlatform());
            }

            SafeProcessHandle? handle = null;
            try
            {
                handle = NativeMethods.OpenProcess(
                    VmRead,
                    bInheritHandle: false,
                    checked((uint)_observation.ProcessId));
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return new ValueTask<OperationResult<byte[]>>(OpenFailed());
                }

                if (!RevalidateIdentity(handle))
                {
                    return new ValueTask<OperationResult<byte[]>>(IdentityMismatch());
                }

                byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
                GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    if (!NativeMethods.ReadProcessMemory(
                            handle,
                            address,
                            pinned.AddrOfPinnedObject(),
                            (nuint)length,
                            out _))
                    {
                        return new ValueTask<OperationResult<byte[]>>(ReadFailed());
                    }
                }
                finally
                {
                    pinned.Free();
                }

                return new ValueTask<OperationResult<byte[]>>(
                    OperationResult.Success(buffer));
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return new ValueTask<OperationResult<byte[]>>(Unavailable());
            }
            finally
            {
                handle?.Dispose();
            }
        }

        private bool RevalidateIdentity(SafeProcessHandle handle)
        {
            if (!NativeMethods.GetProcessTimes(
                    handle,
                    out NativeFileTime creationTime,
                    out _,
                    out _,
                    out _))
            {
                return false;
            }

            if (creationTime.ToInt64() != _observation.ProcessStartIdentity)
            {
                return false;
            }

            uint pathCapacity = 32_768;
            char[] pathBuffer = new char[pathCapacity];
            if (!NativeMethods.QueryFullProcessImageNameW(
                    handle,
                    0,
                    pathBuffer,
                    ref pathCapacity)
                || pathCapacity >= pathBuffer.Length)
            {
                return false;
            }

            string currentPath = new(pathBuffer, 0, checked((int)pathCapacity));
            return string.Equals(
                currentPath,
                _observation.CanonicalExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }

        private static OperationResult<byte[]> InvalidRange() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.invalid_range",
                    "The memory read range is invalid.",
                    Retryable: false));

        private static OperationResult<byte[]> Expired() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.observation_expired",
                    "The authorized observation has expired.",
                    Retryable: false));

        private static OperationResult<byte[]> UnsupportedPlatform() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.platform_unsupported",
                    "Memory reading is only supported on Windows.",
                    Retryable: false));

        private static OperationResult<byte[]> OpenFailed() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.open_failed",
                    "The process could not be opened for memory reading.",
                    Retryable: false));

        private static OperationResult<byte[]> IdentityMismatch() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.identity_mismatch",
                    "The process identity does not match the authorized observation.",
                    Retryable: false));

        private static OperationResult<byte[]> ReadFailed() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.read_failed",
                    "The memory read operation failed.",
                    Retryable: false));

        private static OperationResult<byte[]> Unavailable() =>
            OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "game.memory.unavailable",
                    "The memory read is unavailable.",
                    Retryable: true));
    }
}
