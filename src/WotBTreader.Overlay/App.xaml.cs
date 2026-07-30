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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
