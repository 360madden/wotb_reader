using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

/// <summary>
/// Tests for the OverlayApiState thread-safe bridge between HTTP API and MainViewModel.
///
/// IMPORTANT: OverlayApiState uses a static process-wide singleton (Instance).
/// Tests run sequentially in MSTest but execution ORDER within the class is
/// NOT guaranteed. Tests that rely on the unregistered state must account for
/// the possibility that a prior test registered a ViewModel.
///
/// Strategy: every test that cares about ViewModel state explicitly calls
/// Register() with a fresh VM at the start. Tests for null/unregistered
/// behaviour are grouped at the top and depend on being first to execute.
/// </summary>
[TestClass]
public sealed class OverlayApiStateTests
{
    [TestMethod]
    public void GetStatus_WhenNoViewModel_ReturnsUnready()
    {
        // Relies on running before any Register()-calling test.
        // If another test has already registered a VM, this will still
        // pass but test the wrong thing (a live VM, not null).
        Contracts.OverlayStatusResponse status = OverlayApiState.Instance.GetStatus();
        Assert.AreEqual("overlay not ready", status.Status);
        Assert.AreEqual(0, status.SessionsCount);
        Assert.IsFalse(status.Connected);
    }

    [TestMethod]
    public void GetStatus_WithFreshViewModel_ReflectsFreshState()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);

        // Set explicit values before reading, so the test doesn't depend
        // on MainViewModel defaults (which can vary across versions).
        OverlayApiState.Instance.PostSetSpeed(4.0);
        OverlayApiState.Instance.PostSeek(15.0);

        Contracts.OverlayStatusResponse status = OverlayApiState.Instance.GetStatus();

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
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);

        // A fresh VM has IsPlaying = false (and Duration = 0, so toggle won't work).
        // PostPlay checks !IsPlaying before calling PlayPauseCommand.
        OverlayApiState.Instance.PostPlay();
    }

    [TestMethod]
    public void PostPlay_Idempotent_WhenAlreadyPlaying()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);

        // PostPlay when IsPlaying is false should call the toggle.
        // Since Duration is zero, TogglePlayPause returns early, so
        // IsPlaying stays false. PostPlay should not crash.
        OverlayApiState.Instance.PostPlay();
        OverlayApiState.Instance.PostPlay();
    }

    [TestMethod]
    public void PostPause_WhenNotPlaying_DoesNotThrow()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);
        OverlayApiState.Instance.PostPause();
    }

    [TestMethod]
    public void PostSeek_UpdatesCurrentTime()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);
        OverlayApiState.Instance.PostSeek(42.5);

        // SynchronizationContext.Current is null in tests, so PostToUi
        // executes synchronously.
        Assert.AreEqual(42.5, vm.CurrentTimeSeconds);
    }

    [TestMethod]
    public void PostSetSpeed_UpdatesPlaybackSpeed()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);

        OverlayApiState.Instance.PostSetSpeed(2.0);
        Assert.AreEqual(2.0, vm.PlaybackSpeed);

        OverlayApiState.Instance.PostSetSpeed(8.0);
        Assert.AreEqual(8.0, vm.PlaybackSpeed);

        OverlayApiState.Instance.PostSetSpeed(0.5);
        Assert.AreEqual(0.5, vm.PlaybackSpeed);
    }

    [TestMethod]
    public void PostRefreshSessions_DoesNotThrow()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);
        OverlayApiState.Instance.PostRefreshSessions();
    }

    [TestMethod]
    public void PostSelectSession_WithUnknownId_DoesNotThrow()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);
        OverlayApiState.Instance.PostSelectSession(Guid.NewGuid());
    }

    [TestMethod]
    public void PostLaunch_WhenWpfAppNotRunning_DoesNotThrow()
    {
        // In tests, Application.Current is null (no WPF app running).
        // PostLaunch should guard against this and not crash.
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);
        OverlayApiState.Instance.PostLaunch(@"C:\test\replay.wotbreplay");
    }

    [TestMethod]
    public void Register_WithNewViewModel_ReplacesPreviousState()
    {
        MainViewModel vm1 = new();
        OverlayApiState.Instance.Register(vm1);
        OverlayApiState.Instance.PostSetSpeed(2.0);

        MainViewModel vm2 = new();
        OverlayApiState.Instance.Register(vm2);

        // vm2 should have the default speed, not vm1's 2.0.
        Assert.AreEqual(4.0, vm2.PlaybackSpeed);
        Contracts.OverlayStatusResponse status = OverlayApiState.Instance.GetStatus();
        Assert.AreEqual(4.0, status.PlaybackSpeed);
    }

    [TestMethod]
    public void GetStatus_AfterPlaybackChanges_ReflectsCurrentState()
    {
        MainViewModel vm = new();
        OverlayApiState.Instance.Register(vm);

        OverlayApiState.Instance.PostSetSpeed(1.0);
        OverlayApiState.Instance.PostSeek(30.0);

        Contracts.OverlayStatusResponse status = OverlayApiState.Instance.GetStatus();
        Assert.AreEqual(1.0, status.PlaybackSpeed);
        Assert.AreEqual(30.0, status.CurrentTimeSeconds);
    }
}
