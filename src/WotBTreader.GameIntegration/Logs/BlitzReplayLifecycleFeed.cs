using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Logs;

/// <summary>
/// Owns one process-lifetime, bounded journal of native-log lifecycle evidence.
/// File-system notifications are hints only; reconciliation remains authoritative.
/// </summary>
internal sealed class BlitzReplayLifecycleFeed : IBlitzReplayLifecycleFeed, IAsyncDisposable, IDisposable
{
    private const string LogPattern = "blitz-logs_*.txt";

    private readonly GameIntegrationOptions _options;
    private readonly IBlitzReplayLifecycleParser _parser;
    private readonly ILogger<BlitzReplayLifecycleFeed> _logger;
    private readonly LifecycleEventJournal _journal;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _startGate = new();
    private readonly Lock _barrierGate = new();
    private readonly List<BarrierRequest> _barriers = [];
    private readonly CancellationTokenSource _stop = new();
    private ChannelWriter<bool>? _wakeupWriter;
    private long _barrierGeneration;
    private bool _stopping;
    private bool _producerStopped;
    private Task? _producer;

    public BlitzReplayLifecycleFeed(
        GameIntegrationOptions options,
        IBlitzReplayLifecycleParser parser,
        TimeProvider timeProvider,
        ILogger<BlitzReplayLifecycleFeed> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _parser = parser;
        _logger = logger;
        _journal = new LifecycleEventJournal(options.LogEventChannelCapacity, timeProvider);
    }

    public async ValueTask<LifecycleFeedBaseline> CaptureBaselineAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _journal.CaptureBaseline();
    }

    public async ValueTask<LifecycleFeedBaseline> CaptureReconciledBaselineAsync(
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<LifecycleFeedBaseline> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        ChannelWriter<bool>? wakeupWriter;
        lock (_barrierGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);

            if (_producerStopped)
            {
                LifecycleFeedBaseline stoppedBaseline = _journal.CaptureBaseline();
                ObjectDisposedException.ThrowIf(
                    stoppedBaseline.Health == LifecycleFeedHealth.Healthy,
                    this);
                return stoppedBaseline;
            }

            long generation = checked(++_barrierGeneration);
            _barriers.Add(new BarrierRequest(generation, completion));
            wakeupWriter = _wakeupWriter;
        }

        _ = wakeupWriter?.TryWrite(true);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<LifecycleFeedReadResult> ReadAfterAsync(
        long afterSequence,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // ReadAfter captures events and health under the journal lock, so a
        // degradation cannot race between the event snapshot and health check.
        return _journal.ReadAfter(afterSequence);
    }

    private void EnsureStarted()
    {
        lock (_startGate)
        {
            _producer ??= Task.Run(ProduceAsync);
        }
    }

    private async Task ProduceAsync()
    {
        Channel<bool> wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        Dictionary<string, TailState> states = new(StringComparer.OrdinalIgnoreCase);
        List<FileSystemWatcher> watchers = [];

        try
        {
            lock (_barrierGate)
            {
                _wakeupWriter = wakeups.Writer;
            }

            _ = await ReconcileAsync(
                states,
                _journal.CaptureBaseline(),
                LifecycleFeedReason.InitialReconciliationCompleted).ConfigureAwait(false);
            watchers = CreateWatchers(wakeups.Writer);
            _ready.TrySetResult();

            while (!_stop.IsCancellationRequested)
            {
                using CancellationTokenSource delay =
                    CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                delay.CancelAfter(_options.LogReconciliationInterval);
                try
                {
                    await wakeups.Reader.ReadAsync(delay.Token).ConfigureAwait(false);
                    while (wakeups.Reader.TryRead(out _))
                    {
                    }
                }
                catch (OperationCanceledException) when (delay.IsCancellationRequested)
                {
                    // Periodic reconciliation also detects lost watcher notifications.
                }

                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                long barrierCutoff = CaptureBarrierCutoff();
                bool reconciled = await ReconcileAsync(
                    states,
                    _journal.CaptureBaseline(),
                    LifecycleFeedReason.ReconciliationCompleted).ConfigureAwait(false);
                if (reconciled && barrierCutoff > 0)
                {
                    foreach (TailState state in states.Values)
                    {
                        state.MarkPendingAsHistorical();
                    }
                }

                CompleteBarriersThrough(
                    barrierCutoff,
                    _journal.CaptureBaseline());
            }
        }
        catch (Exception exception)
        {
            _journal.RecordFault(LifecycleFeedReason.ProducerFault);
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(
                    new EventId(3130, "LifecycleFeedProducerFault"),
                    "The native-log lifecycle producer stopped unexpectedly ({ExceptionType}).",
                    exception.GetType().Name);
            }
        }
        finally
        {
            wakeups.Writer.TryComplete();
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }

            CompleteAllBarriersAndStop(_journal.CaptureBaseline());
            _ready.TrySetResult();
        }
    }

    private long CaptureBarrierCutoff()
    {
        lock (_barrierGate)
        {
            return _barrierGeneration;
        }
    }

    private void CompleteBarriersThrough(
        long barrierCutoff,
        LifecycleFeedBaseline baseline)
    {
        if (barrierCutoff == 0)
        {
            return;
        }

        List<TaskCompletionSource<LifecycleFeedBaseline>> completions = [];
        lock (_barrierGate)
        {
            if (_stopping)
            {
                return;
            }

            for (int index = _barriers.Count - 1; index >= 0; index--)
            {
                if (_barriers[index].Generation <= barrierCutoff)
                {
                    completions.Add(_barriers[index].Completion);
                    _barriers.RemoveAt(index);
                }
            }

            foreach (TaskCompletionSource<LifecycleFeedBaseline> completion in completions)
            {
                completion.TrySetResult(baseline);
            }
        }
    }

    private void CompleteAllBarriersAndStop(LifecycleFeedBaseline baseline)
    {
        List<TaskCompletionSource<LifecycleFeedBaseline>> completions;
        lock (_barrierGate)
        {
            _producerStopped = true;
            _wakeupWriter = null;
            completions = [.. _barriers.Select(static request => request.Completion)];
            _barriers.Clear();
        }

        foreach (TaskCompletionSource<LifecycleFeedBaseline> completion in completions)
        {
            if (baseline.Health == LifecycleFeedHealth.Healthy)
            {
                completion.TrySetException(
                    new ObjectDisposedException(nameof(BlitzReplayLifecycleFeed)));
            }
            else
            {
                completion.TrySetResult(baseline);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_barrierGate)
        {
            _stopping = true;
        }

        _stop.Cancel();
        Task? producer;
        lock (_startGate)
        {
            producer = _producer;
        }

        if (producer is not null)
        {
            await producer.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Synchronous disposal for DI container compatibility. Delegates to
    /// DisposeAsync and blocks — safe because cleanup is local cancellation
    /// and task awaiting with no risk of deadlock.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private List<FileSystemWatcher> CreateWatchers(ChannelWriter<bool> wakeupWriter)
    {
        List<FileSystemWatcher> watchers = [];
        if (!TryGetLogDirectories(out string[] directories))
        {
            _journal.RecordGap(LifecycleFeedReason.EnumerationFailed);
            return watchers;
        }

        foreach (string directory in directories)
        {
            try
            {
                FileSystemWatcher watcher = new(directory, LogPattern)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                        NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler signal = (_, _) => wakeupWriter.TryWrite(true);
                RenamedEventHandler rename = (_, _) => wakeupWriter.TryWrite(true);
                ErrorEventHandler error = (_, _) =>
                {
                    _journal.RecordGap(LifecycleFeedReason.WatcherOverflow);
                    wakeupWriter.TryWrite(true);
                };
                watcher.Changed += signal;
                watcher.Created += signal;
                watcher.Deleted += signal;
                watcher.Renamed += rename;
                watcher.Error += error;
                watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _journal.RecordGap(LifecycleFeedReason.WatcherOverflow);
            }
        }

        return watchers;
    }

    private async Task<bool> ReconcileAsync(
        Dictionary<string, TailState> states,
        LifecycleFeedBaseline startingBaseline,
        LifecycleFeedReason completionReason)
    {
        LifecycleMarkerProvenance provenance =
            startingBaseline.Health == LifecycleFeedHealth.Healthy
                ? LifecycleMarkerProvenance.Live
                : LifecycleMarkerProvenance.Historical;
        if (!TryEnumerateCurrentLogFiles(out string[] currentFiles, out bool isComplete))
        {
            _journal.RecordGap(LifecycleFeedReason.EnumerationFailed);
            return false;
        }

        if (!isComplete)
        {
            _journal.RecordGap(LifecycleFeedReason.EnumerationIncomplete);
            return false;
        }

        Dictionary<string, TailState> next = states.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        List<LifecycleFeedDraft> drafts = [];
        HashSet<string> current = currentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, TailState state) in next)
        {
            if (!current.Contains(path))
            {
                if (!state.IsMissing)
                {
                    state.Generation = checked(state.Generation + 1);
                    state.ResetAfterDeletion();
                    drafts.Add(ResetDraft(state, LifecycleFeedReason.SourceDeleted));
                }
            }
        }

        bool isConsistent = true;
        foreach (string path in currentFiles)
        {
            bool isNewSource = false;
            if (!next.TryGetValue(path, out TailState? state))
            {
                state = CreateState(path);
                next.Add(path, state);
                isNewSource = true;
            }
            try
            {
                await ReadPathAsync(
                    path,
                    state,
                    provenance,
                    isNewSource,
                    startingBaseline.CapturedAtUtc,
                    drafts).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _journal.RecordGap(LifecycleFeedReason.ReadFailed);
                isConsistent = false;
            }
        }

        if (!isConsistent)
        {
            return false;
        }

        bool committed = _journal.TryCommitReconciliationBatch(
            drafts,
            [.. next.Values.Where(static state => !state.IsMissing).Select(static state => state.Cursor).OfType<LifecycleSourceCursor>()],
            completionReason,
            startingBaseline.Sequence);
        if (!committed)
        {
            return false;
        }

        states.Clear();
        foreach ((string path, TailState state) in next) states.Add(path, state);

        return true;
    }

    private async Task ReadPathAsync(
        string path,
        TailState state,
        LifecycleMarkerProvenance provenance,
        bool isNewSource,
        DateTimeOffset baselineCapturedAtUtc,
        List<LifecycleFeedDraft> drafts)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        FileIdentity? identity = FileIdentity.TryGet(stream.SafeFileHandle);
        if (OperatingSystem.IsWindows() && identity is null)
        {
            throw new IOException("Native log identity could not be established.");
        }

        long committedOffset = state.Offset;
        byte[]? committedFingerprint = state.BoundaryFingerprint;
        bool continuityVerified = false;
        bool incarnationSnapshot = !state.IsInitialized;
        if (state.IsInitialized
            && !state.IsMissing
            && length >= state.Offset
            && identity == state.FileIdentity
            && committedFingerprint is not null)
        {
            byte[] fingerprint = await ComputeBoundaryFingerprintAsync(
                stream,
                state.Offset,
                state.BoundaryLength).ConfigureAwait(false);
            if (!fingerprint.AsSpan().SequenceEqual(committedFingerprint))
            {
                incarnationSnapshot = true;
                Reset(state, length, identity, LifecycleFeedReason.SourceRewritten, drafts);
            }
            else
            {
                continuityVerified = true;
            }
        }
        if (!state.IsInitialized)
        {
            state.Initialize(Math.Max(0, length - _options.MaxInitialLogScanBytes), identity);
        }
        else if (state.IsMissing || (identity is not null && state.FileIdentity is not null && identity != state.FileIdentity))
        {
            incarnationSnapshot = true;
            Reset(state, length, identity, state.IsMissing
                ? LifecycleFeedReason.SourceReappeared
                : LifecycleFeedReason.SourceReplaced, drafts);
        }
        else if (length < state.Offset)
        {
            incarnationSnapshot = true;
            Reset(state, length, identity, LifecycleFeedReason.SourceTruncated, drafts);
        }
        else
        {
            state.FileIdentity = identity ?? state.FileIdentity;
            state.IsMissing = false;
        }

        DateTimeOffset? creationTimeUtc = identity?.CreationTimeUtc
            ?? TryGetCreationTimeUtc(path);

        // Initial bytes from a newly enumerated source are live only when the
        // file itself was created after the healthy reconciliation barrier.
        // ParseLine additionally requires each marker's native timestamp to be
        // at or after the same barrier. Reappearance, replacement, truncation,
        // rewrite, missing timestamps, and stale copied logs remain historical.
        bool isPostBaselineNewSource =
            isNewSource
            && provenance == LifecycleMarkerProvenance.Live
            && baselineCapturedAtUtc > DateTimeOffset.MinValue
            && creationTimeUtc >= baselineCapturedAtUtc;
        LifecycleMarkerProvenance effectiveProvenance =
            incarnationSnapshot && !isPostBaselineNewSource
            ? LifecycleMarkerProvenance.Historical
            : provenance;
        DateTimeOffset? liveNotBeforeUtc = isPostBaselineNewSource
            ? baselineCapturedAtUtc
            : null;
        long target = length;
        while (state.Offset < target)
        {
            int count = checked((int)Math.Min(target - state.Offset, _options.MaxLogReadBytesPerPass));
            byte[] bytes = GC.AllocateUninitializedArray<byte>(count);
            stream.Position = state.Offset;
            await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
            long chunkOffset = state.Offset;
            state.Offset += bytes.Length;
            ProcessBytes(
                bytes,
                chunkOffset,
                state,
                effectiveProvenance,
                liveNotBeforeUtc,
                drafts);
        }

        if (stream.Length < state.Offset)
        {
            throw new IOException("Native log changed while it was being read.");
        }

        if (continuityVerified)
        {
            byte[] revalidated =
                await ComputeBoundaryFingerprintAsync(
                    stream,
                    committedOffset,
                    state.BoundaryLength).ConfigureAwait(false);
            if (!revalidated.AsSpan().SequenceEqual(committedFingerprint))
            {
                throw new IOException("Native log continuity changed while it was being read.");
            }
        }

        state.BoundaryLength = checked((int)Math.Min(
            state.Offset,
            Math.Max(_options.MaxInitialLogScanBytes, state.PendingBytes.Length)));
        state.BoundaryFingerprint = await ComputeBoundaryFingerprintAsync(
            stream,
            state.Offset,
            state.BoundaryLength).ConfigureAwait(false);
    }

    private void Reset(
        TailState state,
        long length,
        FileIdentity? identity,
        LifecycleFeedReason reason,
        List<LifecycleFeedDraft> drafts)
    {
        long offset = Math.Max(0, length - _options.MaxInitialLogScanBytes);
        state.Generation = checked(state.Generation + 1);
        state.Reset(offset, identity);
        drafts.Add(ResetDraft(state, reason));
    }

    private void ProcessBytes(
        byte[] bytes,
        long chunkOffset,
        TailState state,
        LifecycleMarkerProvenance provenance,
        DateTimeOffset? liveNotBeforeUtc,
        List<LifecycleFeedDraft> drafts)
    {
        byte[] combined = new byte[state.PendingBytes.Length + bytes.Length];
        bool hasPendingBytes = state.PendingBytes.Length > 0;
        LifecycleMarkerProvenance pendingProvenance = state.PendingProvenance;
        DateTimeOffset? pendingLiveNotBeforeUtc = state.PendingLiveNotBeforeUtc;
        state.PendingBytes.CopyTo(combined, 0);
        bytes.CopyTo(combined, state.PendingBytes.Length);
        long combinedOffset = state.PendingBytes.Length == 0 ? chunkOffset : state.PendingOffset;
        int lineStart = 0;
        for (int index = 0; index < combined.Length; index++)
        {
            if (combined[index] != (byte)'\n')
            {
                continue;
            }

            int lineLength = index - lineStart;
            if (lineLength > 0 && combined[index - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (state.SkipFirstPartialLine)
            {
                state.SkipFirstPartialLine = false;
            }
            else
            {
                LifecycleMarkerProvenance lineProvenance =
                    provenance == LifecycleMarkerProvenance.Historical
                        ? LifecycleMarkerProvenance.Historical
                        : hasPendingBytes && lineStart == 0
                        ? pendingProvenance
                        : provenance;
                DateTimeOffset? lineLiveNotBeforeUtc =
                    lineProvenance == LifecycleMarkerProvenance.Historical
                        ? null
                        : hasPendingBytes && lineStart == 0
                        ? pendingLiveNotBeforeUtc
                        : liveNotBeforeUtc;
                ParseLine(
                    combined.AsSpan(lineStart, lineLength),
                    checked(combinedOffset + index + 1),
                    state,
                    lineProvenance,
                    lineLiveNotBeforeUtc,
                    drafts);
            }

            lineStart = index + 1;
        }

        int remaining = combined.Length - lineStart;
        int maxLineBytes = checked(_options.MaxLogLineCharacters * 4);
        if (remaining > maxLineBytes)
        {
            state.PendingBytes = [];
            state.PendingOffset = state.Offset;
            state.SkipFirstPartialLine = true;
        }
        else
        {
            state.PendingBytes = combined[lineStart..];
            state.PendingOffset = combinedOffset + lineStart;
            state.PendingProvenance =
                provenance == LifecycleMarkerProvenance.Historical
                    ? LifecycleMarkerProvenance.Historical
                    : hasPendingBytes && lineStart == 0
                    ? pendingProvenance
                    : provenance;
            state.PendingLiveNotBeforeUtc =
                state.PendingProvenance == LifecycleMarkerProvenance.Historical
                    ? null
                    : hasPendingBytes && lineStart == 0
                    ? pendingLiveNotBeforeUtc
                    : liveNotBeforeUtc;
        }
    }

    private void ParseLine(
        ReadOnlySpan<byte> bytes,
        long cursorOffset,
        TailState state,
        LifecycleMarkerProvenance provenance,
        DateTimeOffset? liveNotBeforeUtc,
        List<LifecycleFeedDraft> drafts)
    {
        if (bytes.Length > checked(_options.MaxLogLineCharacters * 4))
        {
            return;
        }

        string line = Encoding.UTF8.GetString(bytes);
        if (_parser.TryParse(line, out ParsedReplayLogMarker? marker) && marker is not null)
        {
            LifecycleMarkerProvenance markerProvenance =
                provenance == LifecycleMarkerProvenance.Live
                && liveNotBeforeUtc.HasValue
                && (marker.SourceTimestampUtc is null
                    || marker.SourceTimestampUtc < liveNotBeforeUtc)
                    ? LifecycleMarkerProvenance.Historical
                    : provenance;
            drafts.Add(new LifecycleFeedDraft(LifecycleFeedEventKind.Marker, state.SourceId, state.Generation, cursorOffset, marker.Kind, marker.SourceTimestampUtc, markerProvenance, LifecycleFeedReason.Marker));
        }
    }

    private static DateTimeOffset? TryGetCreationTimeUtc(string path)
    {
        try
        {
            DateTime creationTimeUtc = File.GetCreationTimeUtc(path);
            return creationTimeUtc == DateTime.MinValue
                ? null
                : new DateTimeOffset(creationTimeUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static LifecycleFeedDraft ResetDraft(TailState state, LifecycleFeedReason reason) =>
        new(LifecycleFeedEventKind.SourceReset, state.SourceId, state.Generation, state.Offset, null, null, null, reason);

    private static async ValueTask<byte[]> ComputeBoundaryFingerprintAsync(
        FileStream stream,
        long offset,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            (long)count,
            offset,
            nameof(count));

        if (count == 0)
        {
            return SHA256.HashData([]);
        }

        stream.Position = offset - count;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(Math.Min(64 * 1024, count));
        int remaining = count;
        while (remaining > 0)
        {
            int requested = Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Native log ended while continuity was being verified.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return hash.GetHashAndReset();
    }

    private static TailState CreateState(string path) => new(
        new ContentHash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))));

    private bool TryEnumerateCurrentLogFiles(out string[] files, out bool isComplete)
    {
        try
        {
            if (!TryGetLogDirectories(out string[] directories))
            {
                files = [];
                isComplete = false;
                return false;
            }

            string[] candidates = directories
                .SelectMany(directory => Directory.EnumerateFiles(directory, LogPattern, SearchOption.TopDirectoryOnly))
                .Take(checked(_options.MaxTrackedLogFiles + 1))
                .ToArray();
            isComplete = candidates.Length <= _options.MaxTrackedLogFiles;
            files = candidates
                .OrderByDescending(static path => File.GetLastWriteTimeUtc(path))
                .Take(_options.MaxTrackedLogFiles)
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            files = [];
            isComplete = false;
            return false;
        }
    }

    private bool TryGetLogDirectories(out string[] directories)
    {
        List<string> configured = [.. _options.UserDataRoots];
        if (_options.UseDefaultDiscoveryRoots)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                configured.Add(Path.Combine(localAppData, "wotblitz"));
            }
        }

        HashSet<string> discovered = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in configured)
        {
            try
            {
                string root = Path.GetFullPath(candidate);
                string leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string logDirectory = leaf.Equals("DAVAProject", StringComparison.OrdinalIgnoreCase)
                    ? root : leaf.Equals("packs", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Directory.GetParent(root)?.FullName ?? root, "DAVAProject")
                        : Path.Combine(root, "DAVAProject");
                if (Directory.Exists(logDirectory))
                {
                    discovered.Add(logDirectory);
                }
                else if (Directory.Exists(root) && Directory.EnumerateFiles(root, LogPattern).Any())
                {
                    discovered.Add(root);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                // An inaccessible configured root makes a reconciliation incomplete.
                directories = [];
                return false;
            }
        }

        directories = [.. discovered];
        return true;
    }

    private sealed class TailState(ContentHash sourceId)
    {
        public ContentHash SourceId { get; } = sourceId;
        public long Generation { get; set; } = 1;
        public long Offset { get; set; }
        public bool IsInitialized { get; private set; }
        public bool IsMissing { get; set; }
        public FileIdentity? FileIdentity { get; set; }
        public byte[]? BoundaryFingerprint { get; set; }
        public int BoundaryLength { get; set; }
        public byte[] PendingBytes { get; set; } = [];
        public long PendingOffset { get; set; }
        public LifecycleMarkerProvenance PendingProvenance { get; set; } =
            LifecycleMarkerProvenance.Historical;
        public DateTimeOffset? PendingLiveNotBeforeUtc { get; set; }
        public bool SkipFirstPartialLine { get; set; }
        public LifecycleSourceCursor? Cursor => IsInitialized
            ? new LifecycleSourceCursor(SourceId, Generation, Offset)
            : null;

        public void Initialize(long offset, FileIdentity? identity)
        {
            IsInitialized = true;
            Offset = offset;
            FileIdentity = identity;
            PendingOffset = offset;
            SkipFirstPartialLine = offset > 0;
        }

        public void Reset(long offset, FileIdentity? identity)
        {
            Offset = offset;
            FileIdentity = identity;
            BoundaryFingerprint = null;
            BoundaryLength = 0;
            IsMissing = false;
            PendingBytes = [];
            PendingOffset = offset;
            PendingProvenance = LifecycleMarkerProvenance.Historical;
            PendingLiveNotBeforeUtc = null;
            SkipFirstPartialLine = offset > 0;
        }

        public void ResetAfterDeletion()
        {
            IsMissing = true;
            PendingBytes = [];
            PendingOffset = Offset;
            PendingProvenance = LifecycleMarkerProvenance.Historical;
            PendingLiveNotBeforeUtc = null;
            BoundaryFingerprint = null;
            BoundaryLength = 0;
            SkipFirstPartialLine = false;
        }

        public void MarkPendingAsHistorical()
        {
            if (PendingBytes.Length > 0)
            {
                PendingProvenance = LifecycleMarkerProvenance.Historical;
                PendingLiveNotBeforeUtc = null;
            }
        }

        public TailState Clone()
        {
            return new TailState(SourceId)
            {
                Generation = Generation,
                Offset = Offset,
                IsInitialized = IsInitialized,
                IsMissing = IsMissing,
                FileIdentity = FileIdentity,
                BoundaryFingerprint = BoundaryFingerprint is null ? null : [.. BoundaryFingerprint],
                BoundaryLength = BoundaryLength,
                PendingBytes = [.. PendingBytes],
                PendingOffset = PendingOffset,
                PendingProvenance = PendingProvenance,
                PendingLiveNotBeforeUtc = PendingLiveNotBeforeUtc,
                SkipFirstPartialLine = SkipFirstPartialLine,
            };
        }
    }

    private sealed record BarrierRequest(
        long Generation,
        TaskCompletionSource<LifecycleFeedBaseline> Completion);

    private readonly record struct FileIdentity(
        uint VolumeSerial,
        uint IndexHigh,
        uint IndexLow,
        DateTimeOffset CreationTimeUtc)
    {
        public static FileIdentity? TryGet(SafeFileHandle handle)
        {
            if (!OperatingSystem.IsWindows() || handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                return null;
            }

            long creationTime = ((long)information.CreationTime.dwHighDateTime << 32)
                | (uint)information.CreationTime.dwLowDateTime;
            try
            {
                return new FileIdentity(
                    information.VolumeSerialNumber,
                    information.FileIndexHigh,
                    information.FileIndexLow,
                    new DateTimeOffset(DateTime.FromFileTimeUtc(creationTime)));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}
