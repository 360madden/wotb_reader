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
        Assert.AreEqual(
            PenetrationCompatibilityStatus.Exact,
            context.CompatibilityStatus);
        Assert.IsFalse(string.IsNullOrWhiteSpace(context.CompatibilityManifestId));
        Assert.AreEqual(
            ArmorInputProvenance.NominalSummary,
            context.InputProvenance.Armor);
        Assert.AreEqual(
            WeaponInputProvenance.ManualSelection,
            context.InputProvenance.Weapon);
        Assert.AreEqual(AimInputProvenance.Unknown, context.InputProvenance.Aim);

        PenetrationDiagnosticsSnapshot diagnostics = await service.GetSnapshotAsync(
            CancellationToken.None);
        Assert.IsNotNull(diagnostics.Manifest);
        Assert.AreEqual(context.CompatibilityManifestId, diagnostics.Manifest!.ManifestId);
        Assert.AreEqual("11.19.0.10", diagnostics.Manifest.ReplayGameVersion);
        Assert.AreEqual(PenetrationCompatibilityStatus.Exact,
            diagnostics.Manifest.CompatibilityStatus);
        Assert.IsGreaterThanOrEqualTo(3, diagnostics.Manifest.Sources.Count);
        Assert.IsTrue(diagnostics.Manifest.Sources.All(source =>
            !Path.IsPathRooted(source.RelativePath)
            && !source.RelativePath.Contains("..", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Manifest.Sources.Any(source =>
            source.SourceKind == "vehicle"));
        Assert.IsTrue(diagnostics.Manifest.Sources.Any(source =>
            source.SourceKind == "shell"));
        Assert.IsTrue(diagnostics.Manifest.Sources.Any(source =>
            source.SourceKind == "gun"));
        Assert.IsNotNull(diagnostics.ResolutionReport);
        Assert.AreEqual(2, diagnostics.ResolutionReport!.RosterEntities);
        Assert.AreEqual(2, diagnostics.ResolutionReport.VehicleIdsPresent);
        Assert.AreEqual(2, diagnostics.ResolutionReport.VehiclesResolved);
        Assert.AreEqual(2, diagnostics.ResolutionReport.ArmorModelsResolved);
        Assert.AreEqual(1, diagnostics.ResolutionReport.WeaponStatesResolved);

        ReplayDecodeProjection incompleteProjection = Projection("GB08_Churchill_I");
        incompleteProjection = incompleteProjection with
        {
            Session = incompleteProjection.Session! with { GameVersion = "11.19.0" },
        };
        PenetrationContext? incomplete = await service.ResolveAsync(
            incompleteProjection,
            CancellationToken.None);
        Assert.IsNotNull(incomplete);
        Assert.AreEqual(
            PenetrationCompatibilityStatus.ReplayBuildIncomplete,
            incomplete.CompatibilityStatus);
        Assert.IsNull(incomplete.CompatibilityManifestId);
    }

    [TestMethod]
    public async Task ResolveAsync_NationPrefixedTankId_ResolvesArmorAndShell()
    {
        // The decoder emits the enrichment's `nation:tank` VehicleId form
        // (e.g. `uk:GB08_Churchill_I`); the service must split the prefix and
        // resolve the BARE tank file name instead of treating the whole string
        // as the file name (which would never match an install path).
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
            Projection("uk:GB08_Churchill_I"),
            CancellationToken.None);

        Assert.IsNotNull(context);
        Assert.IsTrue(context!.ArmorByEntity.TryGetValue(EnemyEntityId, out TankArmor armor));
        Assert.AreEqual(186.7, armor.FrontMm, 1e-9);
        Assert.AreEqual(92, context.ViewerShell.Penetration0Mm, 1e-9);
    }

    [TestMethod]
    public async Task ResolveTankAsync_NationPrefixedTankId_ResolvesArmorAndShells()
    {
        // The offline PN-4 scorer lane: ONE tank (any roster tank, not the
        // viewer) resolved by its decoded `nation:tank` VehicleId must yield
        // armor + the stock gun's shells.
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
        PenetrationTankData? data = await service.ResolveTankAsync(
            "uk:GB08_Churchill_I",
            CancellationToken.None);

        Assert.IsNotNull(data);
        Assert.AreEqual(186.7, data!.Armor.FrontMm, 1e-9);
        Assert.HasCount(1, data.Shells);
        Assert.AreEqual("_2pdr_AP_Mk.IXBT_2", data.Shells[0].Name);
        Assert.AreEqual(92, data.Shells[0].Spec.Penetration0Mm, 1e-9);
    }

    [TestMethod]
    public async Task ResolveTankAsync_MatchesGunProfileByStockGunIdentity()
    {
        // The same shell resource can be referenced by multiple guns. The
        // stock gun's piercingPower must win; joining guns.xml by shell name
        // alone would incorrectly select the first unrelated gun.
        using Fixture fixture = new();
        fixture.WriteUkVehicle(
            "SharedProfileTank",
            """
            <root>
              <hull><armor><armor_1>100</armor_1></armor><primaryArmor>armor_1</primaryArmor></hull>
              <turrets0><Turret_1><guns><_stock_gun><shots><_shared_shell><shell>shared</shell></_shared_shell></shots></_stock_gun></guns></Turret_1></turrets0>
            </root>
            """);
        fixture.WriteShells(
            """
            <root><_shared_shell><kind>ARMOR_PIERCING</kind><caliber>75</caliber><ricochetAngle>70</ricochetAngle></_shared_shell></root>
            """);
        fixture.WriteGuns(
            """
            <root><shared>
              <_other_gun><shots><_shared_shell><maxDistance>700</maxDistance><piercingPower>10 5</piercingPower></_shared_shell></shots></_other_gun>
              <_stock_gun><shots><_shared_shell><maxDistance>700</maxDistance><piercingPower>100 80</piercingPower></_shared_shell></shots></_stock_gun>
            </shared></root>
            """);

        PenetrationDataService service = fixture.CreateService();
        PenetrationTankData? data = await service.ResolveTankAsync(
            "uk:SharedProfileTank",
            CancellationToken.None);

        Assert.IsNotNull(data);
        Assert.HasCount(1, data!.Shells);
        Assert.AreEqual(100, data.Shells[0].Spec.Penetration0Mm, 1e-9);
        Assert.AreEqual((100.0 - 80.0) / 700.0, data.Shells[0].Spec.DropPerMeterMm, 1e-9);
    }

    [TestMethod]
    public async Task ResolveTankAsync_RawCompactDescriptor_ResolvesThroughMetadataIndex()
    {
        // The store can carry a RAW compact descriptor (the decode-time
        // enrichment missed): `2897` must resolve through the installed-game
        // metadata index to `uk:GB08_Churchill_I` before the armor/shell
        // lanes run — the offline scorer's 69-shot session depends on it.
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

        PenetrationDataService service = new(
            new StubDiscovery(fixture.Identity),
            new DvplReader(new GameIntegrationOptions { UseDefaultDiscoveryRoots = false }),
            new DescriptorStubMetadataProvider(2897, "uk:GB08_Churchill_I"),
            NullLogger<PenetrationDataService>.Instance);

        PenetrationTankData? data = await service.ResolveTankAsync(
            "2897",
            CancellationToken.None);

        Assert.IsNotNull(data);
        Assert.AreEqual(186.7, data!.Armor.FrontMm, 1e-9);
        Assert.AreEqual("_2pdr_AP_Mk.IXBT_2", data.Shells[0].Name);
        Assert.AreEqual(92, data.Shells[0].Spec.Penetration0Mm, 1e-9);
    }

    [TestMethod]
    public async Task ResolveTankAsync_SameBareNameAcrossNations_UsesNationScopedCaches()
    {
        // The same bare tank filename can exist in multiple nation folders.
        // Armor, mesh, and stock-gun shot caches must include the nation or a
        // prior lookup can silently make the second vehicle use the first
        // vehicle's profile.
        using Fixture fixture = new();
        fixture.WriteVehicle(
            "uk",
            "SharedTank",
            """
            <root>
              <hull><armor><armor_1>100</armor_1></armor><primaryArmor>armor_1</primaryArmor></hull>
              <turrets0><Turret_1><guns><Gun><shots><uk_shell><shell>shared</shell></uk_shell></shots></Gun></guns></Turret_1></turrets0>
            </root>
            """);
        fixture.WriteShells(
            "uk",
            """
            <root><uk_shell><kind>ARMOR_PIERCING</kind><caliber>75</caliber><ricochetAngle>70</ricochetAngle></uk_shell></root>
            """);
        fixture.WriteGuns(
            "uk",
            """
            <root><shared><Gun><shots><uk_shell><maxDistance>700</maxDistance><piercingPower>100 80</piercingPower></uk_shell></shots></Gun></shared></root>
            """);
        fixture.WriteVehicle(
            "usa",
            "SharedTank",
            """
            <root>
              <hull><armor><armor_1>200</armor_1></armor><primaryArmor>armor_1</primaryArmor></hull>
              <turrets0><Turret_1><guns><Gun><shots><usa_shell><shell>shared</shell></usa_shell></shots></Gun></guns></Turret_1></turrets0>
            </root>
            """);
        fixture.WriteShells(
            "usa",
            """
            <root><usa_shell><kind>ARMOR_PIERCING_CR</kind><caliber>90</caliber><ricochetAngle>70</ricochetAngle></usa_shell></root>
            """);
        fixture.WriteGuns(
            "usa",
            """
            <root><shared><Gun><shots><usa_shell><maxDistance>700</maxDistance><piercingPower>180 160</piercingPower></usa_shell></shots></Gun></shared></root>
            """);

        PenetrationDataService service = fixture.CreateService();
        PenetrationTankData? uk = await service.ResolveTankAsync("uk:SharedTank", CancellationToken.None);
        PenetrationTankData? usa = await service.ResolveTankAsync("usa:SharedTank", CancellationToken.None);

        Assert.IsNotNull(uk);
        Assert.IsNotNull(usa);
        Assert.AreEqual(100, uk!.Armor.FrontMm, 1e-9);
        Assert.AreEqual(200, usa!.Armor.FrontMm, 1e-9);
        Assert.AreEqual("uk_shell", uk.Shells.Single().Name);
        Assert.AreEqual("usa_shell", usa.Shells.Single().Name);
        Assert.AreEqual(ShellKind.ArmorPiercing, uk.Shells[0].Kind);
        Assert.AreEqual(ShellKind.ArmorPiercingCr, usa.Shells[0].Kind);
    }

    [TestMethod]
    public async Task ResolveTankAsync_PathLikeTankId_FailsClosed()
    {
        // Tank ids are replay-derived input. Path separators and traversal
        // components must never escape the installed resource root during a
        // best-effort metadata lookup.
        using Fixture fixture = new();
        PenetrationDataService service = fixture.CreateService();

        Assert.IsNull(await service.ResolveTankAsync(
            "uk:../outside",
            CancellationToken.None));
        Assert.IsNull(await service.ResolveTankAsync(
            "../outside",
            CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveTankAsync_UnknownTank_ReturnsNull()
    {
        using Fixture fixture = new();

        PenetrationDataService service = fixture.CreateService();
        PenetrationTankData? data = await service.ResolveTankAsync(
            "uk:Not_A_Real_Tank",
            CancellationToken.None);

        Assert.IsNull(data);
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
            new StubMetadataProvider(),
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
            WriteVehicle("uk", name, armorXml);

        public void WriteVehicle(string nation, string name, string armorXml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", nation, $"{name}.xml.dvpl"),
                Encoding.UTF8.GetBytes(armorXml));

        public void WriteShells(string xml) => WriteShells("uk", xml);

        public void WriteShells(string nation, string xml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", nation, "components", "shells.xml.dvpl"),
                Encoding.UTF8.GetBytes(xml));

        public void WriteGuns(string xml) => WriteGuns("uk", xml);

        public void WriteGuns(string nation, string xml) =>
            DvplTestData.Write(
                System.IO.Path.Combine(
                    _dataRoot, "XML", "item_defs", "vehicles", nation, "components", "guns.xml.dvpl"),
                Encoding.UTF8.GetBytes(xml));

        public InstalledGameIdentity Identity => _identity;

        public PenetrationDataService CreateService() => new(
            new StubDiscovery(_identity),
            new DvplReader(new GameIntegrationOptions { UseDefaultDiscoveryRoots = false }),
            new StubMetadataProvider(),
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

    /// <summary>
    /// A metadata index with ONE descriptor→VehicleId mapping (the raw
    /// compact-descriptor lane). Everything else fails closed.
    /// </summary>
    private sealed class DescriptorStubMetadataProvider(int descriptor, string vehicleId)
        : IInstalledGameMetadataProvider
    {
        public ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(new GameMetadataContext(
                new InstalledGameIdentity(
                    "C:\\game\\wotblitz.exe",
                    "11.19.0.10",
                    DvplTestData.HashOf(0xaa),
                    "C:\\game\\Data",
                    DlcRoots: []),
                ProviderVersion: "test",
                SourceSetHash: DvplTestData.HashOf(0xbb),
                LoadedAtUtc: DateTimeOffset.UnixEpoch)));

        public ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
            GameMetadataContext context,
            int compactDescriptor,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(compactDescriptor == descriptor
                ? OperationResult.Success(new VehicleMetadata(
                    descriptor,
                    vehicleId,
                    DisplayName: "test",
                    TankClass: TankClass.Heavy,
                    Nation: "uk",
                    GameVersion: "11.19.0.10",
                    SourceHash: DvplTestData.HashOf(0xcc)))
                : OperationResult.Failure<VehicleMetadata>(
                    new ApplicationError("metadata.vehicle_not_found", "no vehicle")));

        public ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
            GameMetadataContext context,
            string mapId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<MapMetadata>(
                new ApplicationError("metadata.map_not_found", "no map")));
    }

    /// <summary>
    /// A metadata index with no vehicle table: every descriptor resolution
    /// fails closed. The fixture installs are resolved by the nation scan /
    /// explicit-prefix lanes, so no index is needed for those tests.
    /// </summary>
    private sealed class StubMetadataProvider : IInstalledGameMetadataProvider
    {
        public ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<GameMetadataContext>(
                new ApplicationError("metadata.unavailable", "no index")));

        public ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
            GameMetadataContext context,
            int compactDescriptor,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<VehicleMetadata>(
                new ApplicationError("metadata.unavailable", "no index")));

        public ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
            GameMetadataContext context,
            string mapId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<MapMetadata>(
                new ApplicationError("metadata.unavailable", "no index")));
    }
}
