using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.DependencyInjection;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.Diagnostics;

namespace WotBTreader.Bootstrap.DependencyInjection;

public static class FoundationServiceCollectionExtensions
{
    public static IServiceCollection AddWotBTreaderFoundation(
        this IServiceCollection services,
        TreaderBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        LocalApplicationPaths paths = LocalApplicationPaths.Create(options.ApplicationDataRoot);
        paths.EnsureDirectoriesExist();
        services.AddSingleton(paths);
        services.AddWotBTreaderApplication();
        services.AddSingleton<IDoctorService, DoctorService>();
        services.AddSingleton<IDiagnosticBundleService, DiagnosticBundleService>();
        return services;
    }
}
