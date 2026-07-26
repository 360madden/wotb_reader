using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
[TestCategory("LocalGame")]
public sealed class OptInInstalledGameTests
{
    [TestMethod]
    public async Task VersionResource_WhenExplicitlyOptedIn_IsValidDvpl()
    {
        string? gameRoot = Environment.GetEnvironmentVariable("WOTB_TREADER_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            Assert.Inconclusive(
                "Set WOTB_TREADER_GAME_ROOT to opt in to read-only installed-game validation.");
            return;
        }

        string versionResource = System.IO.Path.Combine(gameRoot, "Data", "version.txt.dvpl");
        DvplReader reader = new(
            new GameIntegrationOptions
            {
                GameInstallRoots = [gameRoot],
                UseDefaultDiscoveryRoots = false,
            });

        var result = await reader.ReadAsync(versionResource, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotEmpty(result.Value!.Data.ToArray());
    }

    [TestMethod]
    public async Task MetadataProvider_WhenExplicitlyOptedIn_ResolvesKnownVehicleAndMap()
    {
        string? gameRoot = Environment.GetEnvironmentVariable("WOTB_TREADER_GAME_ROOT");
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            Assert.Inconclusive(
                "Set WOTB_TREADER_GAME_ROOT to opt in to read-only installed-game validation.");
            return;
        }

        GameIntegrationOptions options = new()
        {
            GameInstallRoots = [gameRoot],
            UseDefaultDiscoveryRoots = false,
        };
        GameInstallationDiscovery discovery = new(
            options,
            NullLogger<GameInstallationDiscovery>.Instance);
        InstalledGameMetadataProvider provider = new(
            discovery,
            new DvplReader(options),
            options,
            NullLogger<InstalledGameMetadataProvider>.Instance);

        var context = await provider.ProbeAsync(CancellationToken.None);
        Assert.IsTrue(context.IsSuccess, context.Error?.Message);
        Assert.IsNotNull(context.Value);
        var vehicle = await provider.ResolveVehicleAsync(
            context.Value,
            compactDescriptor: (4 << 8) | 2,
            CancellationToken.None);
        var map = await provider.ResolveMapAsync(
            context.Value,
            "karelia",
            CancellationToken.None);

        Assert.IsTrue(vehicle.IsSuccess, vehicle.Error?.Message);
        Assert.AreEqual("usa:M4_Sherman", vehicle.Value!.VehicleId);
        Assert.AreEqual(WotBTreader.Core.TankClass.Medium, vehicle.Value.TankClass);
        Assert.IsTrue(map.IsSuccess, map.Error?.Message);
        Assert.AreEqual("karelia", map.Value!.MapId);
    }
}
