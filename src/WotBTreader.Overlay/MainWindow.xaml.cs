using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WotBTreader.ApiContracts;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

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
    private const string GameWindowTitle = "World of Tanks Blitz";

    private readonly MainViewModel _viewModel;
    private readonly TelemetryStreamService _streamService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _windowTrackTimer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _hpPulseTimer;
    private bool _disposed;

    public MainWindow()
    {
        _streamService = new TelemetryStreamService();
        _viewModel = new MainViewModel(
            new Discovery.RendezvousLocator(),
            static (baseUri, capability) => new TreaderApiClient(baseUri, capability: capability),
            _streamService);
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Nameplates.CollectionChanged += OnNameplatesChanged;
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Nameplates.CollectionChanged -= OnNameplatesChanged;
        _refreshTimer.Stop();
        _windowTrackTimer.Stop();
        _playbackTimer.Stop();
        _hpPulseTimer.Stop();
        _streamService.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Window lifecycle ─────────────────────────────────────

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            _viewModel.Status = $"Startup error: {ex.GetType().Name}";
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
        if (_viewModel.SelectedSession is not null)
            _ = _viewModel.RefreshSelectedAsync();
    }

    private void OnNameplatesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RenderW2sNameplates();
    }

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying))
        {
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
        else if (e.PropertyName == nameof(MainViewModel.HasLiveMemoryObservation))
        {
            if (_viewModel.HasLiveMemoryObservation && _viewModel.LivePlayerHP is int hp && hp > 0)
            {
                _hpPulseTimer.Start();
            }
            else
            {
                _hpPulseTimer.Stop();
            }
        }
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        _viewModel.AdvancePlayback();
        RefreshW2sFrame();
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

    /// <summary>Renders the latest view-model nameplates onto the HUD canvas.</summary>
    private void RenderW2sNameplates()
    {
        W2sHudView.Render(_viewModel.Nameplates, ActualWidth, ActualHeight);
    }

    private void OnHpPulseTick(object? sender, EventArgs e)
    {
        // Pulse the HP text block foreground between green and yellow when
        // HP is critically low (below ~30% of a typical heavy/medium tank).
        if (!_viewModel.HasLiveMemoryObservation || _viewModel.LivePlayerHP is not int hp)
        {
            _hpPulseTimer.Stop();
            return;
        }

        // Approximate max HP for a typical Blitz heavy/medium.
        const int approximateMaxHp = 2500;
        if (hp > approximateMaxHp * 0.3)
        {
            _hpPulseTimer.Stop();
            return;
        }

        // The HP overlay uses the XAML-defined foreground (#00FF64) which
        // we can't easily modify from code-behind without breaking bindings.
        // The pulse is handled by the FastPlotRenderer's live-player glow
        // which draws a pulsing green ring at the player's position — this
        // visual feedback is more visible than a text color change on a
        // transparent overlay.
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

    private void OpenDashboardInBrowser(object sender, System.Windows.RoutedEventArgs e)
    {
        string baseUri = _viewModel.BaseUri;
        if (string.IsNullOrEmpty(baseUri))
        {
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
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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

    // ── Game window tracking (P/Invoke) ──────────────────────

#pragma warning disable CA2101

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

#pragma warning restore CA2101

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private void OnTrackGameWindow(object? sender, EventArgs e)
    {
        IntPtr hwnd = FindWindowW(null, GameWindowTitle);
        IsTrackingGameWindow = hwnd != IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out RECT rect)) return;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        _ = SetWindowPos(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            HWND_TOPMOST, rect.Left, rect.Top, w, h, SWP_NOACTIVATE);
    }
}
