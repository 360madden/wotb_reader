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
        MemoryRegionSelection RegionSelection,
        long MaxBytes = 0);

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

        // A caller may request a tighter retained-byte budget than the fixed
        // engine ceiling, so an unbounded private/mapped snapshot cannot
        // silently exceed the operator's privacy-safe limit. The budget check
        // precedes each whole-chunk read and soft-caps the snapshot when the
        // next chunk would exceed it (partial success instead of size_limit).
        long snapshotByteBudget = ResolveSnapshotByteBudget(filter.MaxBytes);

        string sessionId = Interlocked.Increment(ref _sessionCounter)
            .ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        _logger.LogInformation(
            "Memory snapshot started: sessionId={SessionId}, valueKind={ValueKind}, valueSize={ValueSize}, alignment={Alignment}, regionSelection={RegionSelection}, processId={ProcessId}, processStartIdentity={ProcessStartIdentity}, productVersion={ProductVersion}, executableSha256={ExecutableSha256}",
            sessionId,
            filter.ValueKind,
            filter.ValueSize,
            filter.Alignment,
            filter.RegionSelection,
            observation.ProcessId,
            observation.ProcessStartIdentity,
            observation.ProductVersion,
            observation.ExecutableSha256.Value);

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider, cancellationToken);
        if (lease is null)
        {
            _logger.LogWarning(
                "Memory snapshot denied: processId={ProcessId}, reason=identity_mismatch",
                observation.ProcessId);
            return Error<string>("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        if (!ValidateTargetAddressRange(
                baseAddress,
                filter,
                lease.MaximumUserAddress,
                out error))
        {
            return Error<string>("discover.snapshot.invalid_options", error!);
        }

        List<MemoryRegion> regions = EnumerateRegions(
            lease,
            baseAddress,
            filter,
            cancellationToken);
        List<SnapshotChunk> chunks = [];
        long candidateCount = 0;
        long storedBytes = 0;
        int readFailureCount = 0;
        byte[] readBuffer = GC.AllocateUninitializedArray<byte>(ReadChunkSize);
        bool budgetExhausted = false;

        foreach (MemoryRegion region in regions)
        {
            if (budgetExhausted)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Memory snapshot cancelled: sessionId={SessionId}, storedBytes={StoredBytes}, candidates={Candidates}, elapsedMs={ElapsedMs}",
                    sessionId,
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
                        "Memory snapshot cancelled: sessionId={SessionId}, storedBytes={StoredBytes}, candidates={Candidates}, elapsedMs={ElapsedMs}",
                        sessionId,
                        storedBytes,
                        candidateCount,
                        (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                int length = (int)Math.Min(ReadChunkSize, region.Length - offset);
                if (storedBytes + length > snapshotByteBudget)
                {
                    // Privacy-safe soft cap: stop retaining further readable
                    // chunks and return the partial snapshot instead of failing
                    // the whole request. Callers that need a hard failure can
                    // still reject oversized ranges via MinAddress/MaxAddress.
                    budgetExhausted = true;
                    break;
                }

                long address = checked(region.BaseAddress + offset);
                if (!lease.TryRead((nint)address, readBuffer, 0, length, cancellationToken, out nuint read)
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
            "Memory snapshot completed: sessionId={SessionId}, valueKind={ValueKind}, regions={Regions}, candidates={Candidates}, bytes={Bytes}, readFailures={ReadFailures}, elapsedMs={ElapsedMs}, executableSha256={ExecutableSha256}",
            sessionId,
            filter.ValueKind,
            regions.Count,
            candidateCount,
            storedBytes,
            readFailureCount,
            (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds,
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
        double? deltaTarget = null,
        double? deltaTolerance = null,
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

        string normalizedCompareMode = compareMode?.Trim().ToLowerInvariant() ?? string.Empty;
        bool isDeltaMode = normalizedCompareMode == "delta";
        bool isExactMode = normalizedCompareMode == "exact";
        if (normalizedCompareMode is not ("changed" or "unchanged" or "increased" or "decreased" or "delta" or "exact")
            || maxCandidates is < 1 or > 10_000
            || ((isDeltaMode || isExactMode)
                && (!deltaTarget.HasValue || !deltaTolerance.HasValue
                    || !double.IsFinite(deltaTarget.Value)
                    || !double.IsFinite(deltaTolerance.Value)
                    || deltaTolerance.Value < 0))
            || (!isDeltaMode && !isExactMode && (deltaTarget.HasValue || deltaTolerance.HasValue)))
        {
            return Error<CompareResult>(
                "discover.invalid_options",
                "Compare mode must be changed, unchanged, increased, decreased, delta, or exact, and maxCandidates must be between 1 and 10000. Delta and exact modes require a finite target and a non-negative finite tolerance; other modes reject those parameters.");
        }

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        _logger.LogInformation(
            "Memory snapshot comparison started: sessionId={SessionId}, compareMode={CompareMode}, maxCandidates={MaxCandidates}, advanceBaseline={AdvanceBaseline}, deltaTarget={DeltaTarget}, deltaTolerance={DeltaTolerance}, processId={ProcessId}, executableSha256={ExecutableSha256}",
            sessionId,
            compareMode,
            maxCandidates,
            advanceBaseline,
            deltaTarget,
            deltaTolerance,
            observation.ProcessId,
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
        if (isDeltaMode && previous.Filter.ValueKind == MemoryValueKind.Bytes)
        {
            // Delta semantics require a numeric value kind; a Bytes snapshot
            // has no meaningful numeric difference. Reject instead of silently
            // returning zero candidates.
            return Error<CompareResult>(
                "discover.invalid_options",
                "Delta compare requires a numeric snapshot value kind (Float, Double, Int32, UInt32, Int64, or UInt64); Bytes snapshots have no numeric delta.");
        }
        if (!SameIdentity(previous.Observation, observation))
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, reason=identity_mismatch, processId={ProcessId}",
                sessionId,
                observation.ProcessId);
            return Error<CompareResult>("discover.identity_mismatch", "The snapshot belongs to a different process identity.");
        }
        if (!IsSnapshotBaseCompatible(previous, baseAddress))
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, reason=base_mismatch",
                sessionId);
            return Error<CompareResult>(
                "discover.base_mismatch",
                "The comparison base address does not match the snapshot.");
        }

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider, cancellationToken);
        if (lease is null)
        {
            _logger.LogWarning(
                "Memory snapshot comparison denied: sessionId={SessionId}, processId={ProcessId}, reason=identity_mismatch",
                sessionId,
                observation.ProcessId);
            return Error<CompareResult>("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        int cap = maxCandidates;
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
            if (!lease.TryRead((nint)chunk.BaseAddress, readBuffer, 0, chunk.Length, cancellationToken, out nuint read)
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
                    "delta" => PassesDelta(
                        oldValue, newValue, previous.Filter.ValueKind,
                        deltaTarget!.Value, deltaTolerance!.Value),
                    // Exact-value pause scan: the replay is paused at a known
                    // decoded value (e.g. replayTime = 60.000s), so keep
                    // candidates whose CURRENT value matches the absolute
                    // target. The previous snapshot value is irrelevant — a
                    // frozen field reads equal anyway. This is the strongest
                    // filter the campaign has: tickers cannot contaminate a
                    // paused frame.
                    "exact" => PassesExact(
                        newValue, previous.Filter.ValueKind,
                        deltaTarget!.Value, deltaTolerance!.Value),
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
            "Memory snapshot comparison completed: sessionId={SessionId}, compareMode={CompareMode}, deltaTarget={DeltaTarget}, deltaTolerance={DeltaTolerance}, previousCount={PreviousCount}, currentCount={CurrentCount}, changed={Changed}, unchanged={Unchanged}, increased={Increased}, decreased={Decreased}, returnedCandidates={ReturnedCandidates}, truncated={Truncated}, retained={Retained}, readFailures={ReadFailures}, elapsedMs={ElapsedMs}, executableSha256={ExecutableSha256}",
            sessionId,
            normalizedCompareMode,
            deltaTarget,
            deltaTolerance,
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
            (_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds,
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
        left.Generation == right.Generation
        && left.ProcessId == right.ProcessId
        && left.ProcessStartIdentity == right.ProcessStartIdentity
        && string.Equals(left.CanonicalExecutablePath, right.CanonicalExecutablePath, StringComparison.OrdinalIgnoreCase)
        && left.ExecutableSha256 == right.ExecutableSha256;

    /// <summary>
    /// Resolves the effective retained-byte budget: an explicit positive
    /// caller budget is honored but never exceeds the fixed engine ceiling;
    /// zero means the ceiling. Negative values are rejected by
    /// <see cref="ValidateFilter"/> before this is reached.
    /// </summary>
    internal static long ResolveSnapshotByteBudget(long maxBytes) =>
        maxBytes > 0 ? Math.Min(maxBytes, MaximumSnapshotBytes) : MaximumSnapshotBytes;

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
            || (filter.MaxAddress > 0 && filter.MinAddress >= filter.MaxAddress)
            || (filter.ValueKind is MemoryValueKind.Int64Value or MemoryValueKind.UInt64Value
                && filter.ValueSize != 8)
            || ((filter.ValueKind is MemoryValueKind.FloatValue
                or MemoryValueKind.Int32Value
                or MemoryValueKind.UInt32Value)
                && filter.ValueSize != 4)
            || (filter.ValueKind is MemoryValueKind.DoubleValue && filter.ValueSize != 8)
            || (filter.FloatMin.HasValue && !float.IsFinite(filter.FloatMin.Value))
            || (filter.FloatMax.HasValue && !float.IsFinite(filter.FloatMax.Value))
            || (filter.FloatMin.HasValue && filter.FloatMax.HasValue && filter.FloatMin.Value > filter.FloatMax.Value)
            || (filter.IntMin.HasValue && filter.IntMax.HasValue && filter.IntMin.Value > filter.IntMax.Value)
            || (filter.LongMin.HasValue && filter.LongMax.HasValue && filter.LongMin.Value > filter.LongMax.Value)
            || (filter.UIntMin.HasValue && filter.UIntMax.HasValue && filter.UIntMin.Value > filter.UIntMax.Value)
            || (filter.ValueKind == MemoryValueKind.UInt32Value
                && (filter.UIntMin > uint.MaxValue || filter.UIntMax > uint.MaxValue))
            || filter.RegionSelection == MemoryRegionSelection.None
            || filter.MaxBytes < 0
            || filter.MaxBytes > MaximumSnapshotBytes)
        {
            error = "Value size, alignment, address range, region selection, or byte budget is invalid. The explicit byte budget must be between 0 and " + MaximumSnapshotBytes + " bytes; a nonzero maximum address is exclusive and must be greater than the minimum.";
            return false;
        }
        return true;
    }

    internal static bool ValidateTargetAddressRange(
        long baseAddress,
        SnapshotFilter filter,
        long maximumUserAddress,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(filter);
        error = null;
        if (maximumUserAddress < MinimumUserAddress)
        {
            error = "The target process address space is unsupported.";
            return false;
        }

        long exclusiveMaximum = checked(maximumUserAddress + 1);
        long minimum = filter.MinAddress == 0
            ? MinimumUserAddress
            : filter.MinAddress;
        long maximum = filter.MaxAddress == 0
            ? exclusiveMaximum
            : filter.MaxAddress;
        if (baseAddress < MinimumUserAddress
            || baseAddress > maximumUserAddress
            || minimum < MinimumUserAddress
            || minimum > maximumUserAddress
            || maximum <= minimum
            || maximum > exclusiveMaximum
            || maximum - minimum < filter.ValueSize)
        {
            error = "The requested range is outside the target process address space or cannot contain one complete value.";
            return false;
        }

        return true;
    }

    private static List<MemoryRegion> EnumerateRegions(
        AuthorizedProcessLease lease,
        long moduleBaseAddress,
        SnapshotFilter filter,
        CancellationToken cancellationToken)
    {
        List<MemoryRegion> regions = [];
        long address = Math.Max(MinimumUserAddress, filter.MinAddress);
        nuint size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(lease.Handle, (nint)address, out MemoryBasicInformation mbi, size) == 0
                || mbi.RegionSize == 0) break;
            long baseAddress = mbi.BaseAddress.ToInt64();
            long length = checked((long)mbi.RegionSize);
            bool committed = (mbi.State & MemCommit) != 0;
            bool readable = (mbi.Protect & Readable) != 0 && (mbi.Protect & (PageNoAccess | PageGuard)) == 0;
            bool selected = (mbi.Type & MemPrivate) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Private)
                || (mbi.Type & MemMapped) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Mapped)
                || (mbi.Type & MemImage) != 0 && filter.RegionSelection.HasFlag(MemoryRegionSelection.Image);
            long end = checked(baseAddress + length);
            long boundedEnd = Math.Min(end, lease.MaximumUserAddress + 1);
            long min = Math.Max(
                baseAddress,
                Math.Max(MinimumUserAddress, filter.MinAddress));
            long max = filter.MaxAddress > 0 ? Math.Min(boundedEnd, filter.MaxAddress) : boundedEnd;
            min = AlignAddressUp(min, moduleBaseAddress, filter.Alignment);
            if (committed && readable && selected && max > min)
                regions.Add(new MemoryRegion(min, max - min));
            if (end <= baseAddress
                || end < 0
                || end >= lease.MaximumUserAddress + 1
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

    /// <summary>
    /// True when the value decodes to a number within <paramref name="tolerance"/>
    /// of the absolute <paramref name="target"/>. This is the exact-value
    /// pause-scan primitive: with the replay paused at a known decoded value
    /// (replayTime at a given frame, a position, an HP after a damage event),
    /// the true field must equal the target exactly, so tickers and decoys are
    /// excluded by construction.
    /// </summary>
    internal static bool PassesExact(
        ReadOnlySpan<byte> value,
        MemoryValueKind kind,
        double target,
        double tolerance)
    {
        if (!TryDecodeNumber(value, kind, out double number))
        {
            return false;
        }

        return Math.Abs(number - target) <= tolerance;
    }

    /// <summary>
    /// True when the numeric difference between the two values is within
    /// <paramref name="tolerance"/> of <paramref name="target"/>. This is the
    /// replay-marker delta primitive: keep candidates whose observed change
    /// matches a known replay-derived delta (position, speed, or HP between two
    /// frames), which is far more selective than the four boolean modes.
    /// </summary>
    internal static bool PassesDelta(
        ReadOnlySpan<byte> oldValue,
        ReadOnlySpan<byte> newValue,
        MemoryValueKind kind,
        double target,
        double tolerance)
    {
        if (oldValue.Length != newValue.Length
            || !TryDecodeNumber(oldValue, kind, out double oldNumber)
            || !TryDecodeNumber(newValue, kind, out double newNumber))
        {
            return false;
        }

        return Math.Abs((newNumber - oldNumber) - target) <= tolerance;
    }

    // Shared kind/length -> double decode for the numeric filter primitives.
    // UInt64 values lose sub-tick precision above 2^53 (e.g. a FILETIME clock
    // ~1.3e18); fine for the Double/Int/Float campaign kinds.
    private static bool TryDecodeNumber(
        ReadOnlySpan<byte> value,
        MemoryValueKind kind,
        out double number)
    {
        switch (kind)
        {
            case MemoryValueKind.FloatValue when value.Length == 4:
                number = BitConverter.ToSingle(value);
                return true;
            case MemoryValueKind.DoubleValue when value.Length == 8:
                number = BitConverter.ToDouble(value);
                return true;
            case MemoryValueKind.Int32Value when value.Length == 4:
                number = BitConverter.ToInt32(value);
                return true;
            case MemoryValueKind.UInt32Value when value.Length == 4:
                number = BitConverter.ToUInt32(value);
                return true;
            case MemoryValueKind.Int64Value when value.Length == 8:
                number = BitConverter.ToInt64(value);
                return true;
            case MemoryValueKind.UInt64Value when value.Length == 8:
                number = BitConverter.ToUInt64(value);
                return true;
            default:
                number = double.NaN;
                return false;
        }
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

    internal static bool IsAligned(long value, int alignment) => alignment <= 1 || value % alignment == 0;

    internal static long AlignAddressUp(long address, long origin, int alignment)
    {
        if (alignment <= 1)
        {
            return address;
        }

        long remainder = (address - origin) % alignment;
        if (remainder < 0)
        {
            remainder += alignment;
        }

        if (remainder == 0)
        {
            return address;
        }

        long increment = alignment - remainder;
        return address > long.MaxValue - increment
            ? long.MaxValue
            : address + increment;
    }

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
