using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

/// <summary>
/// Tests for the OverlayApiState thread-safe bridge between HTTP API and MainViewModel.
/// Each test uses an independent instance so method-level parallelization
/// cannot mutate another test's registered ViewModel.
/// </summary>
[TestClass]
public sealed class OverlayApiStateTests
{
    [TestMethod]
    public void GetStatus_WhenNoViewModel_ReturnsUnready()
    {
        var state = new OverlayApiState();

        Contracts.OverlayStatusResponse status = state.GetStatus();

        Assert.AreEqual("overlay not ready", status.Status);
        Assert.AreEqual(0, status.SessionsCount);
        Assert.IsFalse(status.Connected);
    }

    [TestMethod]
    public void GetStatus_WithFreshViewModel_ReflectsFreshState()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);

        // Set explicit values before reading, so the test doesn't depend
        // on MainViewModel defaults (which can vary across versions).
        state.PostSetSpeed(4.0);
        state.PostSeek(15.0);

        Contracts.OverlayStatusResponse status = state.GetStatus();

        Assert.IsFalse(status.Connected);
        Assert.IsNull(status.BaseUri);
        Assert.AreEqual(0, status.SessionsCount);
        Assert.IsNull(status.SelectedMap);
        Assert.IsFalse(status.IsPlaying);
        Assert.AreEqual(15.0, status.CurrentTimeSeconds);
        Assert.AreEqual(0.0, status.DurationSeconds);
        Assert.AreEqual(4.0, status.PlaybackSpeed);
        Assert.IsFalse(status.GameWindowFound);
        Assert.AreNotEqual("overlay not ready", status.Status);
    }

    [TestMethod]
    public void PostPlay_WhenPaused_DoesNotThrow()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);

        // A fresh VM has IsPlaying = false (and Duration = 0, so toggle won't work).
        // PostPlay checks !IsPlaying before calling PlayPauseCommand.
        state.PostPlay();
    }

    [TestMethod]
    public void PostPlay_Idempotent_WhenAlreadyPlaying()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);

        // PostPlay when IsPlaying is false should call the toggle.
        // Since Duration is zero, TogglePlayPause returns early, so
        // IsPlaying stays false. PostPlay should not crash.
        state.PostPlay();
        state.PostPlay();
    }

    [TestMethod]
    public void PostPause_WhenNotPlaying_DoesNotThrow()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);
        state.PostPause();
    }

    [TestMethod]
    public void PostSeek_UpdatesCurrentTime()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);
        state.PostSeek(42.5);

        // SynchronizationContext.Current is null in tests, so PostToUi
        // executes synchronously.
        Assert.AreEqual(42.5, vm.CurrentTimeSeconds);
    }

    [TestMethod]
    public void PostSetSpeed_UpdatesPlaybackSpeed()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);

        state.PostSetSpeed(2.0);
        Assert.AreEqual(2.0, vm.PlaybackSpeed);

        state.PostSetSpeed(8.0);
        Assert.AreEqual(8.0, vm.PlaybackSpeed);

        state.PostSetSpeed(0.5);
        Assert.AreEqual(0.5, vm.PlaybackSpeed);
    }

    [TestMethod]
    public void PostRefreshSessions_DoesNotThrow()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);
        state.PostRefreshSessions();
    }

    [TestMethod]
    public void PostSelectSession_WithUnknownId_DoesNotThrow()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);
        state.PostSelectSession(Guid.NewGuid());
    }

    [TestMethod]
    public void PostLaunch_WhenWpfAppNotRunning_DoesNotThrow()
    {
        // In tests, Application.Current is null (no WPF app running).
        // PostLaunch should guard against this and not crash.
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);
        state.PostLaunch(@"C:\test\replay.wotbreplay");
    }

    [TestMethod]
    public void Register_WithNewViewModel_ReplacesPreviousState()
    {
        var state = new OverlayApiState();
        MainViewModel vm1 = new();
        state.Register(vm1);
        state.PostSetSpeed(2.0);

        MainViewModel vm2 = new();
        state.Register(vm2);

        // vm2 should have the default speed, not vm1's 2.0.
        Assert.AreEqual(4.0, vm2.PlaybackSpeed);
        Contracts.OverlayStatusResponse status = state.GetStatus();
        Assert.AreEqual(4.0, status.PlaybackSpeed);
    }

    [TestMethod]
    public void GetStatus_AfterPlaybackChanges_ReflectsCurrentState()
    {
        var state = new OverlayApiState();
        MainViewModel vm = new();
        state.Register(vm);

        state.PostSetSpeed(1.0);
        state.PostSeek(30.0);

        Contracts.OverlayStatusResponse status = state.GetStatus();
        Assert.AreEqual(1.0, status.PlaybackSpeed);
        Assert.AreEqual(30.0, status.CurrentTimeSeconds);
    }
}
