using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Game;
using WotBTreader.GameIntegration.DependencyInjection;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Logs;

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
    }

}
