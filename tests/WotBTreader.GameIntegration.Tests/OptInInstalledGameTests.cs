using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Core.Overlay;
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
    public async Task CollisionMeshParser_WhenExplicitlyOptedIn_ReadsRealCollisionMesh()
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
        string path = System.IO.Path.Combine(
            gameRoot, "Data", "3d", "Tanks", "CollisionMeshes", "uk-GB08_Churchill_I.scg.dvpl");
        if (!File.Exists(path))
        {
            Assert.Inconclusive("The Churchill I collision mesh is not installed.");
        }

        DvplReader reader = new(options);
        var result = await reader.ReadAsync(path, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);

        // The real Churchill I collision mesh is 476 vertices / 304 triangles
        // (probed 2026-08-13). The parser must reproduce that without crashing.
        CollisionMesh mesh = CollisionMeshParser.Parse(
            result.Value!.Data.Span,
            maxBytes: 16 * 1024 * 1024);

        Assert.HasCount(476, mesh.Vertices);
        Assert.AreEqual(304, mesh.TriangleCount);
        // A sampled normal must be a unit-ish vector (|n| ≈ 1).
        CollisionVertex first = mesh.Vertices[0];
        double length = Math.Sqrt(
            (first.NormalX * first.NormalX)
            + (first.NormalY * first.NormalY)
            + (first.NormalZ * first.NormalZ));
        Assert.AreEqual(1.0, length, 0.01);
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
