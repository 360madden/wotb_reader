using System.Windows.Threading;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay;

/// <summary>
/// <para><b>Design intent (NOT YET IMPLEMENTED):</b> This window is intended to
/// be a <b>transparent, borderless, always-on-top heads-up display (HUD)</b>
/// that sits on top of the WoT Blitz game while it plays back a pre-recorded
/// replay. The position plot renders team-coloured dots over the game's
/// minimap area, and a compact semi-transparent panel shows session metadata
/// and controls. The game window is tracked via P/Invoke so the overlay stays
/// perfectly aligned during playback.</para>
///
/// <para><b>Current state:</b> The window is a standard opaque WPF window with
/// a toolbar and TabControl (Position Plot + WebView2 Dashboard). The
/// transparency, borderless styling, and game-window tracking have not yet
/// been implemented. See <c>docs/architecture/overview.md</c> for the full
/// HUD design specification.</para>
///
/// <para>Lists battle sessions from the local read API, plots position samples
/// for the selected session (stride-sampled to 2000 dots, team-coloured), and
/// refreshes every 2 seconds. A SignalR stream provides push-based session
/// list updates; the timer is a fallback. A WebView2 tab embeds the Blazor
/// dashboard for session diagnostics.</para>
/// </summary>
public partial class MainWindow : System.Windows.Window, IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly TelemetryStreamService _streamService;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;
    private bool _webViewInitialized;
    private Uri? _navigatedUri;

    public MainWindow()
    {
        _streamService = new TelemetryStreamService();
        _viewModel = new MainViewModel(
            new Discovery.RendezvousLocator(),
            static baseUri => new TreaderApiClient(baseUri),
            _streamService);
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        Closed += OnClosed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refreshTimer.Stop();
        _streamService.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Kick off the initial rendezvous discovery and session load.
        _ = _viewModel.RefreshSessionsAsync();

        // Initialise WebView2 so it's ready when BaseUri becomes available.
        await InitialiseWebViewAsync();
    }

    private async Task InitialiseWebViewAsync()
    {
        try
        {
            string userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WotBTreader",
                "WebView2");

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(userDataFolder: userDataFolder);
            await DashboardView.EnsureCoreWebView2Async(env);

            _webViewInitialized = true;
            DashboardView.Visibility = System.Windows.Visibility.Visible;
            DashboardFallback.Visibility = System.Windows.Visibility.Collapsed;

            // If BaseUri was set before WebView2 finished initialising, navigate now.
            string currentBaseUri = _viewModel.BaseUri;
            if (!string.IsNullOrEmpty(currentBaseUri))
            {
                NavigateDashboard(currentBaseUri);
            }
        }
        catch (Exception)
        {
            // WebView2 may fail to initialise on systems without the Evergreen
            // runtime. Keep the fallback message visible and the Position Plot
            // tab remains fully functional.
            DashboardView.Visibility = System.Windows.Visibility.Collapsed;
            DashboardFallback.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.BaseUri))
        {
            return;
        }

        string baseUri = _viewModel.BaseUri;
        if (!string.IsNullOrEmpty(baseUri) && _webViewInitialized)
        {
            NavigateDashboard(baseUri);
        }
    }

    private void NavigateDashboard(string baseUri)
    {
        Uri target = new(baseUri);
        if (_navigatedUri == target)
        {
            return;
        }

        _navigatedUri = target;
        DashboardView.CoreWebView2.Navigate(target.ToString());
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel.SelectedSession is not null)
        {
            _ = _viewModel.RefreshSelectedAsync();
        }
    }
}
