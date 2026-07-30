using System.Diagnostics;
using System.Runtime.InteropServices;
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
        _refreshTimer.Stop();
        _windowTrackTimer.Stop();
        _playbackTimer.Stop();
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
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        _viewModel.AdvancePlayback();
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

    private void ToggleSidebarCollapse(object sender, System.Windows.RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;

        System.Windows.Visibility visibility = _sidebarExpanded
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        SessionsListBox.Visibility = visibility;
        TimelineGrid.Visibility = visibility;
        DetailGrid.Visibility = visibility;
        CloseButton.Visibility = visibility;

        CollapseButton.Content = _sidebarExpanded ? "«" : "»";
        CollapseButton.ToolTip = _sidebarExpanded
            ? "Collapse sidebar"
            : "Expand sidebar";
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
