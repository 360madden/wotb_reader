using System.Windows.Threading;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay;

/// <summary>
/// Transparent shell that hosts the loopback overlay surface.
/// Lists battle sessions from the local read API, plots position samples for the
/// selected session, and refreshes the selected session every 2 seconds.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += (_, _) => _ = _viewModel.RefreshSessionsAsync();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel.SelectedSession is not null)
        {
            _ = _viewModel.RefreshSelectedAsync();
        }
    }
}
