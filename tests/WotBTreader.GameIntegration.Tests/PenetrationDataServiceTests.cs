using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class PenetrationDataServiceTests
{
    private static readonly BattleSessionId SessionId = BattleSessionId.New();
    private static readonly DecodeRunId RunId = DecodeRunId.New();
    private static readonly ParticipantId ViewpointId = ParticipantId.New();
    private const long EnemyEntityId = 42;

    private static readonly EvidenceReference Evidence = new(
        SourceArtifactId.New(),
        "data.wotreplay",
        Offset: 0,
        Length: 1,
        new ContentHash(new string('a', ContentHash.Sha256HexLength)));

    [TestMethod]
    public async Task ResolveAsync_ReadsArmorAndStockShellFromInstall()
    {
        using Fixture fixture = new();
        fixture.WriteUkVehicle(
            "GB08_Churchill_I",
            armorXml: """
                <root>
                  <hull>
                    <armor>
                      <armor_1>93.4</armor_1>
                      <armor_2>186.7</armor_2>
                    </armor>
                    <primaryArmor>armor_2</primaryArmor>
                  </hull>
                  <turrets0>
                    <Turret_1>
                      <guns>
                        <_2pdr_Gun_Mk_XT>
                          <shots>
                            <_2pdr_AP_Mk.IXBT_2><shell>shared</shell></_2pdr_AP_Mk.IXBT_2>
                          </shots>
                        </_2pdr_Gun_Mk_XT>
                      </guns>
                    </Turret_1>
                  </turrets0>
                </root>
                """);
        fixture.WriteShells(
            """
            <root>
              <icons><ap>x.png 0 0</ap></icons>
              <_2pdr_AP_Mk.IXBT_2>
                <kind>ARMOR_PIERCING</kind>
                <caliber>40</caliber>
                <normalizationAngle>5</normalizationAngle>
                <ricochetAngle>70</ricochetAngle>
              </_2pdr_AP_Mk.IXBT_2>
            </root>
            """);
        fixture.WriteGuns(
            """
            <root>
              <ids><_2pdr_Gun_Mk_XT>1024</_2pdr_Gun_Mk_XT></ids>
              <shared>
                <_2pdr_Gun_Mk_XT>
                  <shots>
                    <_2pdr_AP_Mk.IXBT_2>
                      <speed>850</speed>
                      <maxDistance>720</maxDistance>
                      <piercingPower>92 72</piercingPower>
                    </_2pdr_AP_Mk.IXBT_2>
                  </shots>
                </_2pdr_Gun_Mk_XT>
              </shared>
            </root>
            """);

        PenetrationDataService service = fixture.CreateService();
        PenetrationContext? context = await service.ResolveAsync(
            Projection("GB08_Churchill_I"),
            CancellationToken.None);

        Assert.IsNotNull(context);
        Assert.IsTrue(context!.ArmorByEntity.TryGetValue(EnemyEntityId, out TankArmor enemyArmor));
        Assert.AreEqual(186.7, enemyArmor.FrontMm, 1e-9);
        Assert.AreEqual(0, enemyArmor.SideMm, 1e-9);

        ShellSpec shell = context.ViewerShell;
        Assert.AreEqual(92, shell.Penetration0Mm, 1e-9);
        Assert.AreEqual(40, shell.CaliberMm, 1e-9);
        Assert.AreEqual(70, shell.RicochetDegrees, 1e-9);
        Assert.AreEqual(5, shell.NormalizationDegrees, 1e-9);
        // Drop = (92 - 72) / 720.
        Assert.AreEqual((92.0 - 72.0) / 720.0, shell.DropPerMeterMm, 1e-9);
    }

    [TestMethod]
    public async Task ResolveAsync_NoInstall_ReturnsNull()
    {
        // The discovery fails (no game found), so the context is null — never
        // a fabricated badge.
        var projection = Projection("uk:GB08_Churchill_I");
        PenetrationDataService service = new(
            new FailingDiscovery(),
            new DvplReader(new GameIntegrationOptions { UseDefaultDiscoveryRoots = false }),
            NullLogger<PenetrationDataService>.Instance);

        PenetrationContext? context = await service.ResolveAsync(projection, CancellationToken.None);

        Assert.IsNull(context);
    }

    [TestMethod]
    public async Task ResolveAsync_MalformedVehicleXml_ReturnsNull()
    {
        // A corrupt vehicle XML must omit the badge (null context), never an
        // exception through the frame path — the documented fail-closed
        // contract. The mismatched tags throw XmlException during load.
        using Fixture fixture = new();
        fixture.WriteUkVehicle("GB08_Churchill_I", "<root><hull><armor></root>");

        PenetrationDataService service = fixture.CreateService();

        PenetrationContext? context = await service.ResolveAsync(
            Projection("GB08_Churchill_I"),
            CancellationToken.None);

        Assert.IsNull(context);
    }

    private static ReplayDecodeProjection Projection(string tankId)
    {
        DecodeRun decodeRun = new(
            RunId,
            Evidence.SourceArtifactId,
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
            SessionId,
            RunId,
            GameVersion: "11.19.0.10",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: null,
            ViewpointId,
            SchemaVersion: "1");
        return new ReplayDecodeProjection(
            decodeRun,
            session,
            Participants:
            [
                Participant(ViewpointId, entityId: 7, tankId),
                Participant(ParticipantId.New(), EnemyEntityId, tankId),
            ],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private static Participant Participant(ParticipantId id, long entityId, string tankId) =>
        new(
            id,
            SessionId,
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
            Evidence);

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly string _dataRoot;
        private readonly InstalledGameIdentity _identity;

        public Fixture()
        {
            _dataRoot = _temporary.CreateDirectory("game", "Data");
            string executable = _temporary.GetPath("game", "wotblitz.exe");
            File.WriteAllBytes(executable, "test-executable"u8.ToArray());
            _identity = new InstalledGameIdentity(
                executable,
                "11.19.0.10",
                DvplTestData.HashOf(0xaa),
                _dataRoot,
                DlcRoots: []);
        }

        public void WriteUkVehicle(string name, string armorXml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", "uk", $"{name}.xml.dvpl"),
                Encoding.UTF8.GetBytes(armorXml));

        public void WriteShells(string xml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", "uk", "components", "shells.xml.dvpl"),
                Encoding.UTF8.GetBytes(xml));

        public void WriteGuns(string xml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", "uk", "components", "guns.xml.dvpl"),
                Encoding.UTF8.GetBytes(xml));

        public PenetrationDataService CreateService() => new(
            new StubDiscovery(_identity),
            new DvplReader(new GameIntegrationOptions { UseDefaultDiscoveryRoots = false }),
            NullLogger<PenetrationDataService>.Instance);

        public void Dispose() => _temporary.Dispose();
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

    private sealed class FailingDiscovery : IGameInstallationDiscovery
    {
        public ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                OperationResult.Failure<InstalledGameIdentity>(
                    new ApplicationError("game.not_found", "no game")));
        }
    }
}
