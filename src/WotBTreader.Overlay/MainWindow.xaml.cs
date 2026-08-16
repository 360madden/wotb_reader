using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using WotBTreader.ApiContracts;
using WotBTreader.Overlay.Logging;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;
using WotBTreader.Overlay.Windowing;

namespace WotBTreader.Overlay;

/// <summary>
/// Transparent, borderless, always-on-top HUD that sits over the WoT Blitz game
/// during replay playback. Plots team-coloured position dots over the game's
/// minimap area, with a floating semi-transparent panel for session selection
/// and controls. Tracks the game window via P/Invoke so the overlay stays
/// aligned during playback.
///
/// The HUD is a loopback client only — it does not start the web host, import
/// replays, or launch the game. Those operations belong to the CLI and web host.
/// </summary>
public partial class MainWindow : System.Windows.Window, IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly TelemetryStreamService _streamService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _windowTrackTimer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _hpPulseTimer;
    private readonly IHudLogger _logger;
    private readonly IGameWindowTracker _gameWindowTracker;
    private readonly bool _ownsLogger;
    private bool _disposed;
    private bool _hudRenderPending;
    private bool _hpPulseOn;
    private int _sidebarAnimationGeneration;

    private static readonly System.Windows.Media.Brush LiveHpHealthyBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x43, 0xE0, 0x7A));
    private static readonly System.Windows.Media.Brush LiveHpCriticalBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xC3, 0x4B));

    public MainWindow(IHudLogger? logger = null)
        : this(logger, null)
    {
    }

    internal MainWindow(IHudLogger? logger, IGameWindowTracker? gameWindowTracker)
    {
        _logger = logger ?? HudLoggerFactory.CreateDefault();
        _gameWindowTracker = gameWindowTracker ?? new Win32GameWindowTracker();
        _ownsLogger = logger is null;
        _streamService = new TelemetryStreamService(_logger);
        _viewModel = new MainViewModel(
            new Discovery.RendezvousLocator(),
            static (baseUri, capability) => new TreaderApiClient(baseUri, capability: capability),
            _streamService,
            _logger);
        DataContext = _viewModel;
        InitializeComponent();

        W2sHudView.PlaybackScrubRequested += OnPlaybackScrubRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Nameplates.CollectionChanged += OnHudItemsChanged;
        _viewModel.Beacons.CollectionChanged += OnHudItemsChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();

        _windowTrackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowTrackTimer.Tick += OnTrackGameWindow;
        _windowTrackTimer.Start();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += OnPlaybackTick;

        // HP pulse timer — oscillates the HP text color between green and yellow
        // when HP is below 30% to signal critical health.
        _hpPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hpPulseTimer.Tick += OnHpPulseTick;
        _logger.Information(
            "hud.window.created",
            ("hudUiVersion", _viewModel.HudUiVersionLabel));
    }

    /// <summary>The MainViewModel, exposed for test access.</summary>
    internal ViewModels.MainViewModel ViewModel => _viewModel;

    /// <summary>
    /// True when the game window has been found and the overlay is tracking it.
    /// Set by the window-track timer callback.
    /// </summary>
    internal bool IsTrackingGameWindow { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        W2sHudView.PlaybackScrubRequested -= OnPlaybackScrubRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Nameplates.CollectionChanged -= OnHudItemsChanged;
        _viewModel.Beacons.CollectionChanged -= OnHudItemsChanged;
        _refreshTimer.Stop();
        _windowTrackTimer.Stop();
        _playbackTimer.Stop();
        _hpPulseTimer.Stop();
        _streamService.Dispose();
        _logger.Information("hud.window.disposed");
        if (_ownsLogger && _logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── Window lifecycle ─────────────────────────────────────

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _logger.Information("hud.window.loaded");
        try
        {
            await _viewModel.RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.Failure("hud.window.startup_failed", ex);
            _viewModel.SetFatalState($"Startup error: {ex.GetType().Name}");
        }
    }

    private void SearchText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_viewModel.SearchText)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        // Decoded replay details are immutable after import. Re-fetching them
        // every two seconds reset playback to the end, interrupted play state,
        // and produced unnecessary HTTP/SQLite work. Manual Refresh and the
        // SignalR session-list path remain the explicit refresh mechanisms.
        _viewModel.RefreshRenderHealth();
    }

    private void OnHudItemsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ScheduleHudRender();
    }

    /// <summary>
    /// Coalesces the many collection-change notifications produced by a single
    /// frame rebuild into one canvas render. Rebuilding the whole canvas on
    /// every Add/Clear (dozens per frame at 20 fps) previously caused a
    /// per-frame render storm; the dispatcher collapses them to one pass.
    /// </summary>
    private void ScheduleHudRender()
    {
        if (_hudRenderPending)
        {
            return;
        }

        _hudRenderPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
        {
            _hudRenderPending = false;
            RenderW2sHud();
        }));
    }

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying))
        {
            _logger.Information(
                "hud.playback.state_changed",
                ("playing", _viewModel.IsPlaying));
            if (_viewModel.IsPlaying)
            {
                PlayButton.Content = "⏸";
                _playbackTimer.Start();
            }
            else
            {
                PlayButton.Content = "▶";
                _playbackTimer.Stop();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentTimeSeconds))
        {
            // Scrubbing while paused must also move the projected nameplates.
            RefreshW2sFrame();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLiveMode))
        {
            // Live mode is a frame source, not a timeline state. Reuse the
            // 20 fps playback tick for live polling even when playback is
            // paused, and stop it again when neither mode needs it.
            if (_viewModel.IsLiveMode)
            {
                _playbackTimer.Start();
            }
            else if (!_viewModel.IsPlaying)
            {
                _playbackTimer.Stop();
            }

            // Switching modes must refresh immediately, including when no
            // replay session is selected and the live frame is the only source.
            RefreshW2sFrame();
        }
        else if (e.PropertyName == nameof(MainViewModel.HasLiveMemoryObservation))
        {
            if (_viewModel.HasLiveMemoryObservation && _viewModel.LivePlayerHP is int hp && hp > 0)
            {
                _hpPulseTimer.Start();
            }
            else
            {
                _hpPulseTimer.Stop();
                _hpPulseOn = false;
                SetLiveHpPulseColor(pulseOn: false);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.PenBadge))
        {
            // The pen badge can change without a nameplate change (e.g. the
            // aim moves to a tank that is currently off-viewport); re-render.
            RenderW2sHud();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedPenShellName))
        {
            // Re-score the badge with the newly selected shell (sidebar
            // selector or Q hotkey).
            RefreshW2sFrame();
        }
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        // AdvancePlayback publishes CurrentTimeSeconds, which triggers the
        // same frame refresh via OnViewModelPropertyChanged; issuing a second
        // request here doubled the HTTP/frame load every tick.
        _viewModel.AdvancePlayback();
    }

    /// <summary>
    /// Fetches the overlay frame at the current replay time and renders the
    /// projected nameplates over the game window. Fire-and-forget: failures
    /// keep the previous frame on screen, and stale responses are dropped by
    /// the view model's generation guard.
    /// </summary>
    private void RefreshW2sFrame()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        _ = _viewModel.RefreshOverlayFrameAsync(ActualWidth, ActualHeight);
    }

    /// <summary>Renders the latest view-model nameplates + beacons onto the HUD canvas.</summary>
    private void RenderW2sHud()
    {
        double totalSeconds = _viewModel.Duration.TotalSeconds;
        double? progress = totalSeconds > 0
            ? Math.Clamp(_viewModel.CurrentTimeSeconds / totalSeconds, 0, 1)
            : null;
        string? label = totalSeconds > 0
            ? Views.W2sHudView.FormatPlaybackLabel(_viewModel.CurrentTimeSeconds, totalSeconds)
            : null;

        W2sHudView.Render(
            _viewModel.Beacons,
            _viewModel.Pips,
            _viewModel.Nameplates,
            _viewModel.OwnMarkers,
            _viewModel.MinimapItems,
            _viewModel.MinimapBeacons,
            _viewModel.MinimapCameraX,
            _viewModel.MinimapCameraZ,
            _viewModel.MinimapCameraYawRadians,
            _viewModel.KillFeed,
            _viewModel.Scoreboard,
            _viewModel.PenBadge,
            _viewModel.MinimapImageSource,
            progress,
            label,
            ActualWidth,
            ActualHeight);
    }

    /// <summary>
    /// Handles a click/drag on the in-HUD playback bar: maps the 0..1 fraction
    /// to an absolute timeline position. The view model clamps and refreshes
    /// the frame, so scrubbing while paused updates the projection immediately.
    /// </summary>
    private void OnPlaybackScrubRequested(double fraction)
    {
        _viewModel.ScrubToFraction(fraction);
    }

    private void OnHpPulseTick(object? sender, EventArgs e)
    {
        // Pulse the HP value foreground between green and amber while HP is
        // critically low (~30% of a typical Blitz heavy/medium). The timer
        // keeps running while a live observation is active so a tank that
        // drops into the critical band later still starts pulsing (the old
        // logic stopped the timer as soon as HP was healthy and never
        // restarted it).
        const double approximateMaxHp = 2500;
        if (!_viewModel.HasLiveMemoryObservation || _viewModel.LivePlayerHP is not int hp)
        {
            _hpPulseTimer.Stop();
            _hpPulseOn = false;
            SetLiveHpPulseColor(pulseOn: false);
            return;
        }

        bool critical = hp <= approximateMaxHp * 0.3;
        _hpPulseOn = critical && !_hpPulseOn;
        SetLiveHpPulseColor(critical && _hpPulseOn);
    }

    private void SetLiveHpPulseColor(bool pulseOn)
    {
        if (LiveHpValueText is null)
        {
            return;
        }

        LiveHpValueText.Foreground = pulseOn ? LiveHpCriticalBrush : LiveHpHealthyBrush;
    }

    // ── Keyboard shortcuts ──────────────────────────────────

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Space:
                _viewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Left:
                _viewModel.ScrubRelative(TimeSpan.FromSeconds(-5));
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Right:
                _viewModel.ScrubRelative(TimeSpan.FromSeconds(5));
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D1:
                _viewModel.SetPlaybackSpeed(0.5);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D2:
                _viewModel.SetPlaybackSpeed(1.0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D3:
                _viewModel.SetPlaybackSpeed(2.0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D4:
                _viewModel.SetPlaybackSpeed(4.0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D5:
                _viewModel.SetPlaybackSpeed(8.0);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Q:
                // Cycle the pen badge's shell (AP/APCR/HE/HEAT); the
                // SelectedPenShellName change handler re-scores the badge.
                _viewModel.CycleShell();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    // ── Sidebar collapse toggle ─────────────────────────────

    private bool _sidebarExpanded = true;
    private double _sidebarRestoreOpacity = 0.92;

    private void ToggleSidebarCollapse(object sender, System.Windows.RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;

        // A re-expand started while the fade-out is still animating must win:
        // its Completed handler would otherwise collapse the just-restored
        // panels and leave the sidebar visible but empty.
        int generation = ++_sidebarAnimationGeneration;

        if (_sidebarExpanded)
        {
            // Make panels visible first, then animate opacity from 0 → 0.92.
            SessionsListBox.Visibility = System.Windows.Visibility.Visible;
            TimelineGrid.Visibility = System.Windows.Visibility.Visible;
            DetailGrid.Visibility = System.Windows.Visibility.Visible;
            CloseButton.Visibility = System.Windows.Visibility.Visible;

            SidebarBorder.Opacity = 0;
            System.Windows.Media.Animation.DoubleAnimation fadeIn = new(
                0, _sidebarRestoreOpacity, TimeSpan.FromMilliseconds(200));
            fadeIn.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
            SidebarBorder.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            // Animate out — fade the whole sidebar from current opacity to 0.
            _sidebarRestoreOpacity = SidebarBorder.Opacity;
            System.Windows.Media.Animation.DoubleAnimation fadeOut = new(
                SidebarBorder.Opacity, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (_, _) =>
            {
                if (generation != _sidebarAnimationGeneration)
                {
                    return;
                }

                SessionsListBox.Visibility = System.Windows.Visibility.Collapsed;
                TimelineGrid.Visibility = System.Windows.Visibility.Collapsed;
                DetailGrid.Visibility = System.Windows.Visibility.Collapsed;
                CloseButton.Visibility = System.Windows.Visibility.Collapsed;
            };
            SidebarBorder.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeOut);
        }

        CollapseButton.Content = _sidebarExpanded ? "«" : "»";
        CollapseButton.ToolTip = _sidebarExpanded
            ? "Collapse sidebar"
            : "Expand sidebar";
    }

    // ── Compact mode toggle ────────────────────────────────

    private bool _compactMode;

    /// <summary>
    /// Toggles between normal (350px) and compact (260px) sidebar widths,
    /// reducing font sizes and row density in compact mode.
    /// </summary>
    private void ToggleCompactMode(object sender, System.Windows.RoutedEventArgs e)
    {
        _compactMode = !_compactMode;

        double newWidth = _compactMode ? 260 : 350;
        SidebarBorder.Width = newWidth;
        SidebarBorder.Padding = new Thickness(_compactMode ? 7 : 10);

        // Adjust the session list height and overall margin.
        SessionsListBox.Height = _compactMode ? 110 : 150;
        SessionsListBox.MaxHeight = _compactMode ? 110 : 150;

        CompactButton.Content = _compactMode ? "⊞" : "⊟";
        CompactButton.ToolTip = _compactMode
            ? "Switch to normal mode"
            : "Compact mode";
    }

    // ── Sidebar transparency toggle ─────────────────────────

    private void ToggleSidebarOpacity(object sender, System.Windows.RoutedEventArgs e)
    {
        // Cycle: 0.85 → 0.50 → 0.20 → 0.85
        SidebarBorder.Opacity = SidebarBorder.Opacity switch
        {
            >= 0.84 => 0.50,
            >= 0.49 => 0.20,
            _ => 0.85,
        };

        if (sender is System.Windows.Controls.Button btn)
        {
            btn.ToolTip = $"Sidebar: {SidebarBorder.Opacity * 100:F0}%";
        }
    }

    private void EventItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement element &&
            element.DataContext is EventResponse eventItem)
        {
            _viewModel.ScrubToEventTime(eventItem.ReplayTime);
        }
    }

    private void CloseWindow(object sender, System.Windows.RoutedEventArgs e)
    {
        Close();
    }

    private void ExportDiagnostics(object sender, System.Windows.RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "HUD diagnostics (*.json)|*.json",
            FileName = "wotbtreader-hud-diagnostics.json",
            OverwritePrompt = true,
            Title = "Export privacy-safe HUD diagnostics",
        };

        if (dialog.ShowDialog(this) != true)
        {
            _logger.Information("hud.diagnostics.export_cancelled");
            return;
        }

        try
        {
            HudDiagnosticsExportResult result = HudDiagnosticsExporter.Export(
                dialog.FileName,
                _viewModel.CreateDiagnosticsSnapshot(),
                HudLoggerFactory.GetDefaultLogDirectory());
            _viewModel.Status = "Diagnostics exported";
            _logger.Information(
                "hud.diagnostics.exported",
                ("logFileCount", result.LogFileCount),
                ("logRecordCount", result.LogRecordCount),
                ("eventTypeCount", result.EventTypeCount));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _viewModel.Status = "Diagnostics export failed";
            _logger.Failure("hud.diagnostics.export_failed", exception);
        }
    }

    private void OpenDashboardInBrowser(object sender, System.Windows.RoutedEventArgs e)
    {
        string baseUri = _viewModel.BaseUri;
        if (string.IsNullOrEmpty(baseUri))
        {
            _logger.Warning("hud.dashboard.open_skipped", ("reason", "host_not_connected"));
            _viewModel.Status = "No host connection — cannot open dashboard.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = baseUri,
                UseShellExecute = true,
            });
            _logger.Information("hud.dashboard.open_requested");
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Failure("hud.dashboard.open_failed", ex);
            // Browser launch failed silently — the dashboard is a convenience feature.
        }
    }

    // ── Drag-to-move (no title bar) ──────────────────────────

    private void Window_MouseLeftButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    // ── Game window tracking ─────────────────────────────────

    private void OnTrackGameWindow(object? sender, EventArgs e)
    {
        bool wasTracking = IsTrackingGameWindow;
        GameWindowTrackingResult result = GameWindowTrackingCoordinator.Track(
            _gameWindowTracker,
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            wasTracking);

        IsTrackingGameWindow = result.IsTracking;
        _viewModel.SetGameWindowState(result.State);
        if (result.State == ViewModels.HudGameWindowState.NotFound)
        {
            if (wasTracking)
            {
                _logger.Warning("hud.game_window.lost");
            }
            else
            {
                // This event is rate-limited by the HUD logger so a missing
                // game window remains diagnosable without writing every timer
                // tick to disk.
                _logger.Warning("hud.game_window.not_found");
            }
        }
        else if (result.State == ViewModels.HudGameWindowState.Ambiguous)
        {
            _logger.Warning("hud.game_window.ambiguous");
        }
        else if (result.State == ViewModels.HudGameWindowState.BoundsUnavailable)
        {
            _logger.Warning("hud.game_window.bounds_unavailable");
        }
        else if (result.State == ViewModels.HudGameWindowState.BoundsInvalid)
        {
            _logger.Warning("hud.game_window.bounds_invalid");
        }
        else if (result.State == ViewModels.HudGameWindowState.RepositionFailed)
        {
            _logger.Warning("hud.game_window.reposition_failed");
        }

        if (result.TrackingStarted && result.Bounds is GameWindowBounds bounds)
        {
            _logger.Information(
                "hud.game_window.tracking_started",
                ("width", bounds.Width),
                ("height", bounds.Height));
        }
    }
}
