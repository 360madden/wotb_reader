using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Game;
using WotBTreader.GameIntegration.DependencyInjection;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameIntegrationRegistrationTests
{
    [TestMethod]
    public void AddGameIntegration_RegistersOwnedPortsWithoutFilesystemAccess()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddGameIntegration(
            new GameIntegrationOptions { UseDefaultDiscoveryRoots = false });

        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IDvplReader)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameInstallationDiscovery)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IInstalledGameMetadataProvider)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IBlitzReplayLifecycleParser)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IBlitzReplayLogMonitor)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IBlitzReplayLifecycleFeed)));
    }

    [TestMethod]
    public void AddGameIntegration_RegistersSessionInterfaces()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddGameIntegration(
            new GameIntegrationOptions { UseDefaultDiscoveryRoots = false });

        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameSessionState)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameReplayLauncher)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameMemoryObserver)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameMemoryScanner)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameProcessIdentityObserver)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGameProcessQueryPlatform)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IWindowsExecutableFingerprintReader)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ITrustedGameIdentityProvider)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ILaunchCorrelationGenerator)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IManagedLaunchPreparer)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IReplayLaunchStageNameGenerator)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IReplayLaunchStagingPlatform)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IManagedReplayArtifactStager)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(ISuspendedProcessPlatform)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IManagedLaunchCorrelationRegistrar)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IThreadResumePlatform)));
        Assert.IsTrue(services.Any(item => item.ServiceType == typeof(IGuardedMemoryReaderFactory)));
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IWindowsExecutableFingerprintReader)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(ITrustedGameIdentityProvider)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(ILaunchCorrelationGenerator)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IManagedLaunchPreparer)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IReplayLaunchStageNameGenerator)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IReplayLaunchStagingPlatform)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IManagedReplayArtifactStager)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(ISuspendedProcessPlatform)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IManagedLaunchCorrelationRegistrar)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IThreadResumePlatform)).Lifetime);
        Assert.AreEqual(
            ServiceLifetime.Singleton,
            services.Single(item =>
                item.ServiceType == typeof(IGuardedMemoryReaderFactory)).Lifetime);
    }
}
