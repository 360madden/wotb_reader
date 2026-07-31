using System.Globalization;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;

namespace WotBTreader.GameIntegration.Session;

#pragma warning disable CA1873 // Log arguments are value types, not expensive

/// <summary>
/// Cheat Engine-like multi-scan memory engine. Takes snapshots of all
/// float/int values across committed regions, then compares scans to filter
/// by changed/unchanged/increased/decreased values. This is the core algorithm
/// for narrowing millions of memory addresses to 1-5 candidate offsets.
/// All operations log phase transitions with timestamps via ILogger.
/// </summary>
internal sealed class MemoryScanEngine
{
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;
    private const uint PageReadonly = 0x02;
    private const uint PageReadwrite = 0x04;
    private const uint PageWritecopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadwrite = 0x40;
    private const int ReadChunkSize = 65_536;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemoryScanEngine> _logger;
    private readonly Dictionary<string, Snapshot> _sessions = new();
    private readonly Lock _lock = new();
    private int _sessionCounter;

    public MemoryScanEngine(TimeProvider timeProvider, ILogger<MemoryScanEngine> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public sealed record Snapshot(
        string SessionId,
        DateTimeOffset CreatedAtUtc,
        SnapshotFilter Filter,
        Dictionary<long, byte[]> AllAddresses,
        DateTimeOffset? LastCompareAtUtc,
        int CompareCount);

    public sealed record SnapshotFilter(
        int ValueSize,
        long MinAddress,
        long MaxAddress,
        float? FloatMin,
        float? FloatMax,
        int? IntMin,
        int? IntMax,
        bool IsFloatFilter);

    public sealed record CompareResult(
        DateTimeOffset CompletedAtUtc,
        int PreviousCount,
        int CurrentCount,
        int ChangedCount,
        int UnchangedCount,
        int IncreasedCount,
        int DecreasedCount,
        IReadOnlyList<MemoryScanCandidate> Candidates);

    public OperationResult<string> CreateSnapshot(
        int processId,
        long baseAddress,
        SnapshotFilter filter,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sessionId = Interlocked.Increment(ref _sessionCounter)
            .ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset start = _timeProvider.GetUtcNow();
        _logger.LogInformation("[{Sid}] Snapshot START pid={Pid} base=0x{Base:X} sz={Sz} float=[{Fmin},{Fmax}] int=[{Imin},{Imax}]",
            sessionId, processId, baseAddress, filter.ValueSize,
            filter.FloatMin, filter.FloatMax, filter.IntMin, filter.IntMax);

        var handle = OpenProcess(processId);
        if (handle is null || handle.IsInvalid)
        {
            _logger.LogError("[{Sid}] Snapshot FAILED — cannot open pid={Pid}", sessionId, processId);
            return Error<string>("discover.snapshot.open_failed", "Could not open process for snapshot.");
        }

        try
        {
            var regions = EnumerateRegions(
                handle, filter.MinAddress, filter.MaxAddress, cancellationToken);
            _logger.LogInformation("[{Sid}] Enumerated {Count} region(s)", sessionId, regions.Count);

            var addresses = new Dictionary<long, byte[]>();
            byte[] chunk = new byte[ReadChunkSize];
            long bytesScanned = 0;

            foreach (var region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining = region.Size;
                long addr = region.Base;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = remaining >= ReadChunkSize ? ReadChunkSize : (int)remaining;
                    var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
                        chunk, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        if (!NativeMethods.ReadProcessMemory(handle, (nint)addr,
                                pinned.AddrOfPinnedObject(), (nuint)toRead, out nuint read)
                            || read == 0) { goto nextChunk; }

                        bytesScanned += (long)read;
                        int scanned = (int)read;

                        for (int i = 0; i <= scanned - filter.ValueSize; i += filter.ValueSize)
                        {
                            if ((i & 1023) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            long absAddr = addr + i;
                            if (!PassesFilter(chunk, i, filter)) continue;
                            byte[] value = new byte[filter.ValueSize];
                            Array.Copy(chunk, i, value, 0, filter.ValueSize);
                            addresses[absAddr] = value;
                        }
                    }
                    finally { pinned.Free(); }

                nextChunk:
                    addr += ReadChunkSize;
                    remaining -= ReadChunkSize;
                }
            }

            DateTimeOffset end = _timeProvider.GetUtcNow();
            double elapsed = (end - start).TotalSeconds;
            var session = new Snapshot(sessionId, end, filter, addresses, null, 0);
            lock (_lock) { _sessions[sessionId] = session; }

            _logger.LogInformation("[{Sid}] Snapshot DONE — {Count} addresses, {Bytes} bytes, {Elapsed:F1}s",
                sessionId, addresses.Count, bytesScanned, elapsed);
            return OperationResult.Success(sessionId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Sid}] Snapshot ERROR: {Msg}", sessionId, ex.Message);
            return Error<string>("discover.snapshot.error", $"Snapshot failed: {ex.GetType().Name}");
        }
        finally { handle.Dispose(); }
    }

    public OperationResult<CompareResult> Compare(
        int processId,
        long baseAddress,
        string sessionId,
        string compareMode,
        int maxCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot previous;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out previous!))
                return Error<CompareResult>("discover.session_not_found", $"Session {sessionId} not found.");
        }

        DateTimeOffset startTime = _timeProvider.GetUtcNow();
        _logger.LogInformation("[{Sid}] Compare START mode={Mode} max={Max}", sessionId, compareMode, maxCandidates);

        var handle = OpenProcess(processId);
        if (handle is null || handle.IsInvalid)
        {
            _logger.LogError("[{Sid}] Compare FAILED — cannot open process", sessionId);
            return Error<CompareResult>("discover.compare.open_failed", "Could not open process for compare.");
        }

        try
        {
            var regions = EnumerateRegions(
                handle, previous.Filter.MinAddress, previous.Filter.MaxAddress, cancellationToken);
            var filter = previous.Filter;
            var candidates = new List<MemoryScanCandidate>();
            int changed = 0, unchanged = 0, increased = 0, decreased = 0;
            byte[] chunk = new byte[ReadChunkSize];

            foreach (var region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= maxCandidates) break;
                long remaining = region.Size;
                long addr = region.Base;

                while (remaining > 0 && candidates.Count < maxCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = remaining >= ReadChunkSize ? ReadChunkSize : (int)remaining;
                    var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
                        chunk, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try
                    {
                        if (!NativeMethods.ReadProcessMemory(handle, (nint)addr,
                                pinned.AddrOfPinnedObject(), (nuint)toRead, out nuint read)
                            || read == 0) { goto nextChunk; }

                        int scanned = (int)read;
                        for (int i = 0; i <= scanned - filter.ValueSize; i += filter.ValueSize)
                        {
                            if ((i & 1023) == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            long absAddr = addr + i;
                            if (!previous.AllAddresses.TryGetValue(absAddr, out byte[]? oldValue))
                                continue;

                            byte[] newValue = new byte[filter.ValueSize];
                            Array.Copy(chunk, i, newValue, 0, filter.ValueSize);
                            bool areEqual = oldValue.AsSpan().SequenceEqual(newValue);
                            int cmp = CompareValues(oldValue, newValue, filter.ValueSize);

                            if (areEqual) unchanged++; else { changed++; if (cmp > 0) increased++; else if (cmp < 0) decreased++; }

                            bool include = compareMode switch
                            {
                                "changed" => !areEqual,
                                "unchanged" => areEqual,
                                "increased" => cmp > 0,
                                "decreased" => cmp < 0,
                                _ => !areEqual,
                            };

                            if (include)
                            {
                                string summary = FormatValue(newValue, filter);
                                candidates.Add(new MemoryScanCandidate(absAddr,
                                    absAddr - baseAddress, newValue, summary));
                                if (candidates.Count >= maxCandidates) break;
                            }
                        }
                    }
                    finally { pinned.Free(); }

                nextChunk:
                    addr += ReadChunkSize;
                    remaining -= ReadChunkSize;
                }
            }

            DateTimeOffset endTime = _timeProvider.GetUtcNow();
            double elapsed = (endTime - startTime).TotalSeconds;

            // Update compare metadata on stored session (keep full snapshot intact).
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var current))
                    _sessions[sessionId] = current with
                    {
                        LastCompareAtUtc = endTime,
                        CompareCount = current.CompareCount + 1,
                    };
            }

            _logger.LogInformation("[{Sid}] Compare DONE chg={Chg} un={Un} inc={Inc} dec={Dec} cand={Cand} {Elapsed:F1}s",
                sessionId, changed, unchanged, increased, decreased, candidates.Count, elapsed);

            return OperationResult.Success(new CompareResult(
                endTime, previous.AllAddresses.Count, 0, changed, unchanged,
                increased, decreased, candidates));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[{Sid}] Compare ERROR: {Msg}", sessionId, ex.Message);
            return Error<CompareResult>("discover.compare.error", $"Compare failed: {ex.GetType().Name}");
        }
        finally { handle.Dispose(); }
    }

    public void DiscardSession(string sessionId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(sessionId))
                _logger.LogInformation("[{Sid}] Session discarded", sessionId);
            else
                _logger.LogInformation("[{Sid}] Session not found for discard", sessionId);
        }
    }

    // ── Helpers ─────────────────────────────────────────────

    private static Microsoft.Win32.SafeHandles.SafeProcessHandle? OpenProcess(int pid)
    {
        const uint VmRead = 0x0010;
        const uint QueryInfo = 0x0400;
        var h = NativeMethods.OpenProcess(VmRead | QueryInfo, false, checked((uint)pid));
        return h.IsInvalid ? null : h;
    }

    private static List<(long Base, long Size)> EnumerateRegions(
        Microsoft.Win32.SafeHandles.SafeProcessHandle handle,
        long minAddr,
        long maxAddr,
        CancellationToken cancellationToken)
    {
        var regions = new List<(long, long)>();
        long address = minAddr > 0 ? minAddr : 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.VirtualQueryEx(handle, (nint)address,
                    out MemoryBasicInformation mbi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryBasicInformation>()) == 0)
                break;

            bool isCommitted = (mbi.State & MemCommit) != 0;
            bool isImage = (mbi.Type & MemImage) != 0;
            bool isReadable = (mbi.Protect & (PageReadonly | PageReadwrite |
                PageWritecopy | PageExecuteRead | PageExecuteReadwrite)) != 0;

            if (isCommitted && !isImage && isReadable && mbi.RegionSize > 0)
            {
                long regionEnd = mbi.BaseAddress + checked((long)mbi.RegionSize);
                if (maxAddr <= 0 || mbi.BaseAddress < (nint)maxAddr)
                    regions.Add((mbi.BaseAddress.ToInt64(), checked((long)mbi.RegionSize)));
            }

            address = mbi.BaseAddress + checked((long)mbi.RegionSize);
            if (address < 0 || (maxAddr > 0 && address >= maxAddr)) break;
        }

        return regions;
    }

    private static bool PassesFilter(byte[] buffer, int offset, SnapshotFilter filter)
    {
        if (filter.ValueSize != 4) return true;
        if (filter.IsFloatFilter)
            return CheckFloat(BitConverter.ToSingle(buffer, offset), filter.FloatMin, filter.FloatMax);
        return CheckInt(BitConverter.ToInt32(buffer, offset), filter.IntMin, filter.IntMax);
    }

    private static bool CheckFloat(float v, float? min, float? max) =>
        (!min.HasValue || v >= min.Value) && (!max.HasValue || v <= max.Value);

    private static bool CheckInt(int v, int? min, int? max) =>
        (!min.HasValue || v >= min.Value) && (!max.HasValue || v <= max.Value);

    private static int CompareValues(byte[] a, byte[] b, int size) => size switch
    {
        4 => BitConverter.ToSingle(a).CompareTo(BitConverter.ToSingle(b)),
        8 => BitConverter.ToDouble(a).CompareTo(BitConverter.ToDouble(b)),
        _ => 0,
    };

    private static string FormatValue(byte[] bytes, SnapshotFilter filter) => filter.ValueSize switch
    {
        4 => filter.IsFloatFilter
            ? BitConverter.ToSingle(bytes).ToString("F3", CultureInfo.InvariantCulture)
            : BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
        8 => BitConverter.ToDouble(bytes).ToString("F6", CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(bytes),
    };

    private static OperationResult<T> Error<T>(string code, string msg)
        where T : class =>
        OperationResult.Failure<T>(new ApplicationError(code, msg));
}
