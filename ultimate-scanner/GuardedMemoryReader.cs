using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.UltimateScanner;

internal interface IAuthorizedMemoryReader
{
    ValueTask<OperationResult<byte[]>> ReadAsync(
        nint address,
        int length,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads many addresses under ONE process lease. The monitor loop re-reads
    /// up to 2000 staged addresses every few seconds; a per-address lease
    /// would open and revalidate that many handles per round.
    /// </summary>
    ValueTask<OperationResult<IReadOnlyList<MemoryReadItem>>> ReadBatchAsync(
        IReadOnlyList<nint> addresses,
        int length,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the exact-build entity resolver under one identity-bound process
    /// lease. The module base and layout are coordinator-owned.
    /// </summary>
    ValueTask<OperationResult<Type10EntityPositionResult>> ResolveEntityPositionAsync(
        nint moduleBase,
        int entityId,
        Type10EntityPositionLayout layout,
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
    internal int PointerSize { get; private set; }
    internal long MaximumUserAddress { get; private set; }

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
                || !lease.TryGetSupportedArchitecture(
                    out string architecture,
                    out int pointerSize,
                    out long maximumUserAddress))
            {
                lease.Dispose();
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lease.Architecture = architecture;
            lease.PointerSize = pointerSize;
            lease.MaximumUserAddress = maximumUserAddress;
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

    private bool TryGetSupportedArchitecture(
        out string architecture,
        out int pointerSize,
        out long maximumUserAddress)
    {
        architecture = "unknown";
        pointerSize = 0;
        maximumUserAddress = 0;
        if (!Environment.Is64BitProcess
            || !NativeMethods.IsWow64Process2(
                Handle,
                out ushort processMachine,
                out ushort nativeMachine))
        {
            return false;
        }

        return TryResolveTargetArchitecture(
            processMachine,
            nativeMachine,
            out architecture,
            out pointerSize,
            out maximumUserAddress);
    }

    internal static bool TryResolveTargetArchitecture(
        ushort processMachine,
        ushort nativeMachine,
        out string architecture,
        out int pointerSize,
        out long maximumUserAddress)
    {
        const ushort ImageFileMachineUnknown = 0x0000;
        const ushort ImageFileMachineI386 = 0x014C;
        const ushort ImageFileMachineAmd64 = 0x8664;
        const long X86MaximumUserAddress = uint.MaxValue;
        const long X64MaximumUserAddress = 0x00007FFF_FFFF_FFFF;

        architecture = "unknown";
        pointerSize = 0;
        maximumUserAddress = 0;
        if (nativeMachine != ImageFileMachineAmd64)
        {
            return false;
        }

        switch (processMachine)
        {
            case ImageFileMachineI386:
                architecture = "x86";
                pointerSize = sizeof(uint);
                maximumUserAddress = X86MaximumUserAddress;
                return true;
            case ImageFileMachineUnknown:
                architecture = "x64";
                pointerSize = sizeof(ulong);
                maximumUserAddress = X64MaximumUserAddress;
                return true;
            default:
                return false;
        }
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

        public async ValueTask<OperationResult<IReadOnlyList<MemoryReadItem>>> ReadBatchAsync(
            IReadOnlyList<nint> addresses,
            int length,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (addresses is null
                || addresses.Count == 0
                || length <= 0
                || length > MaxReadLength)
            {
                return OperationResult.Failure<IReadOnlyList<MemoryReadItem>>(
                    new ApplicationError(
                        "game.memory.invalid_range",
                        "The batch memory read range is invalid.",
                        Retryable: false));
            }

            using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(
                observation,
                timeProvider,
                cancellationToken);
            if (lease is null)
            {
                return OperationResult.Failure<IReadOnlyList<MemoryReadItem>>(
                    new ApplicationError(
                        "game.memory.identity_mismatch",
                        "The process identity or architecture is not authorized.",
                        Retryable: false));
            }

            try
            {
                List<MemoryReadItem> items = new(addresses.Count);
                byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
                foreach (nint address in addresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool readOk;
                    nuint bytesRead;
                    try
                    {
                        readOk = lease.TryRead(
                            address,
                            buffer,
                            0,
                            length,
                            cancellationToken,
                            out bytesRead);
                    }
                    catch (Exception exception) when (
                        exception is Win32Exception
                            or IOException
                            or UnauthorizedAccessException
                            or InvalidOperationException)
                    {
                        // Isolate per-address failures: one throwing read must
                        // not abort the whole 2000-address round (that would
                        // blank every series for the round in the monitor
                        // loop). The unreadable marker keeps the round alive.
                        items.Add(new MemoryReadItem(
                            address.ToInt64(),
                            ReadOk: false,
                            null,
                            "unreadable"));
                        continue;
                    }

                    if (!readOk || bytesRead != (nuint)length)
                    {
                        items.Add(new MemoryReadItem(
                            address.ToInt64(),
                            ReadOk: false,
                            null,
                            "unreadable"));
                        continue;
                    }

                    items.Add(new MemoryReadItem(
                        address.ToInt64(),
                        ReadOk: true,
                        buffer.ToArray(),
                        Convert.ToHexString(buffer.AsSpan(0, length))));
                }

                return OperationResult.Success<IReadOnlyList<MemoryReadItem>>(items);
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return OperationResult.Failure<IReadOnlyList<MemoryReadItem>>(
                    new ApplicationError(
                        "game.memory.unavailable",
                        "The batch memory read is unavailable.",
                        Retryable: true));
            }
        }

        public ValueTask<OperationResult<Type10EntityPositionResult>> ResolveEntityPositionAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(layout);
            cancellationToken.ThrowIfCancellationRequested();
            long moduleBaseValue = moduleBase.ToInt64();
            if (moduleBaseValue is < 0x00010000 or > uint.MaxValue)
            {
                return ValueTask.FromResult(
                    OperationResult.Failure<Type10EntityPositionResult>(
                        new ApplicationError(
                            "game.memory.invalid_module_base",
                            "The module base is outside the supported x86 address range.",
                            Retryable: false)));
            }

            using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(
                observation,
                timeProvider,
                cancellationToken);
            if (lease is null || lease.Architecture != "x86" || lease.PointerSize != sizeof(uint))
            {
                return ValueTask.FromResult(
                    OperationResult.Failure<Type10EntityPositionResult>(
                        new ApplicationError(
                            "game.memory.identity_mismatch",
                            "The process identity or architecture is not authorized.",
                            Retryable: false)));
            }

            byte[] scratch = GC.AllocateUninitializedArray<byte>(0x38);
            bool Read(uint address, Span<byte> destination)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (destination.Length <= 0 || destination.Length > scratch.Length)
                {
                    return false;
                }

                bool readOk = lease.TryRead(
                    (nint)address,
                    scratch,
                    0,
                    destination.Length,
                    cancellationToken,
                    out nuint bytesRead);
                if (!readOk || bytesRead != (nuint)destination.Length)
                {
                    return false;
                }

                scratch.AsSpan(0, destination.Length).CopyTo(destination);
                return true;
            }

            try
            {
                Type10EntityPositionResult result = Type10EntityPositionResolver.Resolve(
                    checked((uint)moduleBaseValue),
                    entityId,
                    layout,
                    Read);
                return ValueTask.FromResult(OperationResult.Success(result));
            }
            catch (Exception exception) when (
                exception is Win32Exception
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return ValueTask.FromResult(
                    OperationResult.Failure<Type10EntityPositionResult>(
                        new ApplicationError(
                            "game.memory.unavailable",
                            "The entity-position read is unavailable.",
                            Retryable: true)));
            }
        }
    }
}
