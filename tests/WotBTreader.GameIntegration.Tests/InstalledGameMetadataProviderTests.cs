using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class InstalledGameMetadataProviderTests
{
    // The usa country compact id (33 = (2 << 4) | 1) — pinned 2026-08-14
    // against the ground-truth replay descriptors (usa M4_Sherman index 4 →
    // descriptor 1057). The old 0–8 enumeration matched only germany.
    private const int UsaNationId = 33;
    private const int VehicleTypeId = 4;
    private const int CompactDescriptor = (VehicleTypeId << 8) | UsaNationId;

    [TestMethod]
    public async Task ResolveVehicleAsync_DlcDefinitionOverridesBaseAndResolvesName()
    {
        using MetadataFixture fixture = new();
        fixture.WriteBaseVehicle("BaseTank", "mediumTank", "#usa_vehicles:BaseTank");
        fixture.WriteDlcVehicle("DlcTank", "heavyTank", "#usa_vehicles:DlcTank");
        fixture.WriteLocalization(
            """
            "#usa_vehicles:BaseTank": "Base Display"
            "#usa_vehicles:DlcTank": "DLC Display"
            """);

        InstalledGameMetadataProvider provider = fixture.CreateProvider();
        OperationResult<GameMetadataContext> context =
            await provider.ProbeAsync(CancellationToken.None);
        OperationResult<VehicleMetadata> result =
            await provider.ResolveVehicleAsync(
                context.Value!,
                CompactDescriptor,
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual("usa:DlcTank", result.Value!.VehicleId);
        Assert.AreEqual("DLC Display", result.Value.DisplayName);
        Assert.AreEqual(TankClass.Heavy, result.Value.TankClass);
        Assert.AreEqual("11.18.0.7", result.Value.GameVersion);
    }

    [TestMethod]
    public async Task ProbeAsync_SourceChanges_InvalidatesOldContextAndBuildsNewProjection()
    {
        using MetadataFixture fixture = new();
        fixture.WriteDlcVehicle("FirstTank", "lightTank", "#usa_vehicles:FirstTank");
        fixture.WriteLocalization(
            """
            "#usa_vehicles:FirstTank": "First"
            "#usa_vehicles:SecondTank": "Second"
            """);
        InstalledGameMetadataProvider provider = fixture.CreateProvider();

        OperationResult<GameMetadataContext> firstContext =
            await provider.ProbeAsync(CancellationToken.None);
        OperationResult<VehicleMetadata> first =
            await provider.ResolveVehicleAsync(
                firstContext.Value!,
                CompactDescriptor,
                CancellationToken.None);
        fixture.WriteDlcVehicle("SecondTank", "AT-SPG", "#usa_vehicles:SecondTank");

        OperationResult<VehicleMetadata> stale =
            await provider.ResolveVehicleAsync(
                firstContext.Value!,
                CompactDescriptor,
                CancellationToken.None);
        OperationResult<GameMetadataContext> secondContext =
            await provider.ProbeAsync(CancellationToken.None);
        OperationResult<VehicleMetadata> second =
            await provider.ResolveVehicleAsync(
                secondContext.Value!,
                CompactDescriptor,
                CancellationToken.None);

        Assert.IsTrue(first.IsSuccess);
        Assert.AreEqual("First", first.Value!.DisplayName);
        Assert.IsFalse(stale.IsSuccess);
        Assert.AreEqual("game.metadata.context_stale", stale.Error!.Code);
        Assert.IsTrue(secondContext.IsSuccess);
        Assert.AreNotEqual(firstContext.Value!.SourceSetHash, secondContext.Value!.SourceSetHash);
        Assert.IsTrue(second.IsSuccess);
        Assert.AreEqual("Second", second.Value!.DisplayName);
        Assert.AreEqual(TankClass.TankDestroyer, second.Value.TankClass);
    }

    [TestMethod]
    public async Task ResolveMapAsync_NameAndNumericIdResolveWithoutInventingBounds()
    {
        using MetadataFixture fixture = new();
        fixture.WriteBaseVehicle("BaseTank", "mediumTank", "#usa_vehicles:BaseTank");
        fixture.WriteMaps(
            """
            maps:
                karelia:
                    id: 1
                    localName: "17_karelia_ka/17_karelia_ka.sc2"
            """);
        fixture.WriteLocalization(
            """
            "#maps:karelia:17_karelia_ka/17_karelia_ka.sc2": "Rockfield"
            """);

        InstalledGameMetadataProvider provider = fixture.CreateProvider();
        OperationResult<GameMetadataContext> context =
            await provider.ProbeAsync(CancellationToken.None);
        OperationResult<MapMetadata> byName =
            await provider.ResolveMapAsync(context.Value!, "karelia", CancellationToken.None);
        OperationResult<MapMetadata> byNumber =
            await provider.ResolveMapAsync(context.Value!, "1", CancellationToken.None);

        Assert.IsTrue(byName.IsSuccess);
        Assert.IsTrue(byNumber.IsSuccess);
        Assert.AreEqual("Rockfield", byName.Value!.DisplayName);
        Assert.AreEqual(byName.Value, byNumber.Value);
        Assert.IsNull(byName.Value.WorldMinX);
        Assert.IsNull(byName.Value.WorldMaxX);
        Assert.IsNull(byName.Value.WorldMinZ);
        Assert.IsNull(byName.Value.WorldMaxZ);
    }

    [TestMethod]
    public async Task ProbeAsync_UnsupportedExactVersion_FailsClosed()
    {
        using MetadataFixture fixture = new(productVersion: "11.18.0.8");
        InstalledGameMetadataProvider provider = fixture.CreateProvider();

        OperationResult<GameMetadataContext> result =
            await provider.ProbeAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.metadata.unsupported_version", result.Error!.Code);
    }

    [TestMethod]
    public async Task ResolveVehicleAsync_ExecutableHashChanged_InvalidatesContext()
    {
        using MetadataFixture fixture = new();
        fixture.WriteBaseVehicle("BaseTank", "mediumTank", "#usa_vehicles:BaseTank");
        MutableDiscovery discovery = new(fixture.Identity);
        InstalledGameMetadataProvider provider = fixture.CreateProvider(discovery);
        OperationResult<GameMetadataContext> context =
            await provider.ProbeAsync(CancellationToken.None);
        discovery.Identity = discovery.Identity with
        {
            ExecutableSha256 = DvplTestData.HashOf(0xbb),
        };

        OperationResult<VehicleMetadata> result =
            await provider.ResolveVehicleAsync(
                context.Value!,
                CompactDescriptor,
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.metadata.context_stale", result.Error!.Code);
    }

    private sealed class MetadataFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly string _baseRoot;
        private readonly string _dlcRoot;
        private readonly InstalledGameIdentity _identity;

        public MetadataFixture(string productVersion = "11.18.0.7")
        {
            _baseRoot = _temporary.CreateDirectory("game", "Data");
            _dlcRoot = _temporary.CreateDirectory("user", "packs");
            string executable = _temporary.GetPath("game", "wotblitz.exe");
            File.WriteAllBytes(executable, "test-executable"u8.ToArray());
            _identity = new InstalledGameIdentity(
                executable,
                productVersion,
                DvplTestData.HashOf(0xaa),
                _baseRoot,
                [_dlcRoot]);
        }

        public InstalledGameIdentity Identity => _identity;

        public void WriteBaseVehicle(string name, string tags, string localizationKey) =>
            WriteVehicle(_baseRoot, name, tags, localizationKey);

        public void WriteDlcVehicle(string name, string tags, string localizationKey) =>
            WriteVehicle(_dlcRoot, name, tags, localizationKey);

        public void WriteLocalization(string yaml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(_baseRoot, "Strings", "en.yaml.dvpl"),
                Encoding.UTF8.GetBytes(yaml));

        public void WriteMaps(string yaml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(_baseRoot, "maps.yaml.dvpl"),
                Encoding.UTF8.GetBytes(yaml));

        public InstalledGameMetadataProvider CreateProvider(
            IGameInstallationDiscovery? discovery = null)
        {
            GameIntegrationOptions options = new()
            {
                UseDefaultDiscoveryRoots = false,
                SupportedProductVersions =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "11.18.0.7" },
            };
            return new InstalledGameMetadataProvider(
                discovery ?? new StubDiscovery(_identity),
                new DvplReader(options),
                options,
                NullLogger<InstalledGameMetadataProvider>.Instance);
        }

        public void Dispose() => _temporary.Dispose();

        private static void WriteVehicle(
            string root,
            string name,
            string tags,
            string localizationKey)
        {
            string xml =
                $"""
                 <root>
                   <{name}>
                     <id>{VehicleTypeId}</id>
                     <userString>{localizationKey}</userString>
                     <tags>{tags}</tags>
                   </{name}>
                 </root>
                 """;
            DvplTestData.Write(
                System.IO.Path.Combine(
                    root,
                    "XML",
                    "item_defs",
                    "vehicles",
                    "usa",
                    "list.xml.dvpl"),
                Encoding.UTF8.GetBytes(xml));
        }
    }

    private sealed class StubDiscovery(InstalledGameIdentity identity)
        : IGameInstallationDiscovery
    {
        public ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(OperationResult.Success(identity));
        }
    }

    private sealed class MutableDiscovery(InstalledGameIdentity identity)
        : IGameInstallationDiscovery
    {
        public InstalledGameIdentity Identity { get; set; } = identity;

        public ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(OperationResult.Success(Identity));
        }
    }
}
