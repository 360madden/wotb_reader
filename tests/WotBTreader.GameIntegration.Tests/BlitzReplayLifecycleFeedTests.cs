using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class BlitzReplayLifecycleFeedTests
{
    [TestMethod]
    public async Task CaptureBaselineAsync_InitialTailIsHistoricalAndHealthy()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync(
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic\n");

        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);
        LifecycleFeedReadResult events = await fixture.Feed.ReadAfterAsync(0, CancellationToken.None);

        Assert.AreEqual(LifecycleFeedHealth.Healthy, baseline.Health);
        Assert.IsGreaterThan(0L, baseline.HealthEpoch);
        Assert.HasCount(1, events.Events.Where(feedEvent => feedEvent.Kind == LifecycleFeedEventKind.Marker));
        Assert.AreEqual(
            LifecycleMarkerProvenance.Historical,
            events.Events.Single(feedEvent => feedEvent.Kind == LifecycleFeedEventKind.Marker).Provenance);
    }

    [TestMethod]
    public async Task ReadAfterAsync_AppendedMarkerIsLive()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("unrecognized initial line\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await File.AppendAllTextAsync(
            fixture.LogPath,
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic\n");
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.Marker
                && feedEvent.Provenance == LifecycleMarkerProvenance.Live));

        Assert.IsFalse(events.HistoryGap);
    }

    [TestMethod]
    public async Task ReadAfterAsync_RealCompletionMarker_SurfacesOfflineReplayStopped()
    {
        // Real line shapes from the live 11.19.0.10 replay session log
        // (2026-08-12): the results-screen controller activation fires ~4m41s
        // after the start marker, matching the decoded battle duration.
        await using TestFeed fixture = await TestFeed.CreateAsync(
            "15:36:35 [info] 10:36:35 -5 [replay] Start replay event\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await File.AppendAllTextAsync(
            fixture.LogPath,
            "15:41:18 [info] 10:41:18 -5 [base] Controller activated: BattleResultsPersonalPageController\n");
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.Marker
                && feedEvent.MarkerKind == ReplayLogMarkerKind.OfflineReplayStopped
                && feedEvent.Provenance == LifecycleMarkerProvenance.Live));

        Assert.IsFalse(events.HistoryGap);

        // The start marker was captured in the baseline (sequence <= baseline), so
        // a full read from 0 sees it as historical evidence alongside the live
        // completion marker.
        LifecycleFeedReadResult all =
            await fixture.Feed.ReadAfterAsync(0, CancellationToken.None);
        LifecycleFeedEvent start = all.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.Marker
            && feedEvent.MarkerKind == ReplayLogMarkerKind.OfflineReplayStarted);
        Assert.AreEqual(LifecycleMarkerProvenance.Historical, start.Provenance);
    }

    [TestMethod]
    public async Task ReadAfterAsync_AutoLoopBoundaryLines_DoNotSurfaceCompletion()
    {
        // Real line shapes from the 2026-08-06 auto-loop fixture: battle 1's
        // onLeaveWorld and the next LoadGameScene chain directly, with no
        // results-screen controller between battles. These per-battle boundary
        // lines are NOT in the marker allowlist and must never surface an
        // OfflineReplayStopped event (completion is final-end only). Regression
        // guard: appended after baseline, so an over-greedy future marker on
        // either line would surface as a Live event and fail this test.
        await using TestFeed fixture = await TestFeed.CreateAsync(
            "02:31:28 [info] 21:31:28 -5 [replay] Start replay event\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await File.AppendAllTextAsync(
            fixture.LogPath,
            "02:31:30 [info] 21:31:30 -5 [battle] VehicleGameLogic::onLeaveWorld id: 2549401 isPlayer: 1\n" +
            "02:31:33 [info] 21:31:33 -5 [battle] BattleController::LoadGameScene begins\n");
        await Task.Delay(TimeSpan.FromSeconds(1));
        LifecycleFeedReadResult events =
            await fixture.Feed.ReadAfterAsync(baseline.Sequence, CancellationToken.None);

        Assert.IsFalse(events.Events.Any(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.Marker
            && feedEvent.MarkerKind == ReplayLogMarkerKind.OfflineReplayStopped));
    }

    [TestMethod]
    public async Task CaptureReconciledBaselineAsync_IncludesMarkerAlreadyWritten()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("initial\n");
        LifecycleFeedBaseline before =
            await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);
        await File.AppendAllTextAsync(
            fixture.LogPath,
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic\n");

        LifecycleFeedBaseline reconciled =
            await fixture.Feed.CaptureReconciledBaselineAsync(CancellationToken.None);
        LifecycleFeedReadResult events =
            await fixture.Feed.ReadAfterAsync(before.Sequence, CancellationToken.None);

        LifecycleFeedEvent marker = events.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.Marker);
        Assert.IsLessThanOrEqualTo(reconciled.Sequence, marker.Sequence);
        Assert.AreEqual(LifecycleFeedHealth.Healthy, reconciled.Health);
        Assert.AreEqual(
            new FileInfo(fixture.LogPath).Length,
            reconciled.Sources.Single().LastByteOffset);
    }

    [TestMethod]
    public async Task CaptureReconciledBaselineAsync_PartialLineCompletedLaterStaysHistorical()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("initial\n");
        _ = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);
        await File.AppendAllTextAsync(
            fixture.LogPath,
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic");
        LifecycleFeedBaseline baseline =
            await fixture.Feed.CaptureReconciledBaselineAsync(CancellationToken.None);

        await File.AppendAllTextAsync(fixture.LogPath, "\n");
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.Marker));

        Assert.AreEqual(
            LifecycleMarkerProvenance.Historical,
            events.Events.Single(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.Marker).Provenance);
    }

    [TestMethod]
    public async Task CaptureReconciledBaselineAsync_DisposalDuringBarrierCannotReturnHealthy()
    {
        using TemporaryDirectory directory = new();
        string logDirectory = directory.CreateDirectory("DAVAProject");
        string logPath = Path.Combine(logDirectory, "blitz-logs_test.txt");
        await File.WriteAllTextAsync(logPath, "initial\n");
        GameIntegrationOptions options = new()
        {
            UserDataRoots = [directory.Path],
            UseDefaultDiscoveryRoots = false,
            LogReconciliationInterval = TimeSpan.FromMinutes(5),
        };
        using var parser = new BlockingParser();
        var feed = new BlitzReplayLifecycleFeed(
            options,
            parser,
            TimeProvider.System,
            NullLogger<BlitzReplayLifecycleFeed>.Instance);
        Task? disposal = null;
        try
        {
            _ = await feed.CaptureBaselineAsync(CancellationToken.None);
            await File.AppendAllTextAsync(logPath, "block\n");
            Task<LifecycleFeedBaseline> barrier =
                feed.CaptureReconciledBaselineAsync(CancellationToken.None).AsTask();
            Assert.IsTrue(parser.Entered.Wait(TimeSpan.FromSeconds(8)));

            disposal = feed.DisposeAsync().AsTask();
            parser.Release.Set();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                async () => await barrier);
            await disposal;
        }
        finally
        {
            parser.Release.Set();
            if (disposal is null)
            {
                await feed.DisposeAsync();
            }
            else
            {
                await disposal;
            }
        }
    }

    [TestMethod]
    public async Task ReadAfterAsync_PrepopulatedNewSourceWithStaleMarkerIsHistorical()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("initial\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);
        string second = Path.Combine(Path.GetDirectoryName(fixture.LogPath)!, "blitz-logs_second.txt");
        await File.WriteAllTextAsync(second, "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic\n");

        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static items => items.Any(item => item.Kind == LifecycleFeedEventKind.Marker));
        Assert.AreEqual(
            LifecycleMarkerProvenance.Historical,
            events.Events.Single(item => item.Kind == LifecycleFeedEventKind.Marker).Provenance);
    }

    [TestMethod]
    public async Task ReadAfterAsync_NewlyCreatedSourceWithPostBaselineMarkerIsLive()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("initial\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        string second = Path.Combine(Path.GetDirectoryName(fixture.LogPath)!, "blitz-logs_second.txt");
        string timestamp = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(second, $"[{timestamp}] START_REPLAY_LOCAL synthetic\n");

        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static items => items.Any(item => item.Kind == LifecycleFeedEventKind.Marker));
        LifecycleFeedEvent marker = events.Events.Single(item => item.Kind == LifecycleFeedEventKind.Marker);
        Assert.AreEqual(LifecycleMarkerProvenance.Live, marker.Provenance);
        Assert.IsNotNull(marker.SourceTimestampUtc);
        Assert.IsGreaterThanOrEqualTo(
            baseline.CapturedAtUtc,
            marker.SourceTimestampUtc.Value);
    }

    [TestMethod]
    public async Task ReadAfterAsync_LinePartialAtBaselineRemainsHistorical()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync(
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic");
        LifecycleFeedBaseline baseline =
            await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await File.AppendAllTextAsync(fixture.LogPath, "\n");
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.Marker
                && feedEvent.Provenance == LifecycleMarkerProvenance.Historical));

        LifecycleFeedEvent marker = events.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.Marker);
        Assert.IsNotNull(marker.Cursor);
        Assert.IsGreaterThan(
            baseline.Sources.Single().LastByteOffset,
            marker.Cursor.LastByteOffset);
    }

    [TestMethod]
    public async Task ReadAfterAsync_TruncationEmitsGenerationReset()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync(
            "[2026-07-26T20:00:00Z] START_REPLAY_LOCAL synthetic payload that makes initial content longer\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await File.WriteAllTextAsync(fixture.LogPath, "short\n");
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.SourceReset
                && feedEvent.Reason == LifecycleFeedReason.SourceTruncated));

        LifecycleFeedEvent reset = events.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.SourceReset
            && feedEvent.Reason == LifecycleFeedReason.SourceTruncated);
        Assert.IsNotNull(reset.Cursor);
        Assert.AreEqual(2L, reset.Cursor.Generation);
    }

    [TestMethod]
    public async Task ReadAfterAsync_DeletionAndReappearancePreserveSourceAndAdvanceGeneration()
    {
        await using TestFeed fixture = await TestFeed.CreateAsync("initial\n");
        LifecycleFeedBaseline baseline = await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        File.Delete(fixture.LogPath);
        LifecycleFeedReadResult deleted = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.SourceReset
                && feedEvent.Reason == LifecycleFeedReason.SourceDeleted));
        LifecycleFeedEvent deletion = deleted.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.SourceReset
            && feedEvent.Reason == LifecycleFeedReason.SourceDeleted);

        await File.WriteAllTextAsync(fixture.LogPath, "recreated\n");
        LifecycleFeedReadResult reappeared = await fixture.WaitForAsync(
            deleted.LatestSequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.SourceReset
                && feedEvent.Reason == LifecycleFeedReason.SourceReappeared));
        LifecycleFeedEvent reappearance = reappeared.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.SourceReset
            && feedEvent.Reason == LifecycleFeedReason.SourceReappeared);

        Assert.IsNotNull(deletion.Cursor);
        Assert.IsNotNull(reappearance.Cursor);
        Assert.AreEqual(deletion.Cursor.SourceId, reappearance.Cursor.SourceId);
        Assert.IsGreaterThan(deletion.Cursor.Generation, reappearance.Cursor.Generation);
    }

    [TestMethod]
    public async Task CaptureBaselineAsync_IncompleteEnumerationIsExplicitlyDegraded()
    {
        using TemporaryDirectory directory = new();
        string logDirectory = directory.CreateDirectory("DAVAProject");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "blitz-logs_a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(logDirectory, "blitz-logs_b.txt"), "b\n");
        GameIntegrationOptions options = new()
        {
            UserDataRoots = [directory.Path],
            UseDefaultDiscoveryRoots = false,
            MaxTrackedLogFiles = 1,
        };
        await using var feed = new BlitzReplayLifecycleFeed(
            options,
            new BlitzReplayLifecycleParser(options),
            TimeProvider.System,
            NullLogger<BlitzReplayLifecycleFeed>.Instance);

        LifecycleFeedBaseline baseline =
            await feed.CaptureBaselineAsync(CancellationToken.None);
        LifecycleFeedReadResult events =
            await feed.ReadAfterAsync(0, CancellationToken.None);

        Assert.AreEqual(LifecycleFeedHealth.Degraded, baseline.Health);
        Assert.IsTrue(events.Events.Any(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.Gap
            && feedEvent.Reason == LifecycleFeedReason.EnumerationIncomplete));
    }

    [TestMethod]
    public async Task ReadAfterAsync_FullOverwriteEmitsRewriteReset()
    {
        string initial = new('a', 512);
        await using TestFeed fixture = await TestFeed.CreateAsync(initial);
        LifecycleFeedBaseline baseline =
            await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await RewriteInPlaceAsync(fixture.LogPath, new string('b', 512));
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.SourceReset
                && feedEvent.Reason == LifecycleFeedReason.SourceRewritten));

        LifecycleFeedEvent reset = events.Events.Single(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.SourceReset
            && feedEvent.Reason == LifecycleFeedReason.SourceRewritten);
        Assert.AreEqual(2L, reset.Cursor?.Generation);
    }

    [TestMethod]
    public async Task ReadAfterAsync_PrefixRewritePreservingBoundaryEmitsReset()
    {
        string initial = new('a', 512);
        await using TestFeed fixture = await TestFeed.CreateAsync(initial);
        LifecycleFeedBaseline baseline =
            await fixture.Feed.CaptureBaselineAsync(CancellationToken.None);

        await RewriteInPlaceAsync(
            fixture.LogPath,
            new string('b', 256) + new string('a', 256));
        LifecycleFeedReadResult events = await fixture.WaitForAsync(
            baseline.Sequence,
            static feedEvents => feedEvents.Any(feedEvent =>
                feedEvent.Kind == LifecycleFeedEventKind.SourceReset
                && feedEvent.Reason == LifecycleFeedReason.SourceRewritten));

        Assert.IsTrue(events.Events.Any(feedEvent =>
            feedEvent.Kind == LifecycleFeedEventKind.SourceReset
            && feedEvent.Reason == LifecycleFeedReason.SourceRewritten));
    }

    [TestMethod]
    public async Task CaptureBaselineAsync_ProducerFaultDoesNotPublishStagedMarker()
    {
        using TemporaryDirectory directory = new();
        string logDirectory = directory.CreateDirectory("DAVAProject");
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, "blitz-logs_test.txt"),
            "marker\nfault\n");
        GameIntegrationOptions options = new()
        {
            UserDataRoots = [directory.Path],
            UseDefaultDiscoveryRoots = false,
        };
        await using var feed = new BlitzReplayLifecycleFeed(
            options,
            new MarkerThenFaultParser(),
            TimeProvider.System,
            NullLogger<BlitzReplayLifecycleFeed>.Instance);

        LifecycleFeedBaseline baseline =
            await feed.CaptureBaselineAsync(CancellationToken.None);
        LifecycleFeedReadResult events =
            await feed.ReadAfterAsync(0, CancellationToken.None);

        Assert.AreEqual(LifecycleFeedHealth.Degraded, baseline.Health);
        Assert.IsFalse(events.Events.Any(static item =>
            item.Kind == LifecycleFeedEventKind.Marker));
        Assert.IsTrue(events.Events.Any(static item =>
            item.Kind == LifecycleFeedEventKind.Fault
            && item.Reason == LifecycleFeedReason.ProducerFault));
    }

    private static async Task RewriteInPlaceAsync(string path, string content)
    {
        // Overwrite the existing bytes in place (FileMode.Open) rather than
        // File.WriteAllTextAsync, which truncates then rewrites and lets the
        // tail reader observe the transient empty state as SourceTruncated
        // instead of the rewrite reset the tests pin.
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private sealed class TestFeed : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory;

        private TestFeed(TemporaryDirectory directory, string logPath, BlitzReplayLifecycleFeed feed)
        {
            _directory = directory;
            LogPath = logPath;
            Feed = feed;
        }

        public BlitzReplayLifecycleFeed Feed { get; }

        public string LogPath { get; }

        public static async Task<TestFeed> CreateAsync(string initialContent)
        {
            TemporaryDirectory directory = new();
            string logDirectory = directory.CreateDirectory("DAVAProject");
            string logPath = Path.Combine(logDirectory, "blitz-logs_test.txt");
            await File.WriteAllTextAsync(logPath, initialContent);
            GameIntegrationOptions options = new()
            {
                UserDataRoots = [directory.Path],
                UseDefaultDiscoveryRoots = false,
                LogReconciliationInterval = TimeSpan.FromMilliseconds(250),
                MaxInitialLogScanBytes = 4096,
                MaxLogReadBytesPerPass = 128,
            };
            return new TestFeed(
                directory,
                logPath,
                new BlitzReplayLifecycleFeed(
                    options,
                    new BlitzReplayLifecycleParser(options),
                    TimeProvider.System,
                    NullLogger<BlitzReplayLifecycleFeed>.Instance));
        }

        public async Task<LifecycleFeedReadResult> WaitForAsync(
            long afterSequence,
            Func<IReadOnlyList<LifecycleFeedEvent>, bool> predicate)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            try
            {
                while (true)
                {
                    LifecycleFeedReadResult result = await Feed.ReadAfterAsync(afterSequence, timeout.Token);
                    if (predicate(result.Events))
                    {
                        return result;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                LifecycleFeedReadResult result = await Feed.ReadAfterAsync(afterSequence, CancellationToken.None);
                Assert.Fail($"Timed out waiting for lifecycle event. Observed: {string.Join(",", result.Events.Select(static item => item.Reason))}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Feed.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class MarkerThenFaultParser : IBlitzReplayLifecycleParser
    {
        public bool TryParse(
            string line,
            out ParsedReplayLogMarker? marker)
        {
            if (line == "marker")
            {
                marker = new ParsedReplayLogMarker(
                    ReplayLogMarkerKind.OfflineReplayStarted,
                    SourceTimestampUtc: null);
                return true;
            }

            throw new InvalidOperationException("Synthetic parser fault.");
        }
    }

    private sealed class BlockingParser : IBlitzReplayLifecycleParser, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public bool TryParse(string line, out ParsedReplayLogMarker? marker)
        {
            marker = null;
            if (line != "block")
            {
                return false;
            }

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(8)))
            {
                throw new TimeoutException("Synthetic parser release timed out.");
            }

            return false;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
