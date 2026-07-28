using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay;

/// <summary>
/// WPF process entry point and application-level exception boundary.
/// The overlay is a loopback client and does not host an HTTP control plane.
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Create the main window explicitly so ViewModel is available before
        // the window is shown. WPF's StartupUri creates the window AFTER
        // OnStartup returns, so MainWindow would be null at this point.
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        OverlayApiState.Instance.Register(
            mainWindow.ViewModel,
            action => Dispatcher.BeginInvoke(action));

        mainWindow.Show();
    }
}
