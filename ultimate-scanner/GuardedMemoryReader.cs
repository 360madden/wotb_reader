using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.UltimateScanner;

internal interface IAuthorizedMemoryReader
{
    ValueTask<OperationResult<byte[]>> ReadAsync(
        nint address,
        int length,
        CancellationToken cancellationToken);
}

internal interface IGuardedMemoryReaderFactory
{
    ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
        AuthorizedMemoryObservation observation,
        CancellationToken cancellationToken);
}

internal sealed record AuthorizedMemoryObservation(
    int ProcessId,
    long ProcessStartIdentity,
    string CanonicalExecutablePath,
    string ProductVersion,
    ContentHash ExecutableSha256,
    DateTimeOffset ExpiresAtUtc,
    AuthorizationReadGate ReadGate)
{
    /// <summary>Coordinator authorization generation captured when the scan was admitted.</summary>
    internal long Generation { get; init; }
}

/// <summary>
/// Linearizes authorization admission with each native read. A read admitted
/// before revocation may complete, while reads admitted after revocation are
/// denied without calling Win32. Revocation never waits for a synchronous native
/// call already in progress.
/// </summary>
internal sealed class AuthorizationReadGate
{
    private readonly object _sync = new();
    private int _revoked;

    internal void Revoke()
    {
        Volatile.Write(ref _revoked, 1);
    }

    internal bool IsRevoked => Volatile.Read(ref _revoked) != 0;

    internal bool TryExecute(
        Func<bool> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _revoked) != 0)
            {
                return false;
            }
        }

        // Admission is linearized under _sync, but the synchronous native call
        // runs outside that lock. Revoke can therefore invalidate the session
        // promptly while an already-admitted read finishes; no operation that
        // enters after Revoke's linearization point is admitted.
        return operation();
    }
}

/// <summary>One identity-bound, short-lived VM-read capability.</summary>
internal sealed class AuthorizedProcessLease : IDisposable
{
    private const uint VmRead = 0x0010;
    private const uint QueryInformation = 0x0400;
    private readonly AuthorizedMemoryObservation _observation;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private AuthorizedProcessLease(
        AuthorizedMemoryObservation observation,
        TimeProvider timeProvider,
        SafeProcessHandle handle)
    {
        _observation = observation;
        _timeProvider = timeProvider;
        Handle = handle;
    }

    internal SafeProcessHandle Handle { get; }
    internal string Architecture { get; private set; } = "unknown";

    internal static AuthorizedProcessLease? Open(
        AuthorizedMemoryObservation observation,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()
            || observation.ProcessId <= 0
            || observation.ProcessStartIdentity <= 0
            || observation.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return null;
        }

        SafeProcessHandle handle = NativeMethods.OpenProcess(
            VmRead | QueryInformation,
            bInheritHandle: false,
            checked((uint)observation.ProcessId));
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var lease = new AuthorizedProcessLease(observation, timeProvider, handle);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lease.RevalidateIdentity()
                || !lease.TryGetSupportedArchitecture(out string architecture))
            {
                lease.Dispose();
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lease.Architecture = architecture;
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal bool IsValid() =>
        !_disposed
        && _observation.ExpiresAtUtc > _timeProvider.GetUtcNow()
        && RevalidateIdentity();

    internal bool TryRead(
        nint address,
        byte[] buffer,
        int offset,
        int length,
        CancellationToken cancellationToken,
        out nuint bytesRead)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bytesRead = 0;
        if (offset < 0 || length <= 0 || offset > buffer.Length - length)
        {
            return false;
        }

        bool ReadCore(out nuint nativeBytesRead)
        {
            nativeBytesRead = 0;
            if (!IsValid())
            {
                return false;
            }

            GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint destination = IntPtr.Add(pinned.AddrOfPinnedObject(), offset);
                bool readSucceeded = NativeMethods.ReadProcessMemory(
                    Handle,
                    address,
                    destination,
                    (nuint)length,
                    out nativeBytesRead);
                cancellationToken.ThrowIfCancellationRequested();
                return readSucceeded;
            }
            finally
            {
                pinned.Free();
            }
        }

        nuint nativeBytesRead = 0;
        bool readSucceeded = _observation.ReadGate.TryExecute(
            () => ReadCore(out nativeBytesRead),
            cancellationToken);
        bytesRead = nativeBytesRead;
        return readSucceeded;
    }

    private bool RevalidateIdentity()
    {
        if (!NativeMethods.GetProcessTimes(
                Handle,
                out NativeFileTime creationTime,
                out _,
                out _,
                out _)
            || creationTime.ToInt64() != _observation.ProcessStartIdentity)
        {
            return false;
        }

        uint pathCapacity = 32_768;
        char[] pathBuffer = new char[pathCapacity];
        if (!NativeMethods.QueryFullProcessImageNameW(
                Handle,
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

    private bool TryGetSupportedArchitecture(out string architecture)
    {
        architecture = "unknown";
        if (!Environment.Is64BitProcess
            || !NativeMethods.IsWow64Process2(
                Handle,
                out ushort processMachine,
                out ushort nativeMachine))
        {
            return false;
        }

        const ushort ImageFileMachineUnknown = 0x0000;
        const ushort ImageFileMachineI386 = 0x014C;
        const ushort ImageFileMachineAmd64 = 0x8664;
        // This project intentionally supports the x64 scanner path only.
        if (processMachine == ImageFileMachineI386
            || nativeMachine != ImageFileMachineAmd64
            || processMachine != ImageFileMachineUnknown)
        {
            return false;
        }

        architecture = "x64";
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Handle.Dispose();
    }
}

internal sealed class GuardedMemoryReaderFactory(TimeProvider timeProvider)
    : IGuardedMemoryReaderFactory
{
    private const int MaxReadLength = 64 * 1024;
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
        AuthorizedMemoryObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ReadGate is null
            || observation.ProcessId <= 0
            || observation.ProcessStartIdentity <= 0
            || string.IsNullOrWhiteSpace(observation.CanonicalExecutablePath)
            || string.IsNullOrWhiteSpace(observation.ProductVersion)
            || observation.ExecutableSha256.Value is not { Length: 64 }
            || observation.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return ValueTask.FromResult(OperationResult.Failure<IAuthorizedMemoryReader>(
                new ApplicationError(
                    "game.memory.observation_invalid",
                    "The authorized observation is invalid or expired.",
                    Retryable: false)));
        }

        return ValueTask.FromResult(OperationResult.Success<IAuthorizedMemoryReader>(
            new EphemeralMemoryReader(observation, _timeProvider)));
    }

    public override string ToString() => nameof(GuardedMemoryReaderFactory);

    private sealed class EphemeralMemoryReader(
        AuthorizedMemoryObservation observation,
        TimeProvider timeProvider)
        : IAuthorizedMemoryReader
    {
        public ValueTask<OperationResult<byte[]>> ReadAsync(
            nint address,
            int length,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length <= 0 || length > MaxReadLength)
            {
                return ValueTask.FromResult(OperationResult.Failure<byte[]>(
                    new ApplicationError(
                        "game.memory.invalid_range",
                        "The memory read range is invalid.",
                        Retryable: false)));
            }

            using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(
                observation,
                timeProvider,
                cancellationToken);
            if (lease is null)
            {
                return ValueTask.FromResult(OperationResult.Failure<byte[]>(
                    new ApplicationError(
                        "game.memory.identity_mismatch",
                        "The process identity or architecture is not authorized.",
                        Retryable: false)));
            }

            byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
            try
            {
                if (!lease.TryRead(address, buffer, 0, length, cancellationToken, out nuint bytesRead)
                    || bytesRead != (nuint)length)
                {
                    return ValueTask.FromResult(OperationResult.Failure<byte[]>(
                        new ApplicationError(
                            "game.memory.read_failed",
                            "The memory read operation failed.",
                            Retryable: false)));
                }

                return ValueTask.FromResult(OperationResult.Success(buffer));
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return ValueTask.FromResult(OperationResult.Failure<byte[]>(
                    new ApplicationError(
                        "game.memory.unavailable",
                        "The memory read is unavailable.",
                        Retryable: true)));
            }
        }
    }
}
