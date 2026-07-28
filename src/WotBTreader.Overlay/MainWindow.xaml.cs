using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private readonly DispatcherTimer _playbackTimer;
    private bool _disposed;
    private bool _isQuickLaunching;
    private Process? _webHostProcess;
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
        _windowTrackTimer.Start();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _playbackTimer.Tick += OnPlaybackTick;
    }

    /// <summary>The MainViewModel, exposed for the embedded HTTP API.</summary>
    internal ViewModels.MainViewModel ViewModel => _viewModel;

    /// <summary>
    /// True when the game window has been found and the overlay is tracking it.
    /// Set by the window-track timer callback.
    /// </summary>
    internal bool IsTrackingGameWindow { get; private set; }

    /// <summary>
    /// Public entry point for the overlay HTTP API to trigger a quick-launch
    /// by replay path. Delegates to the private implementation.
    /// </summary>
    internal async Task QuickLaunchWithPathViaApiAsync(string replayPath)
    {
        if (_isQuickLaunching)
        {
            _viewModel.Status = "Already launching — request ignored";
            return;
        }

        _isQuickLaunching = true;
        try
        {
            await QuickLaunchWithPathAsync(replayPath);
        }
        finally
        {
            _isQuickLaunching = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refreshTimer.Stop();
        _windowTrackTimer.Stop();
        _playbackTimer.Stop();
        _streamService.Dispose();
        if (_webHostProcess is not null)
        {
            _webHostProcess.Exited -= OnWebHostExited;
            _webHostProcess.Dispose();
            _webHostProcess = null;
        }

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

        PopulateGamePathInfo();
    }

    private void SearchText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_viewModel.SearchText)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void PopulateGamePathInfo()
    {
        string? gamePath = FindGameExecutablePath();
        string gameText = gamePath is not null
            ? $"🎮 {System.IO.Path.GetDirectoryName(gamePath)}"
            : "🎮 wotblitz.exe not found — set WOTB_GAME_PATH";

        string replaysText = System.IO.Directory.Exists(GameReplaysFolder)
            ? $"📁 {GameReplaysFolder}"
            : "📁 replays folder not found";

        GamePathInfo.Text = $"{gameText}  |  {replaysText}";
        string newline = Environment.NewLine;
        GamePathInfo.ToolTip = $"Game: {(gamePath ?? "not found")}{newline}Replays: {GameReplaysFolder}{newline}{newline}Set WOTB_GAME_PATH env var to override game location.{newline}Drag .wotbreplay files onto this window to quick-launch.";
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

    private void OnWebHostExited(object? sender, EventArgs e)
    {
        _viewModel.Status = "Web host stopped unexpectedly";
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
            element.DataContext is Contracts.EventResponse eventItem)
        {
            _viewModel.ScrubToEventTime(eventItem.ReplayTime);
        }
    }

    // ── One-click launcher ──────────────────────────────────

    private async void QuickLaunch_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isQuickLaunching)
        {
            _viewModel.Status = "Already launching — drop ignored";
            return;
        }

        _isQuickLaunching = true;
        try
        {
            await QuickLaunchCoreAsync();
        }
        finally
        {
            _isQuickLaunching = false;
        }
    }

    private async Task QuickLaunchCoreAsync()
    {
        // Default the dialog to the game replays folder if it exists.
        string initialDir = System.IO.Directory.Exists(GameReplaysFolder)
            ? GameReplaysFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        OpenFileDialog dialog = new()
        {
            Title = "Pick a WoT Blitz replay",
            Filter = "WoT Blitz Replay (*.wotbreplay)|*.wotbreplay|All Files (*.*)|*.*",
            DefaultExt = ".wotbreplay",
            InitialDirectory = initialDir,
        };

        if (dialog.ShowDialog() != true) return;
        string replayPath = dialog.FileName;

        await QuickLaunchWithPathAsync(replayPath);
    }

    private async Task QuickLaunchWithPathAsync(string replayPath)
    {
        if (!System.IO.File.Exists(replayPath))
        {
            _viewModel.Status = "Replay file no longer exists";
            return;
        }

        string replayFileName = System.IO.Path.GetFileName(replayPath);
        _viewModel.Status = $"⚙ Launching {replayFileName}…";

        try
        {
            // ── 1. Resolve paths ─────────────────────────────
            string? repoRoot = ResolveRepoRoot();
            string? dataRoot = ResolveDataRoot(repoRoot);
            if (dataRoot is null)
            {
                _viewModel.Status = "❌ Cannot determine data root — set WOTBTREADER_DATA_ROOT";
                return;
            }

            // ── 2. Start web host if needed ──────────────────
            if (!await IsWebHostRunningAsync())
            {
                _viewModel.Status = "🔧 Starting web host…";
                if (!TryStartWebHost(repoRoot, dataRoot))
                {
                    _viewModel.Status = "❌ Host not built — run build.cmd then serve.cmd first";
                    return;
                }

                _viewModel.Status = "⏳ Waiting for host…";
                if (!await WaitForWebHostAsync(TimeSpan.FromSeconds(25)))
                {
                    _viewModel.Status = "❌ Web host did not start — check the host console window";
                    return;
                }
            }

            // ── 3. Refresh sessions so overlay connects ───────
            _viewModel.Status = "🔗 Connecting to host…";
            await _viewModel.RefreshSessionsAsync();
            if (string.IsNullOrEmpty(_viewModel.BaseUri))
            {
                _viewModel.Status = "❌ Could not connect to web host — is serve running?";
                return;
            }

            // ── 4. Import replay via CLI ─────────────────────
            _viewModel.Status = "📥 Importing replay…";
            (bool imported, string importMessage) = await ImportReplayViaCliAsync(repoRoot, dataRoot, replayPath);
            if (!imported)
            {
                _viewModel.Status = $"❌ {importMessage}";
                return;
            }

            // ── 5. Refresh sessions to see the new one ───────
            _viewModel.Status = "🔄 Refreshing sessions…";
            await _viewModel.RefreshSessionsAsync();

            // ── 6. Copy to game folder and launch ────────────
            _viewModel.Status = "🚀 Launching game…";
            string? gamePath = FindGameExecutablePath();
            if (gamePath is null)
            {
                _viewModel.Status = "❌ wotblitz.exe not found — set WOTB_GAME_PATH env var";
                return;
            }

            if (!LaunchGameWithReplayPath(replayPath))
            {
                _viewModel.Status = "❌ Failed to launch game";
                return;
            }

            _viewModel.Status = $"✅ {replayFileName} — tracking game window";
        }
        catch (Exception ex)
        {
            _viewModel.Status = $"❌ Launch failed: {ex.GetType().Name}";
        }
    }

    private static string? ResolveRepoRoot()
    {
        // Walk up from the exe directory until we find WotBTreader.sln,
        // the reliable marker file at the repository root.
        string? current = System.IO.Path.GetDirectoryName(
            Environment.ProcessPath ?? typeof(MainWindow).Assembly.Location);

        for (int i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(current, "WotBTreader.sln")))
            {
                return current;
            }

            current = System.IO.Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string? ResolveDataRoot(string? repoRoot)
    {
        string? customRoot = Environment.GetEnvironmentVariable("WOTBTREADER_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            return System.IO.Path.GetFullPath(customRoot);
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return System.IO.Path.Combine(repoRoot, ".data");
        }

        return null;
    }

    private async ValueTask<bool> IsWebHostRunningAsync()
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            HttpResponseMessage response = await client.GetAsync(
                $"{_lastDashboardUri}/api/v1/sessions",
                HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool TryStartWebHost(string? repoRoot, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return false;
        }

        string publishDir = System.IO.Path.Combine(repoRoot, ".build", "publish");
        string hostPath = System.IO.Path.Combine(publishDir, "WotBTreader.Host.Web.exe");

        if (!System.IO.File.Exists(hostPath))
        {
            return false;
        }

        try
        {
            if (_webHostProcess is not null)
            {
                _webHostProcess.Exited -= OnWebHostExited;
                _webHostProcess.Dispose();
            }

            _webHostProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = hostPath,
                    WorkingDirectory = publishDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            _webHostProcess.Exited += OnWebHostExited;

            _webHostProcess.StartInfo.Environment["Web__Port"] = "9182";
            _webHostProcess.StartInfo.Environment["Paths__ApplicationDataRoot"] = dataRoot;
            _webHostProcess.Start();
            return true;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or System.IO.IOException)
        {
            _webHostProcess?.Dispose();
            _webHostProcess = null;
            return false;
        }
    }

    private async ValueTask<bool> WaitForWebHostAsync(TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };

        while (sw.Elapsed < timeout)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(
                    $"{_lastDashboardUri}/api/v1/sessions",
                    HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode || (int)response.StatusCode >= 400)
                {
                    return true; // Host is accepting connections.
                }
            }
            catch
            {
                // Not ready yet.
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static async ValueTask<(bool Success, string Message)> ImportReplayViaCliAsync(
        string? repoRoot,
        string dataRoot,
        string replayPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return (false, "Cannot determine repo root");
        }

        string cliPath = System.IO.Path.Combine(
            repoRoot, ".build", "publish", "WotBTreader.Host.Cli.exe");

        if (!System.IO.File.Exists(cliPath))
        {
            return (false, "CLI not built — run build.cmd first");
        }

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = $"import \"{replayPath}\" --json --data-root \"{dataRoot}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return (true, string.Empty);
            }

            // Try to extract a user-safe error from the JSON envelope.
            string errorMessage = ExtractCliErrorMessage(stdout) ?? "Import failed";
            return (false, errorMessage);
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or System.IO.IOException)
        {
            return (false, ex.GetType().Name);
        }
    }

    private static string? ExtractCliErrorMessage(string json)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out System.Text.Json.JsonElement errors)
                && errors.GetArrayLength() > 0)
            {
                System.Text.Json.JsonElement first = errors[0];
                if (first.TryGetProperty("message", out System.Text.Json.JsonElement message))
                {
                    return message.GetString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON — just return null.
        }

        return null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:MarkMembersAsStatic", Justification = "Instance method for consistency with calling pattern.")]
    private bool LaunchGameWithReplayPath(string replayPath)
    {
        try
        {
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

            // Launch via file association — wotblitz.exe does not accept replay
            // paths as command-line arguments. Using Process.Start on the
            // .wotbreplay file itself triggers the Windows file association,
            // which launches the game with the replay visible in its UI.
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception ex) when (
            ex is System.IO.IOException or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // Win32Exception covers "No application is associated" when
            // file association is missing. In that case, try launching
            // the game directly as a fallback.
            if (ex is System.ComponentModel.Win32Exception)
            {
                string? gamePath = FindGameExecutablePath();
                if (gamePath is not null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = gamePath,
                            UseShellExecute = true,
                        });
                        return true;
                    }
                    catch
                    {
                        // Both approaches failed.
                    }
                }
            }

            return false;
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

    // ── Drag-and-drop file import ───────────────────────────

    private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        // Take the first .wotbreplay file dropped.
        string? replayFile = null;
        foreach (string file in files)
        {
            if (file.EndsWith(".wotbreplay", StringComparison.OrdinalIgnoreCase))
            {
                replayFile = file;
                break;
            }

            // Also accept any file if it's the only one dropped.
            if (files.Length == 1)
            {
                replayFile = file;
                break;
            }
        }

        if (replayFile is null || !System.IO.File.Exists(replayFile)) return;

        if (_isQuickLaunching)
        {
            _viewModel.Status = "Already launching — drop ignored";
            return;
        }

        _isQuickLaunching = true;
        try
        {
            await QuickLaunchWithPathAsync(replayFile);
        }
        finally
        {
            _isQuickLaunching = false;
        }
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
        OverlayApiState.Instance.IsTrackingGameWindow = IsTrackingGameWindow;
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
        if (_isQuickLaunching)
        {
            _viewModel.Status = "Already launching — please wait";
            return false;
        }

        string? replayPath = FindReplayFile();
        return replayPath is not null && LaunchGameWithReplayPath(replayPath);
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
