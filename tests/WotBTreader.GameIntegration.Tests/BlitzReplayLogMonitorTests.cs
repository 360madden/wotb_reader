using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class BlitzReplayLogMonitorTests
{
    [TestMethod]
    public async Task WatchAsync_InitialReconciliation_EmitsOnlyRecognizedMarkerMetadata()
    {
        using TemporaryDirectory temporary = new();
        string logDirectory = temporary.CreateDirectory("DAVAProject");
        string logPath = System.IO.Path.Combine(logDirectory, "blitz-logs_test.txt");
        await File.WriteAllTextAsync(
            logPath,
            """
            private user payload without an allowed marker
            [2026-07-26T20:00:00Z] START_REPLAY_LOCAL private-trailing-data

            """);

        GameIntegrationOptions options = new()
        {
            UserDataRoots = [temporary.Path],
            UseDefaultDiscoveryRoots = false,
            LogReconciliationInterval = TimeSpan.FromMilliseconds(250),
            MaxInitialLogScanBytes = 4096,
            MaxLogReadBytesPerPass = 4096,
        };
        BlitzReplayLogMonitor monitor = new(
            options,
            new BlitzReplayLifecycleParser(options),
            NullLogger<BlitzReplayLogMonitor>.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await using IAsyncEnumerator<ReplayLogEvent> enumerator =
            monitor.WatchAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        bool moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout.Token);
        ReplayLogEvent replayEvent = enumerator.Current;

        Assert.IsTrue(moved);
        Assert.AreEqual(1L, replayEvent.Sequence);
        Assert.AreEqual(ReplayLogMarkerKind.OfflineReplayStarted, replayEvent.Kind);
        Assert.IsTrue(replayEvent.IsPositiveOfflineReplayEvidence);
        Assert.AreEqual(64, replayEvent.OpaqueSourceId.Value.Length);
        Assert.IsGreaterThan(0L, replayEvent.ByteOffset);
    }
}
