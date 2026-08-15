using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class PenOfflineScorerTests
{
    private static readonly BattleSessionId SessionId = BattleSessionId.New();
    private static readonly DecodeRunId RunId = DecodeRunId.New();

    private static readonly EvidenceReference Evidence = new(
        SourceArtifactId.New(),
        "data.wotreplay",
        Offset: 0,
        Length: 1,
        new ContentHash(new string('a', ContentHash.Sha256HexLength)));

    [TestMethod]
    public async Task ScoresPenetratingCenterLineShotAgainstFrontArmor()
    {
        // Attacker 1m behind the victim's -Z (the victim's FRONT faces +Z at
        // yaw 0), so the center-line aim strikes the front plate head-on.
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank"),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank"),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ]);

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, aimOverrides: null, CancellationToken.None);

        Assert.AreEqual(0, report.SkippedShots);
        Assert.AreEqual(1, report.Validation.TotalShots);
        Assert.AreEqual(1, report.Validation.ClassifiedShots);
        Assert.AreEqual(1.0, report.Validation.BandAccuracy, 1e-9);
        Assert.AreEqual(0, report.Validation.PredictedRicochet);

        PenValidationShotRow row = report.Validation.Rows.Single();
        Assert.AreEqual(PenetrationBand.Pen, row.Band);
        Assert.IsTrue(row.Penetrated);
        Assert.IsFalse(row.PredictedRicochet);

        OfflinePenShot shot = report.Shots.Single();
        Assert.AreEqual("Attacker Tank", shot.AttackerTankName);
        Assert.AreEqual("Victim Tank", shot.VictimTankName);
        Assert.AreEqual("ap", shot.ShellName);
        Assert.IsNull(shot.Error);
        Assert.IsNotNull(shot.Row);
        Assert.IsFalse(shot.PrimaryEligible);
        Assert.AreEqual(0, report.PrimaryEligibleShots);
        Assert.AreEqual(1, report.ConfoundedShots);
        string[] confounds = shot.Confounds!.ToArray();
        CollectionAssert.Contains(confounds, "aim.exact_gun_ray_missing");
        CollectionAssert.Contains(confounds, "weapon.loaded_shell_unproven");
        CollectionAssert.Contains(confounds, "armor.ordered_layers_unproven");
    }

    [TestMethod]
    public async Task ExactProvenancePopulatesUnconfoundedPrimaryCohort()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", new TankArmor(20, 20, 20, 20), FrontQuad(), 200, exact: true);
        data.Add("uk:V", new TankArmor(50, 50, 50, 50), FrontQuad(), 0, exact: true);
        ParticipantId attackerParticipantId = ParticipantId.New();
        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(1, "uk:A", "Attacker Tank", attackerParticipantId),
                Participant(2, "uk:V", "Victim Tank"),
            ],
            positions:
            [
                Sample(1, 5.0, 0, -100),
                Sample(2, 5.0, 0, 0),
            ],
            events: [ShotEvent(1, 5.0, 1, 2, penetrated: true)],
            viewpointParticipantId: attackerParticipantId);
        AimSample[] exactAim =
        [
            new(
                TimeSpan.FromSeconds(5),
                new AimRay(0, 1.5, -100, 0, -1.5, 100),
                AimInputProvenance.ExactGunRay),
        ];

        OfflinePenScoreReport report = await new PenOfflineScorer(data)
            .ScoreAsync(projection, exactAim, CancellationToken.None);

        Assert.AreEqual(1, report.PrimaryEligibleShots);
        Assert.AreEqual(0, report.ConfoundedShots);
        Assert.AreEqual(1, report.PrimaryValidation!.TotalShots);
        Assert.IsTrue(report.Shots.Single().PrimaryEligible);
        Assert.IsEmpty(report.Shots.Single().Confounds!);
    }

    [TestMethod]
    public async Task ScoresBounceAgainstThickArmor()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(300, 300, 300, 300), mesh: FrontQuad(), penMm: 0);

        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank"),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank"),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: false),
            ]);

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, aimOverrides: null, CancellationToken.None);

        PenValidationShotRow row = report.Validation.Rows.Single();
        Assert.AreEqual(PenetrationBand.NoPen, row.Band);
        Assert.IsFalse(row.Penetrated);
        Assert.AreEqual(1.0, report.Validation.BandAccuracy, 1e-9);
    }

    [TestMethod]
    public async Task SkipsShotWhenAttackerTankDataUnavailable()
    {
        FakePenetrationData data = new();
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank"),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank"),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ]);

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, aimOverrides: null, CancellationToken.None);

        Assert.AreEqual(1, report.SkippedShots);
        Assert.AreEqual(0, report.Validation.TotalShots);
        OfflinePenShot shot = report.Shots.Single();
        Assert.AreEqual("attacker tank data unavailable", shot.Error);
        Assert.IsNull(shot.Row);
    }

    [TestMethod]
    public async Task SkipsShotWhenPositionSampleMissingAtShotTime()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        // The shot lands before any position sample exists for either tank.
        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank"),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank"),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 10.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 10.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ]);

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, aimOverrides: null, CancellationToken.None);

        Assert.AreEqual(1, report.SkippedShots);
        Assert.AreEqual("position sample missing at shot time", report.Shots.Single().Error);
        Assert.AreEqual(0, report.Validation.TotalShots);
    }

    [TestMethod]
    public async Task EmptyWhenNoShotImpactEvents()
    {
        FakePenetrationData data = new();
        ReplayDecodeProjection projection = Projection(participants: [], positions: [], events: []);

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, aimOverrides: null, CancellationToken.None);

        Assert.AreEqual(0, report.SkippedShots);
        Assert.AreEqual(0, report.Validation.TotalShots);
        Assert.IsEmpty(report.Shots);
    }

    [TestMethod]
    public async Task UsesAimOverrideForViewpointShot()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        ParticipantId attackerId = ParticipantId.New();
        ParticipantId victimId = ParticipantId.New();
        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank", attackerId),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank", victimId),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ],
            viewpointParticipantId: attackerId);

        // The override aims +X (sideways, away from the victim at z=0): it
        // misses the front quad, so the verdict is Unknown instead of the
        // center-line's head-on Pen.
        AimSample[] overrides =
        [
            new(TimeSpan.FromSeconds(5.0), new AimRay(0, 1.5, -100, 1, 0, 0)),
        ];

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, overrides, CancellationToken.None);

        Assert.AreEqual(0, report.SkippedShots);
        Assert.AreEqual(1, report.Validation.TotalShots);
        Assert.AreEqual(0, report.Validation.ClassifiedShots);
        Assert.AreEqual(PenetrationBand.Unknown, report.Validation.Rows.Single().Band);
    }

    [TestMethod]
    public void NormalizeAim_NormalizesNonUnitAndRejectsDegenerate()
    {
        AimRay? unit = PenOfflineScorer.NormalizeAim(new AimRay(0, 0, 0, 0, 0, 2));
        Assert.IsNotNull(unit);
        Assert.AreEqual(1.0, unit.Value.DirectionZ, 1e-9);
        Assert.AreEqual(0.0, unit.Value.DirectionY, 1e-9);
        Assert.AreEqual(0.0, unit.Value.DirectionX, 1e-9);

        Assert.IsNull(PenOfflineScorer.NormalizeAim(new AimRay(0, 0, 0, 0, 0, 0)));
        Assert.IsNull(PenOfflineScorer.NormalizeAim(new AimRay(0, 0, 0, double.NaN, 0, 0)));
    }

    [TestMethod]
    public async Task DegenerateAimOverrideFallsBackToCenterLine()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        ParticipantId attackerId = ParticipantId.New();
        ParticipantId victimId = ParticipantId.New();
        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank", attackerId),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank", victimId),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ],
            viewpointParticipantId: attackerId);

        // A zero-length direction cannot be normalized; the scorer must fall
        // through to the center-line (head-on front hit => Pen), never NaN.
        AimSample[] overrides =
        [
            new(TimeSpan.FromSeconds(5.0), new AimRay(0, 1.5, -100, 0, 0, 0)),
        ];

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, overrides, CancellationToken.None);

        Assert.AreEqual(0, report.SkippedShots);
        Assert.AreEqual(1, report.Validation.ClassifiedShots);
        Assert.AreEqual(PenetrationBand.Pen, report.Validation.Rows.Single().Band);
    }

    [TestMethod]
    public async Task IgnoresAimOverrideForNonViewpointShot()
    {
        FakePenetrationData data = new();
        data.Add("uk:A", armor: new TankArmor(20, 20, 20, 20), mesh: FrontQuad(), penMm: 200);
        data.Add("uk:V", armor: new TankArmor(50, 50, 50, 50), mesh: FrontQuad(), penMm: 0);

        ParticipantId attackerId = ParticipantId.New();
        ParticipantId victimId = ParticipantId.New();
        ReplayDecodeProjection projection = Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank", attackerId),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank", victimId),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events:
            [
                ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true),
            ],
            viewpointParticipantId: victimId);

        // The viewpoint is the VICTIM, so this attacker is not the viewer and
        // the override (which aims away) must be ignored: the center-line aim
        // still scores the head-on front hit as Pen.
        AimSample[] overrides =
        [
            new(TimeSpan.FromSeconds(5.0), new AimRay(0, 1.5, -100, 1, 0, 0)),
        ];

        PenOfflineScorer scorer = new(data);
        OfflinePenScoreReport report = await scorer.ScoreAsync(projection, overrides, CancellationToken.None);

        Assert.AreEqual(0, report.SkippedShots);
        Assert.AreEqual(1, report.Validation.ClassifiedShots);
        Assert.AreEqual(PenetrationBand.Pen, report.Validation.Rows.Single().Band);
    }

    private static ReplayDecodeProjection Projection(
        IReadOnlyList<Participant> participants,
        IReadOnlyList<PositionSample> positions,
        IReadOnlyList<CanonicalEvent> events,
        ParticipantId? viewpointParticipantId = null)
    {
        DecodeRun decodeRun = new(
            RunId,
            Evidence.SourceArtifactId,
            DecoderId: "test",
            DecoderVersion: "1",
            SchemaVersion: "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Positions,
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
            Duration: TimeSpan.FromMinutes(10),
            viewpointParticipantId,
            SchemaVersion: "1");
        return new ReplayDecodeProjection(
            decodeRun,
            session,
            participants,
            positions,
            events,
            RawRecords: [],
            Warnings: []);
    }

    private static Participant Participant(
        long entityId,
        string tankId,
        string tankName,
        ParticipantId? id = null) =>
        new(
            id ?? ParticipantId.New(),
            SessionId,
            AccountId: null,
            EntityId: entityId,
            TeamNumber: 1,
            PlayerName: null,
            ClanTag: null,
            VehicleCompactDescriptor: null,
            TankId: tankId,
            tankName,
            TankClass.Heavy,
            BotStatus.Human,
            EvidenceConfidence.Exact,
            BattleStats: null,
            Evidence);

    private static PositionSample Sample(long entityId, double seconds, double x, double z) =>
        new(
            PositionSampleId.New(),
            SessionId,
            ParticipantId: null,
            EntityId: entityId,
            Sequence: 1,
            TimeSpan.FromSeconds(seconds),
            RawX: x,
            RawY: 0,
            RawZ: z,
            NormalizedX: null,
            NormalizedY: null,
            RawCoordinateSpace: CoordinateSpace.ReplayRaw,
            NormalizedCoordinateSpace: null,
            Evidence,
            Yaw: 0,
            Pitch: 0,
            Roll: 0);

    private static CanonicalEvent ShotEvent(
        long sequence,
        double seconds,
        long attacker,
        long victim,
        bool penetrated) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            sequence,
            CanonicalEventKind.ShotImpact,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: victim,
            $"{{\"victimEntityId\":{victim},\"hitResult\":{(penetrated ? 3 : 0)},\"penetrated\":{(penetrated ? "true" : "false")},\"attackerEntityId\":{attacker}}}",
            EvidenceConfidence.Exact,
            Evidence);

    /// <summary>
    /// A single front-facing quad (mesh-local plane Y=1, normal +Y, spanning
    /// X ∈ [-1, 1], Z ∈ [-1, 1]) in the collision mesh's Z-up space. A ray
    /// approaching from the tank's -Z (its front at yaw 0) strikes it
    /// head-on and classifies as <see cref="StruckFace.Front"/>.
    /// </summary>
    private static IReadOnlyList<CollisionMeshPart> FrontQuad()
    {
        CollisionVertex[] vertices =
        [
            new(-1, 1, -1, NormalX: 0, NormalY: 1, NormalZ: 0),
            new(1, 1, -1, NormalX: 0, NormalY: 1, NormalZ: 0),
            new(1, 1, 1, NormalX: 0, NormalY: 1, NormalZ: 0),
            new(-1, 1, 1, NormalX: 0, NormalY: 1, NormalZ: 0),
        ];
        return
        [
            new CollisionMeshPart(
                PartId: 1,
                new CollisionMesh(
                    vertices,
                    TriangleIndices: [0, 2, 1, 0, 3, 2])),
        ];
    }

    private sealed class FakePenetrationData : IOverlayPenetrationData
    {
        private readonly Dictionary<string, PenetrationTankData> _byTankId = new(StringComparer.Ordinal);

        public void Add(
            string tankId,
            TankArmor armor,
            IReadOnlyList<CollisionMeshPart> mesh,
            double penMm,
            bool exact = false)
        {
            ShellSpec spec = new(penMm, CaliberMm: 75, RicochetDegrees: 70, NormalizationDegrees: 5);
            _byTankId[tankId] = new PenetrationTankData(
                armor,
                mesh,
                [new ShellOption("ap", ShellKind.ArmorPiercing, spec)],
                exact
                    ? new PenetrationInputProvenance(
                        ArmorInputProvenance.ExactOrderedLayers,
                        WeaponInputProvenance.ExactLoadedShell,
                        AimInputProvenance.Unknown)
                    : null);
        }

        public ValueTask<PenetrationContext?> ResolveAsync(
            ReplayDecodeProjection projection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PenetrationContext?>(null);

        public ValueTask<PenetrationTankData?> ResolveTankAsync(
            string tankId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _byTankId.TryGetValue(tankId, out PenetrationTankData? data) ? data : null);
    }
}
