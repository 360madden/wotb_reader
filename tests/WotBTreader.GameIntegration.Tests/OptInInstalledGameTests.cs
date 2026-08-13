using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Core;
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
    public async Task PenetrationDataService_WhenExplicitlyOptedIn_ResolvesChurchillArmorShellAndMesh()
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
        PenetrationDataService service = new(
            new GameInstallationDiscovery(options, NullLogger<GameInstallationDiscovery>.Instance),
            new DvplReader(options),
            NullLogger<PenetrationDataService>.Instance);

        PenetrationContext? context = await service.ResolveAsync(
            ChurchillProjection(),
            CancellationToken.None);

        // The real install resolves the Churchill I end-to-end: nation scan →
        // armor XML → stock-gun shell + gun join → collision mesh.
        Assert.IsNotNull(context);
        Assert.IsTrue(context!.ArmorByEntity.TryGetValue(EnemyEntityId, out TankArmor armor));
        // Hull front = the thickest primaryArmor group (186.7 for the Churchill
        // I); side/rear stay 0 = unknown because the install declares no face
        // mapping for them (fail-closed, never guessed).
        Assert.AreEqual(186.7, armor.FrontMm, 1e-6);
        Assert.AreEqual(0, armor.SideMm, 1e-9);
        Assert.AreEqual(0, armor.RearMm, 1e-9);

        ShellSpec shell = context.ViewerShell;
        Assert.IsGreaterThan(0, shell.Penetration0Mm);
        Assert.IsGreaterThan(0, shell.CaliberMm);

        Assert.IsNotNull(context.MeshesByEntity);
        Assert.IsTrue(context.MeshesByEntity!.TryGetValue(EnemyEntityId, out CollisionMesh? mesh));
        Assert.IsNotNull(mesh);
        Assert.IsGreaterThan(0, mesh.TriangleCount);

        // Aim the camera head-on at the enemy: the cylinder pick + mesh
        // raycast + verdict must resolve without a crash and attribute the
        // badge to the aimed tank. The band is deliberately unasserted — the
        // real mesh geometry is not pinned here, so a head-on shot may resolve
        // to a determinate band or fail closed to Unknown.
        OverlayCamera camera = new(X: 0, Y: 0, Z: 100, YawRadians: Math.PI, PitchRadians: 0, RollRadians: null);
        OverlayTankState[] tanks = [new OverlayTankState(
            EnemyEntityId, X: 0, Y: 0, Z: 0, YawRadians: 0,
            HpFraction: 1.0, Alive: true, TeamNumber: 1,
            PlayerName: null, ClanTag: null, TankName: null, TankClass: null,
            DistanceMeters: 100)];

        PenetrationBadge? badge = PenetrationAim.ResolveBadge(
            camera,
            tanks,
            context.ArmorByEntity,
            context.ViewerShell,
            meshesByEntity: context.MeshesByEntity);

        Assert.IsNotNull(badge);
        Assert.AreEqual(EnemyEntityId, badge.Value.AimedEntityId);
        // The head-on aim must strike the FRONT plate of the real mesh. Before
        // the Z-up orientation fix (2026-08-13) this same ray was cast as "down"
        // in the Y-up local frame and misclassified the deck as the back face.
        Assert.AreEqual(StruckFace.Front, badge.Value.Face);
        // The real glacis is sloped, so effective armor thickens the nominal
        // 186.7 mm front (thickness / cos(incidence) ≥ thickness). The exact
        // slope is not pinned, only the monotonic bound.
        Assert.IsGreaterThanOrEqualTo(186.7, badge.Value.Verdict.EffectiveArmorMm!.Value);
        Assert.AreEqual(PenetrationBand.NoPen, badge.Value.Verdict.Band);
    }

    private const long EnemyEntityId = 42;

    private static ReplayDecodeProjection ChurchillProjection()
    {
        DecodeRunId runId = DecodeRunId.New();
        BattleSessionId sessionId = BattleSessionId.New();
        ParticipantId viewpointId = ParticipantId.New();
        EvidenceReference evidence = new(
            SourceArtifactId.New(),
            "data.wotreplay",
            Offset: 0,
            Length: 1,
            new ContentHash(new string('a', ContentHash.Sha256HexLength)));

        DecodeRun decodeRun = new(
            runId,
            evidence.SourceArtifactId,
            DecoderId: "test",
            DecoderVersion: "1",
            SchemaVersion: "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Participants,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            FailureCode: null,
            FailureSummary: null);
        BattleSession session = new(
            sessionId,
            runId,
            GameVersion: "11.19.0.10",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: null,
            viewpointId,
            SchemaVersion: "1");
        return new ReplayDecodeProjection(
            decodeRun,
            session,
            Participants:
            [
                Participant(viewpointId, 7, "GB08_Churchill_I", sessionId, evidence),
                Participant(ParticipantId.New(), EnemyEntityId, "GB08_Churchill_I", sessionId, evidence),
            ],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private static Participant Participant(
        ParticipantId id,
        long entityId,
        string tankId,
        BattleSessionId sessionId,
        EvidenceReference evidence) =>
        new(
            id,
            sessionId,
            AccountId: null,
            EntityId: entityId,
            TeamNumber: null,
            PlayerName: null,
            ClanTag: null,
            VehicleCompactDescriptor: null,
            tankId,
            TankName: null,
            TankClass.Unknown,
            BotStatus.Unknown,
            EvidenceConfidence.Unknown,
            BattleStats: null,
            evidence);

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
