using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;

namespace WotBTreader.UltimateScanner;

#pragma warning disable CA1873

internal sealed class MemoryScanEngine
{
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint Readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const int ReadChunkSize = 1_048_576;
    private const int MaximumSessions = 8;
    private const long MaximumSnapshotBytes = 512L * 1024 * 1024;
    private const long MinimumUserAddress = 0x10000;
    private const long MaximumUserAddress = 0x00007FFF_FFFF_FFFF;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemoryScanEngine> _logger;
    private readonly Dictionary<string, Snapshot> _sessions = new();
    private readonly Lock _lock = new();
    private int _sessionCounter;

    public MemoryScanEngine(TimeProvider timeProvider, ILogger<MemoryScanEngine> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal sealed record SnapshotChunk(
        long BaseAddress,
        int Length,
        int ValueSize,
        byte[] Values,
        BitArray Candidates,
        string AddressKind);

    internal sealed record Snapshot(
        string SessionId,
        DateTimeOffset CreatedAtUtc,
        AuthorizedMemoryObservation Observation,
        long ModuleBaseAddress,
        SnapshotFilter Filter,
        IReadOnlyList<SnapshotChunk> Chunks,
        long CandidateCount,
        long SnapshotBytes,
        DateTimeOffset? LastCompareAtUtc,
        int CompareCount);

    internal sealed record SnapshotFilter(
        int ValueSize,
        long MinAddress,
        long MaxAddress,
        float? FloatMin,
        float? FloatMax,
        int? IntMin,
        int? IntMax,
        long? LongMin,
        long? LongMax,
        ulong? UIntMin,
        ulong? UIntMax,
        MemoryValueKind ValueKind,
        int Alignment,
        MemoryRegionSelection RegionSelection);

    internal sealed record CompareResult(
        DateTimeOffset CompletedAtUtc,
        int PreviousCount,
        int CurrentCount,
        int ChangedCount,
        int UnchangedCount,
        int IncreasedCount,
        int DecreasedCount,
        IReadOnlyList<MemoryScanCandidate> Candidates,
        bool Truncated,
        bool ComparedAgainstRollingBaseline,
        int RetainedCount = 0);

    public OperationResult<string> CreateSnapshot(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        SnapshotFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedUserAddress(baseAddress))
        {
            return Error<string>(
                "discover.snapshot.invalid_options",
                "The module base address is outside the supported user address space.");
        }

        if (!ValidateFilter(filter, out string? error))
        {
            return Error<string>("discover.snapshot.invalid_options", error!);
        }

        string sessionId = Interlocked.Increment(ref _sessionCounter)
            .ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        _logger.LogInformation(
            "Memory snapshot started: sessionId={SessionId}, baseAddress=0x{BaseAddress:X}, valueKind={ValueKind}, valueSize={ValueSize}, alignment={Alignment}, minAddress=0x{MinAddress:X}, maxAddress=0x{MaxAddress:X}, floatMin={FloatMin}, floatMax={FloatMax}, intMin={IntMin}, intMax={IntMax}, longMin={LongMin}, longMax={LongMax}, uintMin={UIntMin}, uintMax={UIntMax}, regionSelection={RegionSelection}, processId={ProcessId}, processStartIdentity={ProcessStartIdentity}, executablePath={ExecutablePath}, productVersion={ProductVersion}, executableSha256={ExecutableSha256}",
            sessionId,
            baseAddress,
            filter.ValueKind,
            filter.ValueSize,
            filter.Alignment,
            filter.MinAddress,
            filter.MaxAddress,
            filter.FloatMin,
            filter.FloatMax,
            filter.IntMin,
            filter.IntMax,
            filter.LongMin,
            filter.LongMax,
            filter.UIntMin,
            filter.UIntMax,
            filter.RegionSelection,
            observation.ProcessId,
            observation.ProcessStartIdentity,
            observation.CanonicalExecutablePath,
            observation.ProductVersion,
            observation.ExecutableSha256.Value);

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider);
        if (lease is null)
        {
            _logger.LogWarning(
                "Memory snapshot denied: baseAddress=0x{BaseAddress:X}, processId={ProcessId}, executablePath={ExecutablePath}, reason=identity_mismatch",
                baseAddress,
                observation.ProcessId,
                observation.CanonicalExecutablePath);
            return Error<string>("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        List<MemoryRegion> regions = EnumerateRegions(lease.Handle, filter, cancellationToken);
        List<SnapshotChunk> chunks = [];
        long candidateCount = 0;
        long storedBytes = 0;
        int readFailureCount = 0;
        byte[] readBuffer = GC.AllocateUninitializedArray<byte>(ReadChunkSize);

        foreach (MemoryRegion region in regions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Memory snapshot cancelled: sessionId={SessionId}, baseAddress=0x{BaseAddress:X}, storedBytes={StoredBytes}, candidates={Candidates}, elapsedMs={ElapsedMs}",
                    sessionId,
                    baseAddress,
                    storedBytes,
                    candidateCount,
                    (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
            }
            long offset = 0;
            while (offset < region.Length)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Memory snapshot cancelled: sessionId={SessionId}, baseAddress=0x{BaseAddress:X}, regionBaseAddress=0x{RegionBaseAddress:X}, storedBytes={StoredBytes}, candidates={Candidates}, elapsedMs={ElapsedMs}",
                        sessionId,
                        baseAddress,
                        region.BaseAddress,
                        storedBytes,
                        candidateCount,
                        (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int length = (int)Math.Min(ReadChunkSize, region.Length - offset);
                if (storedBytes + length > MaximumSnapshotBytes)
                {
                    return Error<string>("discover.snapshot.size_limit", "Snapshot size limit reached; narrow the address or region filters.");
                }

                long address = checked(region.BaseAddress + offset);
                if (!lease.TryRead((nint)address, readBuffer, 0, length, out nuint read)
                    || read != (nuint)length)
                {
                    readFailureCount++;
                    offset += length;
                    continue;
                }

                string addressKind = GetAddressKind(lease.Handle, address);
                int valueCount = length / filter.ValueSize;
                byte[] values = new byte[valueCount * filter.ValueSize];
                BitArray matches = new(valueCount);
                for (int index = 0; index < valueCount; index++)
                {
                    int byteOffset = index * filter.ValueSize;
                    long absolute = address + byteOffset;
                    if (!IsAligned(absolute - baseAddress, filter.Alignment)
                        || !PassesFilter(readBuffer.AsSpan(byteOffset, filter.ValueSize), filter))
                    {
                        continue;
                    }

                    readBuffer.AsSpan(byteOffset, filter.ValueSize)
                        .CopyTo(values.AsSpan(byteOffset, filter.ValueSize));
                    matches[index] = true;
                    candidateCount++;
                }

                chunks.Add(new SnapshotChunk(
                    address,
                    length,
                    filter.ValueSize,
                    values,
                    matches,
                    addressKind));
                storedBytes += length;
                offset += length;
            }
        }

        var snapshot = new Snapshot(
            sessionId,
            _timeProvider.GetUtcNow(),
            observation,
            baseAddress,
            filter,
            chunks,
            candidateCount,
            storedBytes,
            null,
            0);

        lock (_lock)
        {
            ExpireSessionsLocked();
            while (_sessions.Count >= MaximumSessions)
            {
                string oldest = _sessions.Values.OrderBy(s => s.CreatedAtUtc).First().SessionId;
                _sessions.Remove(oldest);
            }
            _sessions[sessionId] = snapshot;
        }

        _logger.LogInformation(
            "Memory snapshot completed: sessionId={SessionId}, baseAddress=0x{BaseAddress:X}, valueKind={ValueKind}, regions={Regions}, candidates={Candidates}, bytes={Bytes}, readFailures={ReadFailures}, candidateSample={CandidateSample}, elapsedMs={ElapsedMs}, executablePath={ExecutablePath}, executableSha256={ExecutableSha256}",
            sessionId,
            baseAddress,
            filter.ValueKind,
            regions.Count,
            candidateCount,
            storedBytes,
            readFailureCount,
            FormatSnapshotCandidateSample(chunks, filter.ValueSize, filter.ValueKind, baseAddress),
            (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds,
            observation.CanonicalExecutablePath,
            observation.ExecutableSha256.Value);
        return OperationResult.Success(sessionId);
    }

    public OperationResult<CompareResult> Compare(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        string sessionId,
        string compareMode,
        int maxCandidates,
        bool advanceBaseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedUserAddress(baseAddress))
        {
            return Error<CompareResult>(
                "discover.invalid_base",
                "The module base address is outside the supported user address space.");
        }

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        _logger.LogInformation(
            "Memory snapshot comparison started: sessionId={SessionId}, baseAddress=0x{BaseAddress:X}, compareMode={CompareMode}, maxCandidates={MaxCandidates}, advanceBaseline={AdvanceBaseline}, processId={ProcessId}, executablePath={ExecutablePath}, executableSha256={ExecutableSha256}",
            sessionId,
            baseAddress,
            compareMode,
            maxCandidates,
            advanceBaseline,
            observation.ProcessId,
            observation.CanonicalExecutablePath,
            observation.ExecutableSha256.Value);

        Snapshot? previous;
        lock (_lock)
        {
            ExpireSessionsLocked();
            _sessions.TryGetValue(sessionId, out previous);
        }

        if (previous is null)
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, reason=session_not_found",
                sessionId);
            return Error<CompareResult>("discover.session_not_found", "The snapshot session was not found or expired.");
        }
        if (!SameIdentity(previous.Observation, observation))
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, reason=identity_mismatch, processId={ProcessId}, executablePath={ExecutablePath}",
                sessionId,
                observation.ProcessId,
                observation.CanonicalExecutablePath);
            return Error<CompareResult>("discover.identity_mismatch", "The snapshot belongs to a different process identity.");
        }
        if (!IsSnapshotBaseCompatible(previous, baseAddress))
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, requestedBaseAddress=0x{BaseAddress:X}, capturedBaseAddress=0x{CapturedBaseAddress:X}, reason=base_mismatch",
                sessionId,
                baseAddress,
                previous.ModuleBaseAddress);
            return Error<CompareResult>(
                "discover.base_mismatch",
                "The comparison base address does not match the snapshot.");
        }

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider);
        if (lease is null)
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, processId={ProcessId}, executablePath={ExecutablePath}, reason=identity_mismatch",
                sessionId,
                observation.ProcessId,
                observation.CanonicalExecutablePath);
            return Error<CompareResult>("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        int cap = Math.Clamp(maxCandidates, 1, 10_000);
        string normalizedCompareMode = string.IsNullOrWhiteSpace(compareMode)
            ? "changed"
            : compareMode.Trim().ToLowerInvariant();
        int changed = 0, unchanged = 0, increased = 0, decreased = 0, currentCount = 0;
        int retainedCandidateCount = 0;
        int readFailureCount = 0;
        List<MemoryScanCandidate> candidates = [];
        List<SnapshotChunk> nextChunks = [];
        byte[] readBuffer = GC.AllocateUninitializedArray<byte>(ReadChunkSize);

        foreach (SnapshotChunk chunk in previous.Chunks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Memory snapshot comparison cancelled: sessionId={SessionId}, elapsedMs={ElapsedMs}",
                    sessionId,
                    (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!lease.TryRead((nint)chunk.BaseAddress, readBuffer, 0, chunk.Length, out nuint read)
                || read != (nuint)chunk.Length)
            {
                readFailureCount++;
                if (advanceBaseline)
                {
                    nextChunks.Add(chunk);
                    retainedCandidateCount += chunk.Candidates.Cast<bool>().Count(candidate => candidate);
                }
                continue;
            }

            byte[] nextValues = advanceBaseline
                ? new byte[chunk.Values.Length]
                : chunk.Values;
            BitArray nextMatches = advanceBaseline
                ? new BitArray(chunk.Candidates.Length)
                : chunk.Candidates;

            for (int index = 0; index < chunk.Candidates.Length; index++)
            {
                if (!chunk.Candidates[index]) continue;
                int offset = index * chunk.ValueSize;
                ReadOnlySpan<byte> oldValue = chunk.Values.AsSpan(offset, chunk.ValueSize);
                ReadOnlySpan<byte> newValue = readBuffer.AsSpan(offset, chunk.ValueSize);
                bool equal = oldValue.SequenceEqual(newValue);
                int comparison = CompareValues(oldValue, newValue, previous.Filter.ValueKind);
                if (equal) unchanged++; else { changed++; if (comparison > 0) increased++; else if (comparison < 0) decreased++; }

                bool include = normalizedCompareMode switch
                {
                    "unchanged" => equal,
                    "increased" => comparison > 0,
                    "decreased" => comparison < 0,
                    _ => !equal,
                };
                if (!include) continue;
                currentCount++;
                if (advanceBaseline)
                {
                    nextMatches[index] = true;
                }

                if (candidates.Count >= cap) continue;
                byte[] observed = newValue.ToArray();
                MemoryScanCandidate candidate = new(
                    chunk.BaseAddress + offset,
                    chunk.BaseAddress + offset - baseAddress,
                    observed,
                    FormatValue(observed, previous.Filter.ValueKind),
                    chunk.AddressKind);
                candidates.Add(candidate);
            }

            if (advanceBaseline)
            {
                readBuffer.AsSpan(0, chunk.Values.Length).CopyTo(nextValues);
                nextChunks.Add(chunk with { Values = nextValues, Candidates = nextMatches });
            }
        }

        DateTimeOffset completed = _timeProvider.GetUtcNow();
        int effectiveCurrentCount = checked(currentCount + retainedCandidateCount);
        bool truncated = effectiveCurrentCount > candidates.Count;
        lock (_lock)
        {
            // A discard or another rolling compare may have replaced the
            // immutable snapshot while native reads were in progress. Never
            // report success or overwrite a newer baseline in that case.
            if (!_sessions.TryGetValue(sessionId, out Snapshot? current)
                || !ReferenceEquals(current, previous))
            {
                _logger.LogWarning(
                    "Memory snapshot comparison abandoned: sessionId={SessionId}, reason=session_changed",
                    sessionId);
                return Error<CompareResult>(
                    "discover.session_changed",
                    "The snapshot was discarded or changed while it was being compared.");
            }

            _sessions[sessionId] = advanceBaseline
                ? current with
                {
                    Chunks = nextChunks,
                    CandidateCount = effectiveCurrentCount,
                    LastCompareAtUtc = completed,
                    CompareCount = current.CompareCount + 1,
                }
                : current with
                {
                    LastCompareAtUtc = completed,
                    CompareCount = current.CompareCount + 1,
                };
        }

        _logger.LogInformation(
            "Memory snapshot comparison completed: sessionId={SessionId}, compareMode={CompareMode}, previousCount={PreviousCount}, currentCount={CurrentCount}, changed={Changed}, unchanged={Unchanged}, increased={Increased}, decreased={Decreased}, returnedCandidates={ReturnedCandidates}, truncated={Truncated}, retained={Retained}, readFailures={ReadFailures}, candidateSample={CandidateSample}, elapsedMs={ElapsedMs}, executablePath={ExecutablePath}, executableSha256={ExecutableSha256}",
            sessionId,
            normalizedCompareMode,
            previous.CandidateCount,
            effectiveCurrentCount,
            changed,
            unchanged,
            increased,
            decreased,
            candidates.Count,
            truncated,
            retainedCandidateCount,
            readFailureCount,
            FormatCandidateSample(candidates),
            (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds,
            observation.CanonicalExecutablePath,
            observation.ExecutableSha256.Value);

        return OperationResult.Success(new CompareResult(
            completed,
            previous.CandidateCount > int.MaxValue ? int.MaxValue : (int)previous.CandidateCount,
            effectiveCurrentCount,
            changed,
            unchanged,
            increased,
            decreased,
            candidates,
            truncated,
            advanceBaseline,
            retainedCandidateCount));
    }

    public void DiscardSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_lock) _sessions.Remove(sessionId);
        _logger.LogInformation("Memory snapshot session discarded: sessionId={SessionId}", sessionId);
    }

    public void DiscardAllSessions()
    {
        lock (_lock) _sessions.Clear();
        _logger.LogInformation("All memory snapshot sessions discarded");
    }

    private static string GetAddressKind(SafeProcessHandle handle, long address)
    {
        if (NativeMethods.VirtualQueryEx(
                handle,
                (nint)address,
                out MemoryBasicInformation information,
                (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
        {
            return "unknown";
        }

        return GetAddressKind(information.Type);
    }

    private static string GetAddressKind(uint type) =>
        (type & MemImage) != 0 ? "image-mapping"
        : (type & MemMapped) != 0 ? "mapped-mapping"
        : (type & MemPrivate) != 0 ? "private-mapping"
        : "unknown";

    private static bool SameIdentity(AuthorizedMemoryObservation left, AuthorizedMemoryObservation right) =>
        left.ProcessId == right.ProcessId
        && left.ProcessStartIdentity == right.ProcessStartIdentity
        && string.Equals(left.CanonicalExecutablePath, right.CanonicalExecutablePath, StringComparison.OrdinalIgnoreCase)
        && left.ExecutableSha256 == right.ExecutableSha256;

    internal static bool ValidateFilter(SnapshotFilter filter, out string? error)
    {
        ArgumentNullException.ThrowIfNull(filter);
        error = null;
        if (!Enum.IsDefined(filter.ValueKind)
            || filter.ValueSize is not (1 or 2 or 4 or 8)
            || filter.Alignment is not (1 or 2 or 4 or 8)
            || filter.MinAddress < 0
            || filter.MaxAddress < 0
            || (filter.MinAddress != 0
                && !IsSupportedUserAddress(filter.MinAddress))
            || (filter.MaxAddress != 0
                && !IsSupportedUserAddress(filter.MaxAddress))
            || (filter.MaxAddress > 0 && filter.MinAddress > filter.MaxAddress)
            || (filter.ValueKind is MemoryValueKind.Int64Value or MemoryValueKind.UInt64Value
                && filter.ValueSize != 8)
            || ((filter.ValueKind is MemoryValueKind.FloatValue
                or MemoryValueKind.Int32Value
                or MemoryValueKind.UInt32Value)
                && filter.ValueSize != 4)
            || (filter.ValueKind is MemoryValueKind.DoubleValue && filter.ValueSize != 8)
            || (filter.FloatMin.HasValue && filter.FloatMax.HasValue && filter.FloatMin.Value > filter.FloatMax.Value)
            || (filter.IntMin.HasValue && filter.IntMax.HasValue && filter.IntMin.Value > filter.IntMax.Value)
            || (filter.LongMin.HasValue && filter.LongMax.HasValue && filter.LongMin.Value > filter.LongMax.Value)
            || (filter.UIntMin.HasValue && filter.UIntMax.HasValue && filter.UIntMin.Value > filter.UIntMax.Value)
            || (filter.ValueKind == MemoryValueKind.UInt32Value
                && (filter.UIntMin > uint.MaxValue || filter.UIntMax > uint.MaxValue))
            || filter.RegionSelection == MemoryRegionSelection.None)
        {
            error = "Value size, alignment, address range, or region selection is invalid.";
            return false;
        }
        return true;
    }

    private static List<MemoryRegion> EnumerateRegions(
        SafeProcessHandle handle,
        SnapshotFilter filter,
        CancellationToken cancellationToken)
    {
        List<MemoryRegion> regions = [];
        long address = Math.Max(MinimumUserAddress, filter.MinAddress);
        nuint size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(handle, (nint)address, out MemoryBasicInformation mbi, size) == 0
                || mbi.RegionSize == 0) break;
            long baseAddress = mbi.BaseAddress.ToInt64();
            long length = checked((long)mbi.RegionSize);
            bool committed = (mbi.State & MemCommit) != 0;
            bool readable = (mbi.Protect & Readable) != 0 && (mbi.Protect & (PageNoAccess | PageGuard)) == 0;
            bool selected = (mbi.Type & MemPrivate) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Private)
                || (mbi.Type & MemMapped) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Mapped)
                || (mbi.Type & MemImage) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Image);
            long end = checked(baseAddress + length);
            long boundedEnd = Math.Min(end, MaximumUserAddress + 1);
            long min = Math.Max(
                baseAddress,
                Math.Max(MinimumUserAddress, filter.MinAddress));
            long max = filter.MaxAddress > 0 ? Math.Min(boundedEnd, filter.MaxAddress) : boundedEnd;
            if (committed && readable && selected && max > min)
                regions.Add(new MemoryRegion(min, max - min));
            if (end <= baseAddress
                || end < 0
                || end >= MaximumUserAddress + 1
                || (filter.MaxAddress > 0 && end >= filter.MaxAddress))
            {
                break;
            }

            address = end;
        }
        return regions;
    }

    internal static bool PassesFilter(ReadOnlySpan<byte> bytes, SnapshotFilter filter)
    {
        return filter.ValueKind switch
        {
            MemoryValueKind.FloatValue when bytes.Length == 4 => CompareFloat(BitConverter.ToSingle(bytes), filter.FloatMin, filter.FloatMax),
            MemoryValueKind.DoubleValue when bytes.Length == 8 => CompareDouble(BitConverter.ToDouble(bytes), filter.FloatMin, filter.FloatMax),
            MemoryValueKind.Int32Value when bytes.Length == 4 => CompareInt(BitConverter.ToInt32(bytes), filter.IntMin, filter.IntMax),
            MemoryValueKind.UInt32Value when bytes.Length == 4 => CompareUInt(BitConverter.ToUInt32(bytes), filter.UIntMin, filter.UIntMax),
            MemoryValueKind.Int64Value when bytes.Length == 8 => CompareLong(BitConverter.ToInt64(bytes), filter.LongMin, filter.LongMax),
            MemoryValueKind.UInt64Value when bytes.Length == 8 => CompareULong(BitConverter.ToUInt64(bytes), filter.UIntMin, filter.UIntMax),
            _ => true,
        };
    }

    private static int CompareValues(ReadOnlySpan<byte> oldValue, ReadOnlySpan<byte> newValue, MemoryValueKind kind) => kind switch
    {
        MemoryValueKind.FloatValue => BitConverter.ToSingle(oldValue).CompareTo(BitConverter.ToSingle(newValue)),
        MemoryValueKind.DoubleValue => BitConverter.ToDouble(oldValue).CompareTo(BitConverter.ToDouble(newValue)),
        MemoryValueKind.Int32Value => BitConverter.ToInt32(oldValue).CompareTo(BitConverter.ToInt32(newValue)),
        MemoryValueKind.UInt32Value => BitConverter.ToUInt32(oldValue).CompareTo(BitConverter.ToUInt32(newValue)),
        MemoryValueKind.Int64Value => BitConverter.ToInt64(oldValue).CompareTo(BitConverter.ToInt64(newValue)),
        MemoryValueKind.UInt64Value => BitConverter.ToUInt64(oldValue).CompareTo(BitConverter.ToUInt64(newValue)),
        _ => oldValue.SequenceCompareTo(newValue),
    };

    private static string FormatValue(byte[] bytes, MemoryValueKind kind) => kind switch
    {
        MemoryValueKind.FloatValue => BitConverter.ToSingle(bytes).ToString("F3", CultureInfo.InvariantCulture),
        MemoryValueKind.DoubleValue => BitConverter.ToDouble(bytes).ToString("F6", CultureInfo.InvariantCulture),
        MemoryValueKind.Int32Value => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.UInt32Value => BitConverter.ToUInt32(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.Int64Value => BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.UInt64Value => BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(bytes),
    };

    private static bool CompareFloat(float value, float? min, float? max) => !float.IsNaN(value) && !float.IsInfinity(value) && (!min.HasValue || value >= min) && (!max.HasValue || value <= max);
    private static bool CompareDouble(double value, float? min, float? max) => !double.IsNaN(value) && !double.IsInfinity(value) && (!min.HasValue || value >= min) && (!max.HasValue || value <= max);
    private static bool CompareInt(int value, int? min, int? max) => (!min.HasValue || value >= min) && (!max.HasValue || value <= max);
    private static bool CompareUInt(uint value, ulong? min, ulong? max) => (!min.HasValue || value >= min.Value) && (!max.HasValue || value <= max.Value);
    private static bool CompareLong(long value, long? min, long? max) => (!min.HasValue || value >= min.Value) && (!max.HasValue || value <= max.Value);
    private static bool CompareULong(ulong value, ulong? min, ulong? max) => (!min.HasValue || value >= min.Value) && (!max.HasValue || value <= max.Value);

    private static string FormatCandidateSample(IReadOnlyList<MemoryScanCandidate> candidates) =>
        string.Join(
            "; ",
            candidates.Take(5).Select(candidate =>
                $"0x{candidate.AbsoluteAddress:X}/0x{candidate.BaseDisplacement:X}:{candidate.ValueSummary}:[{Convert.ToHexString(candidate.ObservedValue)}]"));

    private static string FormatSnapshotCandidateSample(
        IReadOnlyList<SnapshotChunk> chunks,
        int valueSize,
        MemoryValueKind valueKind,
        long baseAddress) =>
        string.Join(
            "; ",
            chunks.SelectMany(chunk => Enumerable.Range(0, chunk.Candidates.Length)
                .Where(index => chunk.Candidates[index])
                .Take(5)
                .Select(index =>
                {
                    int offset = index * valueSize;
                    byte[] observed = chunk.Values.AsSpan(offset, valueSize).ToArray();
                    long address = chunk.BaseAddress + offset;
                    return $"0x{address:X}/0x{address - baseAddress:X}:{FormatValue(observed, valueKind)}:[{Convert.ToHexString(observed)}]";
                }))
                .Take(5));

    internal static bool IsAligned(long value, int alignment) => alignment <= 1 || value % alignment == 0;

    internal static bool IsSupportedUserAddress(long address) =>
        address is >= MinimumUserAddress and <= MaximumUserAddress;

    internal static bool IsSnapshotBaseCompatible(Snapshot snapshot, long baseAddress) =>
        snapshot.ModuleBaseAddress == baseAddress;

    private void ExpireSessionsLocked()
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - SessionLifetime;
        foreach (string id in _sessions.Values.Where(s => s.CreatedAtUtc < cutoff).Select(s => s.SessionId).ToArray())
            _sessions.Remove(id);
    }

    private static OperationResult<T> Error<T>(string code, string message) where T : class =>
        OperationResult.Failure<T>(new ApplicationError(code, message));

    private readonly record struct MemoryRegion(long BaseAddress, long Length);
}
