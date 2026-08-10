using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class ReplayFrameSourceTests
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
    public async Task Frame_UsesViewpointAsCamera_AndRendersTanksWithNearestSamples()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "EnemyTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 1, seconds: 10, x: 5, y: 0, z: 0, yaw: 0.5),
                Sample(entityId: 2, seconds: 0, x: 100, y: 0, z: 0, yaw: null),
                Sample(entityId: 2, seconds: 10, x: 11, y: 8, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        // Camera = the viewpoint entity's sample at 10s.
        Assert.AreEqual(5, frame.Camera.X);
        Assert.AreEqual(0.5, frame.Camera.YawRadians!.Value, 1e-9);
        // Two tanks, nearest sample at/before 10s (the 10s sample).
        Assert.HasCount(2, frame.Tanks);
        OverlayTankState self = frame.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(5, self.X);
        Assert.AreEqual("ViewpointTank", self.TankName);
        Assert.AreEqual(1, self.TeamNumber);
        // Sorted by distance: self (0m) before enemy (~8m).
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
        OverlayTankState enemy = frame.Tanks[1];
        Assert.AreEqual(2, enemy.EntityId);
        Assert.AreEqual(2, enemy.TeamNumber);
        Assert.AreEqual(10, enemy.DistanceMeters, 1e-6);
    }

    [TestMethod]
    public void Frame_OmittedWhenNoSampleAtOrBeforeFrameTime()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "LateTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                // Tank 2's first sample is AFTER the frame time.
                Sample(entityId: 2, seconds: 20, x: 1, y: 0, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        // Fail-closed: tank 2 has no evidence at/before 10s and is omitted.
        Assert.HasCount(1, frame.Tanks);
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
    }

    [TestMethod]
    public void Frame_HpFractionReflectsDamageReceived_AndDestroyedClearsAlive()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "VictimTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 2, seconds: 0, x: 10, y: 0, z: 0, yaw: null),
            },
            events: new[]
            {
                DamageEvent(entityId: 2, seconds: 2, damage: 60),
                DamageEvent(entityId: 2, seconds: 4, damage: 40),
            });

        // At 3s: 60 of the victim's 100 observed damage has landed -> 0.4 hp.
        OverlayFrame mid = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(3));
        Assert.AreEqual(0.4, mid.Tanks.Single(tank => tank.EntityId == 2).HpFraction, 1e-9);
        Assert.IsTrue(mid.Tanks.Single(tank => tank.EntityId == 2).Alive);

        // At 5s: all 100 landed, then destroyed -> alive false, hp 0.
        OverlayFrame after = ReplayFrameSource.BuildFrame(
            projection with
            {
                Events = [.. projection.Events,
                    DestroyedEvent(entityId: 2, seconds: 4.5)],
            },
            TimeSpan.FromSeconds(5));
        OverlayTankState victim = after.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(0.0, victim.HpFraction, 1e-9);
        Assert.IsFalse(victim.Alive);
    }

    [TestMethod]
    public void Frame_OmitsNonParticipantEntities()
    {
        // The position stream carries non-tank entities (a duplicate "self"
        // stream, projectiles) that must never render as nameplates.
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                // Entity 9999 has full position evidence but no roster entry.
                Sample(entityId: 9999, seconds: 0, x: 50, y: 0, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.Zero);

        Assert.HasCount(1, frame.Tanks);
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
    }

    [TestMethod]
    public void Frame_NoViewpointParticipant_ReturnsOriginCamera()
    {
        var projection = Projection(
            viewpointId: null,
            new[]
            {
                Participant(ParticipantId.New(), entityId: 1, "SomeTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 10, y: 0, z: 0, yaw: 0.1),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.Zero);

        Assert.AreEqual(0, frame.Camera.X);
        Assert.IsNull(frame.Camera.YawRadians);
        Assert.HasCount(1, frame.Tanks);
    }

    [TestMethod]
    public async Task GetFrameAsync_SessionMissing_ReturnsFailure()
    {
        // Projection with a null session record triggers the explicit guard.
        ReplayDecodeProjection projection = Projection(
            viewpointId: null,
            participants: [],
            positions: [],
            events: []) with
        { Session = null };
        StubSessionRepository sessions = new(projection);
        ReplayFrameSource source = new(sessions);

        OperationResult<OverlayFrame> result = await source.GetFrameAsync(
            SessionId, TimeSpan.Zero, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("overlay.session.missing", result.Error?.Code);
    }

    private static ReplayDecodeProjection Projection(
        ParticipantId? viewpointId,
        IReadOnlyList<Participant> participants,
        IReadOnlyList<PositionSample> positions,
        IReadOnlyList<CanonicalEvent> events)
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
            viewpointId,
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
        ParticipantId id,
        long entityId,
        string tankName,
        int team) =>
        new(
            id,
            SessionId,
            AccountId: null,
            EntityId: entityId,
            TeamNumber: team,
            PlayerName: null,
            ClanTag: null,
            VehicleCompactDescriptor: null,
            TankId: null,
            tankName,
            TankClass.Heavy,
            BotStatus.Human,
            EvidenceConfidence.Exact,
            BattleStats: null,
            Evidence);

    private static PositionSample Sample(
        long entityId,
        double seconds,
        double x,
        double y,
        double z,
        double? yaw) =>
        new(
            PositionSampleId.New(),
            SessionId,
            ParticipantId: null,
            EntityId: entityId,
            Sequence: entityId * 100 + (long)(seconds * 10),
            ReplayTime: TimeSpan.FromSeconds(seconds),
            RawX: x,
            RawY: y,
            RawZ: z,
            NormalizedX: null,
            NormalizedY: null,
            RawCoordinateSpace: CoordinateSpace.ReplayRaw,
            NormalizedCoordinateSpace: null,
            Evidence,
            yaw,
            Pitch: null,
            Roll: null);

    private static CanonicalEvent DamageEvent(long entityId, double seconds, int damage) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 1000 + (long)(seconds * 10),
            CanonicalEventKind.Damage,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: entityId,
            ValuesJson: $"{{\"damage\":{damage}}}",
            EvidenceConfidence.Exact,
            Evidence);

    private static CanonicalEvent DestroyedEvent(long entityId, double seconds) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 2000 + (long)(seconds * 10),
            CanonicalEventKind.Destroyed,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: entityId,
            ValuesJson: "{}",
            EvidenceConfidence.Exact,
            Evidence);

    private sealed class StubSessionRepository : ISessionQueryRepository
    {
        private readonly ReplayDecodeProjection? _projection;

        public StubSessionRepository(ReplayDecodeProjection? projection)
        {
            _projection = projection;
        }

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DecodeRunSummary>>([]);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _projection is null
                    ? OperationResult.Failure<ReplayDecodeProjection>(
                        new ApplicationError("replay.session.notfound", "not found"))
                    : OperationResult.Success(_projection));

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }
}
