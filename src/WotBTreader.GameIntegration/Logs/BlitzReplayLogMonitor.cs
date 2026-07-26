using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Logs;

/// <summary>
/// Uses FileSystemWatcher as a low-latency hint and periodic directory reconciliation
/// as the source of truth, because Windows watcher notifications can be dropped.
/// </summary>
public sealed class BlitzReplayLogMonitor : IBlitzReplayLogMonitor
{
    private const string LogPattern = "blitz-logs_*.txt";

    private readonly GameIntegrationOptions _options;
    private readonly IBlitzReplayLifecycleParser _parser;
    private readonly ILogger<BlitzReplayLogMonitor> _logger;

    /// <summary>Creates a bounded native-log monitor.</summary>
    public BlitzReplayLogMonitor(
        GameIntegrationOptions options,
        IBlitzReplayLifecycleParser parser,
        ILogger<BlitzReplayLogMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _parser = parser;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReplayLogEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BoundedChannelOptions channelOptions = new(_options.LogEventChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        };
        Channel<ReplayLogEvent> events = Channel.CreateBounded<ReplayLogEvent>(channelOptions);

        using CancellationTokenSource producerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task producer = ProduceAsync(events.Writer, producerCancellation.Token);

        try
        {
            await foreach (ReplayLogEvent replayEvent in events.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return replayEvent;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task ProduceAsync(
        ChannelWriter<ReplayLogEvent> eventWriter,
        CancellationToken cancellationToken)
    {
        Channel<bool> wakeups = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        ConcurrentDictionary<string, LogTailState> states =
            new(StringComparer.OrdinalIgnoreCase);
        List<FileSystemWatcher> watchers = CreateWatchers(wakeups.Writer);
        long sequence = 0;

        try
        {
            wakeups.Writer.TryWrite(true);
            while (!cancellationToken.IsCancellationRequested)
            {
                await ReconcileAsync(states, eventWriter, () => ++sequence, cancellationToken)
                    .ConfigureAwait(false);

                using CancellationTokenSource delayCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                delayCancellation.CancelAfter(_options.LogReconciliationInterval);
                try
                {
                    await wakeups.Reader.ReadAsync(delayCancellation.Token).ConfigureAwait(false);
                    while (wakeups.Reader.TryRead(out _))
                    {
                    }
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested &&
                    delayCancellation.IsCancellationRequested)
                {
                    // Periodic reconciliation is intentional even with no watcher activity.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }

            eventWriter.TryComplete();
        }
    }

    private List<FileSystemWatcher> CreateWatchers(ChannelWriter<bool> wakeupWriter)
    {
        List<FileSystemWatcher> watchers = [];
        foreach (string directory in GetLogDirectories())
        {
            try
            {
                FileSystemWatcher watcher = new(directory, LogPattern)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };

                FileSystemEventHandler signal = (_, _) => wakeupWriter.TryWrite(true);
                RenamedEventHandler renameSignal = (_, _) => wakeupWriter.TryWrite(true);
                ErrorEventHandler errorSignal = (_, _) => wakeupWriter.TryWrite(true);
                watcher.Changed += signal;
                watcher.Created += signal;
                watcher.Deleted += signal;
                watcher.Renamed += renameSignal;
                watcher.Error += errorSignal;
                watchers.Add(watcher);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        new EventId(3120, "NativeLogWatcherUnavailable"),
                        "A native-log watcher could not be started ({ExceptionType}); periodic reconciliation remains active.",
                        exception.GetType().Name);
                }
            }
        }

        return watchers;
    }

    private async Task ReconcileAsync(
        ConcurrentDictionary<string, LogTailState> states,
        ChannelWriter<ReplayLogEvent> eventWriter,
        Func<long> nextSequence,
        CancellationToken cancellationToken)
    {
        string[] currentFiles = EnumerateCurrentLogFiles();
        HashSet<string> currentSet = currentFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string tracked in states.Keys)
        {
            if (!currentSet.Contains(tracked))
            {
                states.TryRemove(tracked, out _);
            }
        }

        foreach (string path in currentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTailState state = states.GetOrAdd(path, CreateInitialState);
            try
            {
                await ReadNewBytesAsync(
                        path,
                        state,
                        eventWriter,
                        nextSequence,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        new EventId(3121, "NativeLogReadDeferred"),
                        "A native log could not be read consistently ({ExceptionType}); it will be reconciled again.",
                        exception.GetType().Name);
                }
            }
        }
    }

    private async Task ReadNewBytesAsync(
        string path,
        LogTailState state,
        ChannelWriter<ReplayLogEvent> eventWriter,
        Func<long> nextSequence,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long length = stream.Length;
        if (length < state.Offset)
        {
            state.Reset(Math.Max(0, length - _options.MaxInitialLogScanBytes));
        }

        long available = length - state.Offset;
        if (available <= 0)
        {
            return;
        }

        int readLength = checked((int)Math.Min(available, _options.MaxLogReadBytesPerPass));
        byte[] bytes = GC.AllocateUninitializedArray<byte>(readLength);
        stream.Position = state.Offset;
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        long chunkOffset = state.Offset;
        state.Offset += bytes.Length;

        byte[] combined = new byte[state.PendingBytes.Length + bytes.Length];
        state.PendingBytes.CopyTo(combined, 0);
        bytes.CopyTo(combined, state.PendingBytes.Length);
        long combinedOffset = state.PendingBytes.Length == 0
            ? chunkOffset
            : state.PendingOffset;

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
                ParseLine(
                    combined.AsSpan(lineStart, lineLength),
                    combinedOffset + lineStart,
                    state.SourceId,
                    eventWriter,
                    nextSequence);
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
        }
    }

    private void ParseLine(
        ReadOnlySpan<byte> bytes,
        long byteOffset,
        ContentHash sourceId,
        ChannelWriter<ReplayLogEvent> eventWriter,
        Func<long> nextSequence)
    {
        if (bytes.Length > checked(_options.MaxLogLineCharacters * 4))
        {
            return;
        }

        string line = Encoding.UTF8.GetString(bytes);
        if (!_parser.TryParse(line, out ParsedReplayLogMarker? marker) || marker is null)
        {
            return;
        }

        eventWriter.TryWrite(
            new ReplayLogEvent(
                nextSequence(),
                marker.Kind,
                marker.SourceTimestampUtc,
                DateTimeOffset.UtcNow,
                sourceId,
                byteOffset));
    }

    private LogTailState CreateInitialState(string path)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            length = 0;
        }

        long offset = Math.Max(0, length - _options.MaxInitialLogScanBytes);
        return new LogTailState(
            offset,
            new ContentHash(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))),
            skipFirstPartialLine: offset > 0);
    }

    private string[] EnumerateCurrentLogFiles()
    {
        try
        {
            return GetLogDirectories()
                .SelectMany(
                    directory => Directory.EnumerateFiles(
                        directory,
                        LogPattern,
                        SearchOption.TopDirectoryOnly))
                // Native-log directories are expected to be small; cap hostile or
                // accidentally broad roots before allocating FileInfo projections.
                .Take(checked(_options.MaxTrackedLogFiles * 16))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(_options.MaxTrackedLogFiles)
                .Select(file => file.FullName)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    new EventId(3122, "NativeLogEnumerationDeferred"),
                    "Native-log enumeration was deferred ({ExceptionType}).",
                    exception.GetType().Name);
            }

            return [];
        }
    }

    private string[] GetLogDirectories()
    {
        List<string> configured = [.. _options.UserDataRoots];
        if (_options.UseDefaultDiscoveryRoots)
        {
            string localAppData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                configured.Add(Path.Combine(localAppData, "wotblitz"));
            }
        }

        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in configured)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                string root = Path.GetFullPath(candidate);
                string leaf = Path.GetFileName(
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string logDirectory = leaf.Equals("DAVAProject", StringComparison.OrdinalIgnoreCase)
                    ? root
                    : leaf.Equals("packs", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Directory.GetParent(root)?.FullName ?? root, "DAVAProject")
                        : Path.Combine(root, "DAVAProject");

                if (Directory.Exists(logDirectory))
                {
                    directories.Add(logDirectory);
                }
                else if (Directory.Exists(root) &&
                         Directory.EnumerateFiles(root, LogPattern, SearchOption.TopDirectoryOnly).Any())
                {
                    directories.Add(root);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException or
                    IOException or UnauthorizedAccessException)
            {
                // Invalid or inaccessible roots are ignored without logging path material.
            }
        }

        return directories.ToArray();
    }

    private sealed class LogTailState(
        long offset,
        ContentHash sourceId,
        bool skipFirstPartialLine)
    {
        public long Offset { get; set; } = offset;

        public ContentHash SourceId { get; } = sourceId;

        public byte[] PendingBytes { get; set; } = [];

        public long PendingOffset { get; set; } = offset;

        public bool SkipFirstPartialLine { get; set; } = skipFirstPartialLine;

        public void Reset(long newOffset)
        {
            Offset = newOffset;
            PendingBytes = [];
            PendingOffset = newOffset;
            SkipFirstPartialLine = newOffset > 0;
        }
    }
}
