using System.Windows.Threading;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay;

/// <summary>
/// Transparent shell that hosts the loopback overlay surface.
/// Lists battle sessions from the local read API, plots position samples for the
/// selected session, and refreshes the selected session every 2 seconds.
/// A SignalR stream connection provides push-based session list updates;
/// the timer is a fallback.
/// </summary>
public partial class MainWindow : System.Windows.Window, IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly TelemetryStreamService _streamService;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public MainWindow()
    {
        _streamService = new TelemetryStreamService();
        _viewModel = new MainViewModel(
            new Discovery.RendezvousLocator(),
            static baseUri => new TreaderApiClient(baseUri),
            _streamService);
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += (_, _) => _ = _viewModel.RefreshSessionsAsync();
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
        _refreshTimer.Stop();
        _streamService.Dispose();
        GC.SuppressFinalize(this);
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
