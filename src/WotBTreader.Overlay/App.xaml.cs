using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using WotBTreader.Overlay.Endpoints;
using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay;

/// <summary>
/// WPF process entry point and application-level exception boundary.
/// Starts the embedded Kestrel HTTP API on a loopback port for automation.
/// </summary>
public partial class App : System.Windows.Application
{
    private WebApplication? _webApp;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        OverlayApiState.Instance.Register(
            ((MainWindow)MainWindow).ViewModel);

        StartOverlayApi();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_webApp is not null)
        {
            await _webApp.StopAsync();
            await _webApp.DisposeAsync();
        }

        base.OnExit(e);
    }

    private void StartOverlayApi()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 9190);
        });

        WebApplication app = builder.Build();
        app.MapOverlayApi();

        _webApp = app;
        _ = app.RunAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                // Kestrel startup failure is non-fatal for the HUD.
                // Log the inner exception so the developer can diagnose
                // port conflicts or address-in-use errors.
                foreach (Exception inner in t.Exception.InnerExceptions)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[OverlayApi] Kestrel startup failed: {inner.Message}");
                }
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
