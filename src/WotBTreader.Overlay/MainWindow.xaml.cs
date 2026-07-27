using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay;

/// <summary>
/// Transparent, borderless, always-on-top HUD that sits over the WoT Blitz game
/// during replay playback. Plots team-coloured position dots over the game's
/// minimap area, with a floating semi-transparent panel for session selection
/// and controls. Tracks the game window via P/Invoke so the overlay stays
/// aligned during playback.
/// </summary>
public partial class MainWindow : System.Windows.Window, IDisposable
{
    private const string GameWindowTitle = "World of Tanks Blitz";
    private const string GameExecutableName = "wotblitz.exe";
    private static readonly string GameReplaysFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "wotblitz", "DAVAProject", "replays");

    private readonly MainViewModel _viewModel;
    private readonly TelemetryStreamService _streamService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _windowTrackTimer;
    private bool _disposed;
    private string _lastDashboardUri = "http://127.0.0.1:9182";

    public MainWindow()
    {
        _streamService = new TelemetryStreamService();
        _viewModel = new MainViewModel(
            new Discovery.RendezvousLocator(),
            static baseUri => new TreaderApiClient(baseUri),
            _streamService,
            LaunchGameWithSelectedReplay);
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refreshTimer.Stop();
        _windowTrackTimer.Stop();
        _streamService.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Window lifecycle ─────────────────────────────────────

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _ = _viewModel.RefreshSessionsAsync();
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
        if (e.PropertyName == nameof(MainViewModel.BaseUri) &&
            !string.IsNullOrEmpty(_viewModel.BaseUri))
        {
            _lastDashboardUri = _viewModel.BaseUri;
        }
    }

    // ── Button handlers ──────────────────────────────────────

    private void CloseWindow(object sender, System.Windows.RoutedEventArgs e)
    {
        Close();
    }

    private void OpenDashboardInBrowser(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastDashboardUri,
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
        if (hwnd == IntPtr.Zero) return;
        if (!GetWindowRect(hwnd, out RECT rect)) return;

        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        _ = SetWindowPos(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            HWND_TOPMOST, rect.Left, rect.Top, w, h, SWP_NOACTIVATE);
    }

    // ── Game launching ──────────────────────────────────────

    private bool LaunchGameWithSelectedReplay(SessionRow? session)
    {
        if (session is null) return false;

        try
        {
            string? replayPath = FindReplayFile();
            if (replayPath is null) return false;

            System.IO.Directory.CreateDirectory(GameReplaysFolder);
            string target = System.IO.Path.Combine(
                GameReplaysFolder,
                System.IO.Path.GetFileName(replayPath));

            if (!string.Equals(
                System.IO.Path.GetFullPath(replayPath),
                System.IO.Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
            {
                System.IO.File.Copy(replayPath, target, overwrite: true);
            }

            string? gamePath = FindGameExecutablePath();
            if (gamePath is null) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = gamePath,
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });

            _windowTrackTimer.Start();
            return true;
        }
        catch (Exception ex) when (
            ex is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindReplayFile()
    {
        if (!System.IO.Directory.Exists(GameReplaysFolder)) return null;

        string[] files = System.IO.Directory.GetFiles(
            GameReplaysFolder, "*.wotbreplay",
            System.IO.SearchOption.TopDirectoryOnly);

        if (files.Length == 0) return null;

        string newest = files[0];
        DateTime newestTime = System.IO.File.GetLastWriteTimeUtc(newest);
        for (int i = 1; i < files.Length; i++)
        {
            DateTime t = System.IO.File.GetLastWriteTimeUtc(files[i]);
            if (t > newestTime)
            {
                newestTime = t;
                newest = files[i];
            }
        }

        return newest;
    }

    // ── Game executable path discovery ───────────────────────

    /// <summary>
    /// Finds wotblitz.exe using environment variable, default install roots,
    /// and a hardcoded fallback. Does NOT depend on GameIntegration to keep
    /// the Overlay isolated from parser/storage adapters.
    /// </summary>
    private static string? FindGameExecutablePath()
    {
        // 1. Environment variable override.
        string? envPath = Environment.GetEnvironmentVariable("WOTB_GAME_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && System.IO.File.Exists(envPath))
        {
            return envPath;
        }

        // 2. Default discovery roots (mirrors GameInstallationDiscovery logic).
        foreach (string root in GetGameDiscoveryRoots())
        {
            string candidate = System.IO.Path.Combine(root, GameExecutableName);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 3. Hardcoded fallback.
        string fallback = @"C:\Games\World_of_Tanks_Blitz\wotblitz.exe";
        return System.IO.File.Exists(fallback) ? fallback : null;
    }

    private static string[] GetGameDiscoveryRoots()
    {
        System.Collections.Generic.List<string> roots = [];

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            string systemDrive = System.IO.Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            roots.Add(System.IO.Path.Combine(systemDrive, "Games", "World_of_Tanks_Blitz"));

            string? programFilesX86 = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                roots.Add(System.IO.Path.Combine(
                    programFilesX86, "Steam", "steamapps", "common", "World of Tanks Blitz"));
            }

            string? programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                roots.Add(System.IO.Path.Combine(
                    programFiles, "Steam", "steamapps", "common", "World of Tanks Blitz"));
            }
        }

        return [.. roots];
    }
}
