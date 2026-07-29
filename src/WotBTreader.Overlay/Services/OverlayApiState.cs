using WotBTreader.ApiContracts;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Services;

/// <summary>
/// Shared state bridge between the WPF UI thread and the overlay's embedded HTTP API.
/// Endpoints read/write ViewModel state through this singleton, which marshals
/// all mutations to the WPF dispatcher thread via an injectable dispatch delegate.
/// </summary>
public sealed class OverlayApiState
{
    /// <summary>
    /// The single process-wide instance. Set by the WPF startup path before
    /// the embedded HTTP server begins accepting requests.
    /// </summary>
    public static OverlayApiState Instance { get; } = new();

    private MainViewModel? _viewModel;
    private Action<Action>? _dispatch;
    private readonly Lock _gate = new();

    /// <summary>
    /// Thread-safe flag set by MainWindow's window-tracking timer.
    /// Read from any thread; written only from the UI thread.
    /// </summary>
    internal volatile bool IsTrackingGameWindow;

    internal OverlayApiState()
    {
    }

    /// <summary>
    /// Registers the MainViewModel so endpoints can query and control it.
    /// Must be called from the WPF UI thread before HTTP requests arrive.
    /// </summary>
    /// <param name="viewModel">The MainViewModel to register.</param>
    /// <param name="dispatch">
    /// Optional delegate that marshals an action to the WPF UI thread.
    /// When omitted, actions execute synchronously (test-friendly default).
    /// In production, pass <c>action => Dispatcher.BeginInvoke(action)</c>.
    /// </param>
    public void Register(
        MainViewModel viewModel,
        Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        lock (_gate)
        {
            _viewModel = viewModel;
            _dispatch = dispatch;
        }
    }

    /// <summary>
    /// Builds a read-only status snapshot. Safe to call from any thread.
    /// </summary>
    public OverlayStatusResponse GetStatus()
    {
        MainViewModel? vm;
        lock (_gate) { vm = _viewModel; }

        if (vm is null)
        {
            return new OverlayStatusResponse { Status = "overlay not ready" };
        }

        return new OverlayStatusResponse
        {
            Connected = !string.IsNullOrEmpty(vm.BaseUri),
            BaseUri = string.IsNullOrEmpty(vm.BaseUri) ? null : vm.BaseUri,
            SessionsCount = vm.Sessions.Count,
            SelectedMap = vm.MapName,
            IsPlaying = vm.IsPlaying,
            CurrentTimeSeconds = vm.CurrentTimeSeconds,
            DurationSeconds = vm.Duration.TotalSeconds,
            PlaybackSpeed = vm.PlaybackSpeed,
            GameWindowFound = IsGameWindowVisible(),
            Status = vm.Status,
        };
    }

    /// <summary>
    /// Posts a refresh-sessions action to the UI thread. Returns immediately;
    /// the refresh runs asynchronously on the dispatcher.
    /// </summary>
    public void PostRefreshSessions()
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }
            _ = (vm?.RefreshSessionsAsync() ?? Task.CompletedTask);
        });
    }

    /// <summary>
    /// Posts a launch action for the given replay path to the UI thread.
    /// </summary>
    public void PostLaunch(string replayPath)
    {
        PostToUi(() =>
        {
            // Launch is handled via the MainWindow's QuickLaunchWithPathAsync,
            // which is exposed through a static delegate or accessed via the
            // current application's main window.
            if (System.Windows.Application.Current?.MainWindow is MainWindow window)
            {
                _ = window.QuickLaunchWithPathViaApiAsync(replayPath);
            }
        });
    }

    /// <summary>Posts a play action to the UI thread.</summary>
    public void PostPlay()
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }
            // Only start playing if currently paused.
            if (vm is not null && !vm.IsPlaying)
                vm.PlayPauseCommand.Execute(null);
        });
    }

    /// <summary>Posts a pause action to the UI thread.</summary>
    public void PostPause()
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }
            // Toggle only if currently playing.
            if (vm is not null && vm.IsPlaying)
                vm.PlayPauseCommand.Execute(null);
        });
    }

    /// <summary>Posts a seek action to the UI thread.</summary>
    public void PostSeek(double seconds)
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }
            if (vm is not null)
                vm.CurrentTimeSeconds = seconds;
        });
    }

    /// <summary>Posts a speed-change action to the UI thread.</summary>
    public void PostSetSpeed(double speed)
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }
            vm?.SetPlaybackSpeed(speed);
        });
    }

    /// <summary>Posts a session-select action to the UI thread.</summary>
    public void PostSelectSession(Guid battleSessionId)
    {
        PostToUi(() =>
        {
            MainViewModel? vm;
            lock (_gate) { vm = _viewModel; }

            if (vm is null) return;

            foreach (SessionRow row in vm.Sessions)
            {
                if (row.BattleSessionId == battleSessionId)
                {
                    vm.SelectedSession = row;
                    return;
                }
            }
        });
    }

    /// <summary>
    /// Returns true if the WoT Blitz game window is currently visible on screen.
    /// Thread-safe: reads a volatile flag set by MainWindow's P/Invoke timer.
    /// </summary>
    private static bool IsGameWindowVisible()
    {
        return Instance.IsTrackingGameWindow;
    }

    private void PostToUi(Action action)
    {
        // Read outside the lock: reference reads are atomic and a stale
        // value (null vs dispatcher) is harmless — the action just runs
        // synchronously. Volatile.Read ensures the freshest possible value
        // without a full memory barrier.
        Action<Action>? dispatch = Volatile.Read(ref _dispatch);

        if (dispatch is not null)
        {
            dispatch(action);
        }
        else
        {
            action();
        }
    }
}
