using WotBTreader.CaptureLogs.Clock;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Tests;

[TestClass]
public sealed class SegmentedReplayClockSourceTests
{
    [TestMethod]
    public async Task SnapshotAdvancesAtSegmentSpeed()
    {
        SegmentedReplayClockSource clock = new();
        BattleSessionId sessionId = BattleSessionId.New();
        DateTimeOffset anchor = new(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);
        await clock.AddSegmentAsync(
            CreateSegment(sessionId, sequence: 1, anchor, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var result = await clock.GetSnapshotAsync(
            sessionId,
            anchor.AddSeconds(2),
            CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromSeconds(7), result.Value?.EstimatedReplayTime);
        Assert.AreEqual(ReplayClockQuality.Estimated, result.Value?.Quality);
    }

    [TestMethod]
    public async Task NonMonotonicSegmentIsRejected()
    {
        SegmentedReplayClockSource clock = new();
        BattleSessionId sessionId = BattleSessionId.New();
        DateTimeOffset anchor = new(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);
        await clock.AddSegmentAsync(
            CreateSegment(sessionId, sequence: 2, anchor, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var result = await clock.AddSegmentAsync(
            CreateSegment(sessionId, sequence: 1, anchor.AddSeconds(1), TimeSpan.FromSeconds(6)),
            CancellationToken.None);

        Assert.AreEqual("clock.segment.non_monotonic", result.Error?.Code);
    }

    [TestMethod]
    public async Task MarkStaleReportsStaleQuality()
    {
        SegmentedReplayClockSource clock = new();
        BattleSessionId sessionId = BattleSessionId.New();
        DateTimeOffset anchor = new(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);
        await clock.AddSegmentAsync(
            CreateSegment(sessionId, sequence: 1, anchor, TimeSpan.Zero),
            CancellationToken.None);

        var result = await clock.MarkStaleAsync(
            sessionId,
            anchor.AddSeconds(1),
            CancellationToken.None);

        Assert.AreEqual(ReplayClockQuality.Stale, result.Value?.Quality);
    }

    private static ReplayClockSegment CreateSegment(
        BattleSessionId sessionId,
        long sequence,
        DateTimeOffset sourceAnchor,
        TimeSpan replayAnchor) =>
        new(
            ReplayClockSegmentId.New(),
            sessionId,
            sequence,
            sourceAnchor,
            replayAnchor,
            Speed: 1,
            TelemetrySourceKind.Manual,
            Uncertainty: TimeSpan.FromMilliseconds(100),
            CreatedAtUtc: sourceAnchor);
}
