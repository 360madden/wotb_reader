using WotBTreader.Overlay.ViewModels;
using WotBTreader.Overlay.Windowing;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class GameWindowTrackingTests
{
    private static readonly IntPtr OverlayHandle = new(42);
    private static readonly GameWindowBounds Bounds = new(100, 200, 1920, 1080);

    [TestMethod]
    public void Track_NotFound_LeavesOverlayUntrackedAndDoesNotAttemptPositioning()
    {
        FakeGameWindowTracker tracker = new(GameWindowProbe.NotFound());

        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            tracker,
            OverlayHandle,
            wasTracking: false);

        Assert.AreEqual(HudGameWindowState.NotFound, result.State);
        Assert.IsFalse(result.IsTracking);
        Assert.IsFalse(result.TrackingStarted);
        Assert.IsFalse(tracker.PositionCalled);
        Assert.IsNull(result.Bounds);
    }

    [TestMethod]
    public void Track_AmbiguousWindows_RemainsFailClosed()
    {
        FakeGameWindowTracker tracker = new(GameWindowProbe.Ambiguous());

        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            tracker,
            OverlayHandle,
            wasTracking: true);

        Assert.AreEqual(HudGameWindowState.Ambiguous, result.State);
        Assert.IsFalse(result.IsTracking);
        Assert.IsFalse(result.TrackingStarted);
        Assert.IsFalse(tracker.PositionCalled);
        Assert.IsNull(result.Bounds);
    }

    [TestMethod]
    public void Track_ReadyWindowPositionsOverlayAndReportsTrackingStart()
    {
        FakeGameWindowTracker tracker = new(GameWindowProbe.Ready(Bounds));

        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            tracker,
            OverlayHandle,
            wasTracking: false);

        Assert.AreEqual(HudGameWindowState.Tracking, result.State);
        Assert.IsTrue(result.IsTracking);
        Assert.IsTrue(result.TrackingStarted);
        Assert.IsTrue(tracker.PositionCalled);
        Assert.AreEqual(OverlayHandle, tracker.PositionedHandle);
        Assert.AreEqual(Bounds, tracker.PositionedBounds);
        Assert.AreEqual(Bounds, result.Bounds!.Value);
    }

    [TestMethod]
    public void Track_AlreadyTrackingWindowDoesNotRepeatStartTransition()
    {
        FakeGameWindowTracker tracker = new(GameWindowProbe.Ready(Bounds));

        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            tracker,
            OverlayHandle,
            wasTracking: true);

        Assert.AreEqual(HudGameWindowState.Tracking, result.State);
        Assert.IsTrue(result.IsTracking);
        Assert.IsFalse(result.TrackingStarted);
    }

    [TestMethod]
    public void Track_BoundsFailuresRemainFailClosed()
    {
        GameWindowProbe[] failures =
        [
            GameWindowProbe.BoundsUnavailable(),
            GameWindowProbe.BoundsInvalid(),
        ];

        foreach (GameWindowProbe probe in failures)
        {
            FakeGameWindowTracker tracker = new(probe);

            GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
                tracker,
                OverlayHandle,
                wasTracking: true);

            Assert.AreNotEqual(HudGameWindowState.Tracking, result.State);
            Assert.IsFalse(result.IsTracking);
            Assert.IsFalse(tracker.PositionCalled);
        }
    }

    [TestMethod]
    public void Track_PositionFailureReportsAlignmentFailure()
    {
        FakeGameWindowTracker tracker = new(GameWindowProbe.Ready(Bounds), positionSucceeds: false);

        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            tracker,
            OverlayHandle,
            wasTracking: false);

        Assert.AreEqual(HudGameWindowState.RepositionFailed, result.State);
        Assert.IsFalse(result.IsTracking);
        Assert.IsFalse(result.TrackingStarted);
        Assert.IsTrue(tracker.PositionCalled);
        Assert.AreEqual(Bounds, result.Bounds!.Value);
    }

    private sealed class FakeGameWindowTracker(
        GameWindowProbe probe,
        bool positionSucceeds = true) : IGameWindowTracker
    {
        public bool PositionCalled { get; private set; }

        public IntPtr PositionedHandle { get; private set; }

        public GameWindowBounds PositionedBounds { get; private set; }

        public GameWindowProbe Probe() => probe;

        public bool TryPositionOverlay(IntPtr overlayHandle, GameWindowBounds bounds)
        {
            PositionCalled = true;
            PositionedHandle = overlayHandle;
            PositionedBounds = bounds;
            return positionSucceeds;
        }
    }
}
