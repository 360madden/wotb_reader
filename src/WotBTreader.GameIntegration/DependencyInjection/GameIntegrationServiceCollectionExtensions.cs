using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WotBTreader.Application.Game;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Metadata;
using WotBTreader.GameIntegration.Session;
using WotBTreader.UltimateScanner;

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

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBlitzReplayLifecycleFeed, BlitzReplayLifecycleFeed>();
        services.TryAddSingleton<GameSessionCoordinator>();
        services.TryAddSingleton<IWindowsExecutableFingerprintReader, WindowsExecutableFingerprintReader>();
        services.TryAddSingleton<ITrustedGameIdentityProvider, TrustedGameIdentityProvider>();
        services.TryAddSingleton<ILaunchCorrelationGenerator, LaunchCorrelationGenerator>();
        services.TryAddSingleton<IManagedLaunchPreparer, ManagedLaunchPreparer>();
        services.TryAddSingleton<IReplayLaunchStageNameGenerator, ReplayLaunchStageNameGenerator>();
        services.TryAddSingleton<IReplayLaunchStagingPlatform, WindowsReplayLaunchStagingPlatform>();
        services.TryAddSingleton<IManagedReplayArtifactStager, ManagedReplayArtifactStager>();
        services.TryAddSingleton<IGameProcessQueryPlatform, WindowsGameProcessQueryPlatform>();
        services.TryAddSingleton<IGameProcessIdentityObserver, GameProcessIdentityObserver>();
        services.TryAddSingleton<IGameProcessModuleBaseAddressResolver, WindowsGameProcessModuleBaseAddressResolver>();
        services.TryAddSingleton<ISuspendedProcessPlatform, WindowsSuspendedProcessPlatform>();
        services.TryAddSingleton<IManagedLaunchCorrelationRegistrar, ManagedLaunchCorrelationRegistrar>();
        services.TryAddSingleton<IThreadResumePlatform, WindowsThreadResumePlatform>();
        services.TryAddSingleton<IGuardedMemoryReaderFactory, GuardedMemoryReaderFactory>();
        services.TryAddSingleton<IInstructionSnapshotRunner, WindowsInstructionSnapshotRunner>();
        services.TryAddSingleton<IMemoryScanDiscoverer, MemoryScanDiscoverer>();
        services.TryAddSingleton<MemoryScanEngine>();

        services.TryAddSingleton<IGameSessionState>(
            sp => sp.GetRequiredService<GameSessionCoordinator>());
        services.TryAddSingleton<IGameReplayLauncher>(
            sp => sp.GetRequiredService<GameSessionCoordinator>());
        services.TryAddSingleton<IGameMemoryObserver>(
            sp => sp.GetRequiredService<GameSessionCoordinator>());
        services.TryAddSingleton<IGameMemoryScanner>(
            sp => sp.GetRequiredService<GameSessionCoordinator>());
        services.TryAddSingleton<IGameProcessLauncher, GameProcessLauncher>();

        return services;
    }
}
