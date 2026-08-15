using System.Windows.Threading;
using WotBTreader.Overlay.Logging;

namespace WotBTreader.Overlay;

/// <summary>
/// WPF process entry point and application-level exception boundary.
/// The overlay is a loopback client and does not host an HTTP control plane.
/// </summary>
public partial class App : System.Windows.Application
{
    private IHudLogger? _logger;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _logger = HudLoggerFactory.CreateDefault();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _logger.Information("hud.process.starting");
        base.OnStartup(e);

        try
        {
            var mainWindow = new MainWindow(_logger);
            MainWindow = mainWindow;
            mainWindow.Show();
            _logger.Information("hud.process.started");
        }
        catch (Exception exception)
        {
            _logger.Failure("hud.process.start_failed", exception);
            throw;
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _logger?.Information("hud.process.stopping");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }

        _logger = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Failure("hud.ui.unhandled_exception", e.Exception);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.Failure("hud.process.unhandled_exception", exception);
        }
        else
        {
            _logger?.Failure(
                "hud.process.unhandled_exception",
                null,
                ("exceptionType", e.ExceptionObject?.GetType().Name));
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Failure("hud.task.unobserved_exception", e.Exception);
    }
}
