using WotBTreader.Application.Replay;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class PublishedMarkerShotJoinTests
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
    public void MissingViewpointWhenSessionHasNoViewpoint()
    {
        ReplayDecodeProjection projection = Projection(
            participants: [],
            positions: [],
            events: []);

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(projection, []);

        Assert.AreEqual(MissingViewpointSummary(), summary);
    }

    [TestMethod]
    public void IncomingShotIsNotCountedAsViewpointShot()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 2, victim: 1, penetrated: true));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0))]);

        Assert.AreEqual(EmptyJoinSummary(), summary);
    }

    [TestMethod]
    public void MissingAttackerInJsonCountsMissingAttacker()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEventMissingAttacker(sequence: 1, seconds: 5.0, victim: 2));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0))]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 0,
                Joined: 0,
                NoSampleBefore: 0,
                LagExceeded: 0,
                MissingAttacker: 1,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    [TestMethod]
    public void JoinsViewpointShotWhenG2SampleWithinLagAimsAtVictim()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0))]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 1,
                Joined: 1,
                NoSampleBefore: 0,
                LagExceeded: 0,
                MissingAttacker: 0,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    [TestMethod]
    public void SampleAfterShotOnlyCountsNoSampleBefore()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0) + TimeSpan.FromMilliseconds(50))]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 1,
                Joined: 0,
                NoSampleBefore: 1,
                LagExceeded: 0,
                MissingAttacker: 0,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    [TestMethod]
    public void Sample400MillisecondsBeforeCountsLagExceeded()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0) - TimeSpan.FromMilliseconds(400))]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 1,
                Joined: 0,
                NoSampleBefore: 0,
                LagExceeded: 1,
                MissingAttacker: 0,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    [TestMethod]
    public void UnprovenDecodedClockSampleIsIgnored()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true));

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(
            projection,
            [MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0), sameDecodedClockProven: false)]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 1,
                Joined: 0,
                NoSampleBefore: 1,
                LagExceeded: 0,
                MissingAttacker: 0,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    [TestMethod]
    public void MarkerAimed90DegreesOffDoesNotJoin()
    {
        ParticipantId viewpointId = ParticipantId.New();
        ReplayDecodeProjection projection = StandardProjection(
            viewpointId,
            ShotEvent(sequence: 1, seconds: 5.0, attacker: 1, victim: 2, penetrated: true));

        PublishedMarkerSample aimed = MarkerAimedAtVictim(TimeSpan.FromSeconds(5.0));
        PublishedMarkerSample off = aimed with { MarkerYawRadians = aimed.MarkerYawRadians + (Math.PI / 2.0) };

        PublishedMarkerJoinSummary summary = PublishedMarkerShotJoin.Evaluate(projection, [off]);

        Assert.AreEqual(
            new PublishedMarkerJoinSummary(
                ViewpointShots: 1,
                Joined: 0,
                NoSampleBefore: 0,
                LagExceeded: 0,
                MissingAttacker: 0,
                MissingViewpoint: 0,
                MissingPosition: 0),
            summary);
    }

    private static PublishedMarkerJoinSummary MissingViewpointSummary() =>
        new(
            ViewpointShots: 0,
            Joined: 0,
            NoSampleBefore: 0,
            LagExceeded: 0,
            MissingAttacker: 0,
            MissingViewpoint: 1,
            MissingPosition: 0);

    private static PublishedMarkerJoinSummary EmptyJoinSummary() =>
        new(
            ViewpointShots: 0,
            Joined: 0,
            NoSampleBefore: 0,
            LagExceeded: 0,
            MissingAttacker: 0,
            MissingViewpoint: 0,
            MissingPosition: 0);

    // Attacker at (0, 0, -100), victim at (0, 0, 0): center-line from hull+1.5m Y.
    private static PublishedMarkerSample MarkerAimedAtVictim(
        TimeSpan replayTime,
        bool sameDecodedClockProven = true)
    {
        const double originX = 0;
        const double originY = 1.5;
        const double originZ = -100;
        const double dx = 0 - originX;
        const double dy = 0 - originY;
        const double dz = 0 - originZ;
        double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        return new PublishedMarkerSample(
            replayTime,
            Math.Atan2(dx, dz),
            Math.Asin(dy / length),
            sameDecodedClockProven);
    }

    private static ReplayDecodeProjection StandardProjection(
        ParticipantId viewpointId,
        CanonicalEvent shot) =>
        Projection(
            participants:
            [
                Participant(entityId: 1, tankId: "uk:A", tankName: "Attacker Tank", viewpointId),
                Participant(entityId: 2, tankId: "uk:V", tankName: "Victim Tank"),
            ],
            positions:
            [
                Sample(entityId: 1, seconds: 5.0, x: 0, z: -100),
                Sample(entityId: 2, seconds: 5.0, x: 0, z: 0),
            ],
            events: [shot],
            viewpointParticipantId: viewpointId);

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

    private static CanonicalEvent ShotEventMissingAttacker(
        long sequence,
        double seconds,
        long victim) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            sequence,
            CanonicalEventKind.ShotImpact,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: victim,
            $"{{\"victimEntityId\":{victim},\"hitResult\":3,\"penetrated\":true}}",
            EvidenceConfidence.Exact,
            Evidence);
}
