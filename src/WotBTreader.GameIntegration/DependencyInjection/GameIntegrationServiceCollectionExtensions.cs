using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WotBTreader.Application.Game;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.DependencyInjection;

/// <summary>Registers the read-only installed-game and native-log adapters.</summary>
public static class GameIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Adds game integration with explicit immutable options. No filesystem is
    /// accessed until a registered service is invoked.
    /// </summary>
    public static IServiceCollection AddGameIntegration(
        this IServiceCollection services,
        GameIntegrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IDvplReader, DvplReader>();
        services.TryAddSingleton<IGameInstallationDiscovery, GameInstallationDiscovery>();
        services.TryAddSingleton<IInstalledGameMetadataProvider, InstalledGameMetadataProvider>();
        services.TryAddSingleton<IBlitzReplayLifecycleParser, BlitzReplayLifecycleParser>();
        services.TryAddTransient<IBlitzReplayLogMonitor, BlitzReplayLogMonitor>();
        return services;
    }
}
