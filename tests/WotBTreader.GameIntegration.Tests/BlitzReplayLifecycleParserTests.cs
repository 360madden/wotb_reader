using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class BlitzReplayLifecycleParserTests
{
    private static readonly GameIntegrationOptions Options = new()
    {
        UseDefaultDiscoveryRoots = false,
        MaxLogLineCharacters = 128,
    };

    [TestMethod]
    public void TryParse_OfflineStart_IsPositiveOfflineEvidence()
    {
        BlitzReplayLifecycleParser parser = new(Options);

        bool recognized = parser.TryParse(
            "[2026-07-26T16:00:00-04:00] START_REPLAY_LOCAL",
            out ParsedReplayLogMarker? marker);

        Assert.IsTrue(recognized);
        Assert.IsNotNull(marker);
        Assert.AreEqual(ReplayLogMarkerKind.OfflineReplayStarted, marker.Kind);
        Assert.IsTrue(marker.IsPositiveOfflineReplayEvidence);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
            marker.SourceTimestampUtc);
    }

    [TestMethod]
    [DataRow("ReplayRecorder::StartRecording", ReplayLogMarkerKind.ReplayRecordingStarted)]
    [DataRow("ReplayRecorder::StopRecording", ReplayLogMarkerKind.ReplayRecordingStopped)]
    [DataRow("STOP_REPLAY_LOCAL", ReplayLogMarkerKind.OfflineReplayStopped)]
    // Playback completion: the game never writes STOP_REPLAY_LOCAL in a real
    // run; the observed completion signals are the post-battle controller
    // transitions to the results screen (live-verified 2026-08-12).
    [DataRow(
        "Controller activated: BattleResultsController",
        ReplayLogMarkerKind.OfflineReplayStopped)]
    [DataRow(
        "Controller activated: BattleResultsPersonalPageController",
        ReplayLogMarkerKind.OfflineReplayStopped)]
    public void TryParse_KnownNonStartMarkers_AreNotPositiveOfflineEvidence(
        string line,
        ReplayLogMarkerKind expectedKind)
    {
        BlitzReplayLifecycleParser parser = new(Options);

        bool recognized = parser.TryParse(line, out ParsedReplayLogMarker? marker);

        Assert.IsTrue(recognized);
        Assert.AreEqual(expectedKind, marker!.Kind);
        Assert.IsFalse(marker.IsPositiveOfflineReplayEvidence);
    }

    [TestMethod]
    public void TryParse_RealResultsControllerLine_IsOfflineReplayStopped()
    {
        BlitzReplayLifecycleParser parser = new(Options);

        // Exact line shape from the live 11.19.0.10 replay session log
        // (2026-08-12, deadlocked-run replay): the results screen controller
        // activation fired ~4m41s after the second 'Start replay event' marker,
        // matching the decoded battle duration.
        bool recognized = parser.TryParse(
            "15:41:18 [info] 10:41:18 -5 [base] Controller activated: BattleResultsPersonalPageController",
            out ParsedReplayLogMarker? marker);

        Assert.IsTrue(recognized);
        Assert.IsNotNull(marker);
        Assert.AreEqual(ReplayLogMarkerKind.OfflineReplayStopped, marker.Kind);
        Assert.IsFalse(marker.IsPositiveOfflineReplayEvidence);
    }

    [TestMethod]
    public void TryParse_UnknownPrivateText_IsDiscarded()
    {
        BlitzReplayLifecycleParser parser = new(Options);

        bool recognized = parser.TryParse(
            "player=private-name account=123456 chat=private-message",
            out ParsedReplayLogMarker? marker);

        Assert.IsFalse(recognized);
        Assert.IsNull(marker);
    }

    [TestMethod]
    public void TryParse_OversizedLine_IsDiscardedBeforeMarkerSearch()
    {
        BlitzReplayLifecycleParser parser = new(Options);
        string line = string.Concat(new string('x', 129), " START_REPLAY_LOCAL");

        bool recognized = parser.TryParse(line, out ParsedReplayLogMarker? marker);

        Assert.IsFalse(recognized);
        Assert.IsNull(marker);
    }
}
