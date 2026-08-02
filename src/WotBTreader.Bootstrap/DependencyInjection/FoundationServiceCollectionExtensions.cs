using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WotBTreader.Application.DependencyInjection;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.Diagnostics;
using WotBTreader.Bootstrap.Startup;
using WotBTreader.CaptureLogs.DependencyInjection;
using WotBTreader.GameIntegration;
using WotBTreader.GameIntegration.DependencyInjection;
using WotBTreader.GameIntegration.Session;
using WotBTreader.Replays;
using WotBTreader.Storage.Sqlite;

namespace WotBTreader.Bootstrap.DependencyInjection;

public static class FoundationServiceCollectionExtensions
{
    /// <summary>
    /// Registers every port a host resolves: application orchestration, the
    /// storage, replay, capture, and game adapters, and local diagnostics.
    /// Hosts must not register adapters themselves; this is the single place
    /// where the dependency direction is closed.
    /// </summary>
    public static IServiceCollection AddWotBTreaderFoundation(
        this IServiceCollection services,
        TreaderBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        LocalApplicationPaths paths = LocalApplicationPaths.Create(options.ApplicationDataRoot);
        paths.EnsureDirectoriesExist();
        services.AddSingleton(paths);

        // Adapters resolve ILogger<T>; a host that configures Serilog later still
        // wins because the logging builder registrations are additive.
        services.AddLogging();
        services.AddWotBTreaderApplication();

        // Bootstrap owns the on-disk layout so the adapter cannot drift from the
        // paths the doctor and diagnostic bundle report.
        services.AddSqliteStorage(new SqliteStorageOptions
        {
            ApplicationDataRoot = paths.Root,
            ContentRoot = paths.ContentStore,
            DatabasePath = paths.Database,
        });
        services.AddReplayDecoding();
        services.AddCaptureLogs();
        services.AddGameIntegration(CreateGameIntegrationOptions(options, paths));

        services.AddSingleton<IDoctorService, DoctorService>();
        services.AddSingleton<IDiagnosticBundleService, DiagnosticBundleService>();
        services.AddHostedService<StorageInitializationHostedService>();
        return services;
    }

    private static GameIntegrationOptions CreateGameIntegrationOptions(
        TreaderBootstrapOptions options,
        LocalApplicationPaths paths) =>
        new()
        {
            GameInstallRoots = string.IsNullOrWhiteSpace(options.GameRoot)
                ? []
                : [options.GameRoot],
            UserDataRoots = string.IsNullOrWhiteSpace(options.GameUserDataRoot)
                ? []
                : [options.GameUserDataRoot],
            ReplayLaunchStagingRoot = ReplayLaunchStagingPaths.Resolve(
                options.GameUserDataRoot,
                paths.Root),
            LifecycleEvidenceTimeout =
                options.LifecycleEvidenceTimeout ?? TimeSpan.FromSeconds(45),
            OfflineReplayEvidenceLifetime =
                options.OfflineReplayEvidenceLifetime ?? TimeSpan.FromSeconds(15),
        };

    /// <summary>
    /// Narrow composition for tooling that only needs replay probe and decoding.
    /// Registers <see cref="ReplayDecoderRegistry"/> and calls
    /// <see cref="ReplayDecodingServiceCollectionExtensions.AddReplayDecoding"/>.
    /// No filesystem side effects, no logging, no storage, no full application
    /// stack.
    /// </summary>
    public static IServiceCollection AddWotBTreaderReplayTooling(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ReplayDecoderRegistry>();
        services.AddReplayDecoding();
        return services;
    }
}
