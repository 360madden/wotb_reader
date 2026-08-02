using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Storage;
using WotBTreader.Application.Streaming;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.DependencyInjection;
using WotBTreader.GameIntegration;

namespace WotBTreader.Bootstrap.Tests;

/// <summary>
/// Guards the composition root itself. Every other test project exercises one
/// adapter in isolation, so only these tests prove that a host can actually
/// resolve the ports it depends on at runtime.
/// </summary>
[TestClass]
public sealed class CompositionRootTests
{
    private static readonly Type[] PublishedPorts =
    [
        typeof(IDoctorService),
        typeof(IDiagnosticBundleService),
        typeof(IReplayIngestionService),
        typeof(ITelemetryEventPublisher),
        typeof(ReplayDecoderRegistry),
        typeof(IReplayProbe),
        typeof(IStorageInitializer),
        typeof(ISourceArtifactStore),
        typeof(IDecodeRunRepository),
        typeof(ISessionQueryRepository),
        typeof(IComparisonRunRepository),
        typeof(IReplayClockSegmentRepository),
        typeof(ITelemetrySource),
        typeof(ITelemetryNormalizer),
        typeof(ITelemetryComparator),
        typeof(IReplayClockSource),
        typeof(ITelemetryCaptureWriter),
        typeof(IInstalledGameMetadataProvider),
        typeof(IGameSessionState),
        typeof(IGameReplayLauncher),
        typeof(IGameMemoryObserver),
        typeof(IGameMemoryScanner),
        typeof(IGameProcessLauncher),
    ];

    [TestMethod]
    public void FoundationBuildsWithConstructorAndScopeValidation()
    {
        using TemporaryRoot root = new();

        // ValidateOnBuild surfaces an unregistered dependency of any registered
        // service, and ValidateScopes rejects captive dependencies.
        using ServiceProvider provider = BuildProvider(root);

        Assert.IsNotNull(provider);
    }

    [TestMethod]
    public void FoundationResolvesEveryPublishedPort()
    {
        using TemporaryRoot root = new();
        using ServiceProvider provider = BuildProvider(root);
        using IServiceScope scope = provider.CreateScope();

        foreach (Type port in PublishedPorts)
        {
            Assert.IsNotNull(
                scope.ServiceProvider.GetRequiredService(port),
                $"{port.Name} could not be resolved from the composition root.");
        }
    }

    [TestMethod]
    public void FoundationUsesOneGameSessionCoordinator()
    {
        using TemporaryRoot root = new();
        using ServiceProvider provider = BuildProvider(root);

        IGameSessionState state = provider.GetRequiredService<IGameSessionState>();
        IGameReplayLauncher launcher = provider.GetRequiredService<IGameReplayLauncher>();
        IGameMemoryObserver observer = provider.GetRequiredService<IGameMemoryObserver>();
        IGameMemoryScanner scanner = provider.GetRequiredService<IGameMemoryScanner>();

        Assert.IsTrue(ReferenceEquals(state, launcher));
        Assert.IsTrue(ReferenceEquals(state, observer));
        Assert.IsTrue(ReferenceEquals(state, scanner));
    }

    [TestMethod]
    public void FoundationRegistersTheStrictReplayDecoder()
    {
        using TemporaryRoot root = new();
        using ServiceProvider provider = BuildProvider(root);

        IReplayDecoder[] decoders = [.. provider.GetServices<IReplayDecoder>()];

        Assert.IsNotEmpty(decoders);
        Assert.IsTrue(
            decoders.Any(static decoder => decoder.Descriptor.Id.Length > 0),
            "A registered decoder must expose a stable descriptor identifier.");
    }

    [TestMethod]
    public void ReplayToolingComposition_ValidatesOnBuildAndResolvesToolingPorts()
    {
        ServiceCollection services = new();
        services.AddWotBTreaderReplayTooling();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // Prove IReplayProbe resolves — the adapter is wired via AddReplayDecoding.
        Assert.IsNotNull(provider.GetRequiredService<IReplayProbe>());

        ReplayDecoderRegistry registry = provider.GetRequiredService<ReplayDecoderRegistry>();
        IReplayDecoder[] decoders = [.. provider.GetServices<IReplayDecoder>()];
        Assert.IsNotEmpty(decoders);

        var supportedProbe = new ReplayProbeResult(
            IsReplay: true,
            GameVersion: "11.18.0.7",
            FormatVersion: "1",
            ArchiveEntries: [],
            ObservableCapabilities: default,
            Warnings: []);
        var selection = registry.Select(supportedProbe);

        Assert.IsTrue(selection.IsSuccess);
        Assert.IsNotNull(selection.Value);
        Assert.AreEqual(decoders[0].Descriptor.Id, selection.Value.Descriptor.Id);
    }

    [TestMethod]
    public void FoundationUsesTheConfiguredApplicationDataRoot()
    {
        using TemporaryRoot root = new();
        using ServiceProvider provider = BuildProvider(root);

        LocalApplicationPaths paths = provider.GetRequiredService<LocalApplicationPaths>();

        Assert.AreEqual(Path.GetFullPath(root.Path), paths.Root);
        Assert.IsTrue(Directory.Exists(paths.ContentStore));
        Assert.IsTrue(Directory.Exists(paths.Rendezvous));
    }

    [TestMethod]
    public void FoundationPropagatesExplicitOfflineReplayEvidenceLifetime()
    {
        using TemporaryRoot root = new();
        ServiceCollection services = new();
        services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(
            ApplicationDataRoot: root.Path,
            OfflineReplayEvidenceLifetime: TimeSpan.FromMinutes(2)));
        using ServiceProvider provider = services.BuildServiceProvider();

        GameIntegrationOptions options =
            provider.GetRequiredService<GameIntegrationOptions>();

        Assert.AreEqual(TimeSpan.FromMinutes(2), options.OfflineReplayEvidenceLifetime);
    }

    [TestMethod]
    public void FoundationPropagatesExplicitLifecycleEvidenceTimeout()
    {
        using TemporaryRoot root = new();
        ServiceCollection services = new();
        services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(
            ApplicationDataRoot: root.Path,
            LifecycleEvidenceTimeout: TimeSpan.FromMinutes(2)));
        using ServiceProvider provider = services.BuildServiceProvider();

        GameIntegrationOptions options =
            provider.GetRequiredService<GameIntegrationOptions>();

        Assert.AreEqual(TimeSpan.FromMinutes(2), options.LifecycleEvidenceTimeout);
    }

    [TestMethod]
    public async Task HostStartupInitializesStorageSchema()
    {
        using TemporaryRoot root = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "WotBTreader.Bootstrap.Tests",
        });
        builder.Services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(root.Path));
        using IHost host = builder.Build();

        await host.StartAsync(TestContext.CancellationToken);
        try
        {
            LocalApplicationPaths paths = host.Services.GetRequiredService<LocalApplicationPaths>();
            Assert.IsTrue(
                File.Exists(paths.Database),
                "Host startup must apply storage migrations before serving.");
        }
        finally
        {
            await host.StopAsync(TestContext.CancellationToken);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static ServiceProvider BuildProvider(TemporaryRoot root)
    {
        ServiceCollection services = new();
        services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(root.Path));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() =>
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wotbtreader-composition-{Guid.CreateVersion7():N}");

        public string Path { get; }

        public void Dispose()
        {
            // Pooled SQLite connections keep the database file handle open after
            // the owning provider is disposed, so the pool must be drained first.
            SqliteConnection.ClearAllPools();
            for (int attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(20 * (attempt + 1)));
                    SqliteConnection.ClearAllPools();
                }
            }
        }
    }
}
