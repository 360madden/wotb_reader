using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class LiveFrameProjectorTests
{
    private const double Fov = 90.0 * Math.PI / 180.0;

    private static readonly long[] NearestFirstEntityIds = [2, 3, 1];

    private static CameraPoseReadResult Pose(
        float x,
        float y,
        float z,
        float yaw = 0f,
        float pitch = 0f,
        CameraPoseStatus status = CameraPoseStatus.Resolved,
        float[]? basis = null) => new(
        CompletedAtUtc: DateTimeOffset.UtcNow,
        GameVersion: "11.19.0.10",
        status,
        FailureStage: null,
        AvatarAddress: 0,
        CameraAddress: 0,
        CameraStateAddress: 0,
        x,
        y,
        z,
        yaw,
        pitch,
        Basis: basis ?? [],
        AvatarIdentityVerified: status == CameraPoseStatus.Resolved,
        CameraIdentityVerified: status == CameraPoseStatus.Resolved,
        CameraStateIdentityVerified: status == CameraPoseStatus.Resolved,
        ConsistentDoubleRead: status == CameraPoseStatus.Resolved,
        ModuleRooted: true);

    private static LiveFrameTankState Tank(
        int entityId,
        float x,
        float y,
        float z,
        float? yaw = 0.5f,
        Type10EntityPositionStatus status = Type10EntityPositionStatus.Resolved,
        float? hpCurrent = null,
        float? hpMax = null,
        bool? alive = null) => new(
        entityId,
        status,
        x,
        y,
        z,
        yaw,
        hpCurrent,
        hpMax,
        alive,
        FailureStage: null,
        ModuleRooted: true);

    private static LiveFrameReadResult Frame(
        IReadOnlyList<LiveFrameTankState> tanks,
        CameraPoseReadResult? camera = null,
        double? replayTimeSeconds = null,
        Type10EntityPositionStatus status = Type10EntityPositionStatus.Resolved) => new(
        CompletedAtUtc: DateTimeOffset.UtcNow,
        GameVersion: "11.19.0.10",
        status,
        FailureStage: null,
        replayTimeSeconds,
        SameDecodedClockProven: replayTimeSeconds is not null,
        camera,
        tanks,
        RosterCandidatesSeen: 10,
        RosterFilteredOut: 0);

    [TestMethod]
    public void Project_UsesResolvedCameraPoseAndProjectsTanks()
    {
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame(
                [
                    Tank(1, 0, 0, 10),
                    Tank(2, 0, 0, -10),
                ],
                Pose(5f, 0f, 0f)),
            Fov,
            1920,
            1080);

        // Camera: the CAM-001 pose, not the origin fallback.
        Assert.AreEqual(5.0, projection.CameraX);
        Assert.AreEqual(0.0, projection.CameraY);
        Assert.AreEqual(0.0, projection.CameraZ);
        Assert.AreEqual(0.0, projection.CameraYawRadians);
        Assert.AreEqual(0.0, projection.CameraPitchRadians);

        Assert.HasCount(2, projection.Tanks);
        ProjectedTank front = projection.Tanks.Single(tank => tank.EntityId == 1);
        Assert.IsNotNull(front.ScreenX);
        Assert.IsTrue(front.InViewport);
        // Distance from the CAMERA (5,0,0): sqrt(25 + 100) = ~11.18.
        Assert.AreEqual(Math.Sqrt(125), front.DistanceMeters, 1e-6);
        ProjectedTank behind = projection.Tanks.Single(tank => tank.EntityId == 2);
        Assert.IsNull(behind.ScreenX);
        Assert.IsFalse(behind.InViewport);
        // World coords survive for the god-view minimap even behind camera.
        Assert.AreEqual(0.0, behind.WorldX, 1e-9);
        Assert.AreEqual(-10.0, behind.WorldZ, 1e-9);
    }

    [TestMethod]
    public void Project_LiveTankIsHonestUnknownHpAndNoNames()
    {
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(7, 0, 0, 10)], Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        ProjectedTank tank = projection.Tanks.Single();
        Assert.AreEqual(7, tank.EntityId);
        Assert.IsNull(tank.PlayerName);
        Assert.IsNull(tank.TankName);
        Assert.IsNull(tank.ClanTag);
        Assert.IsNull(tank.TeamNumber);
        // Unknown HP: the DTO's "unknown" representation (empty bar, no
        // readout) — never a fabricated fraction.
        Assert.AreEqual(0.0, tank.HpFraction);
        Assert.AreEqual(0, tank.MaxHealth);
        Assert.AreEqual(0, tank.CurrentHealth);
        Assert.AreEqual(0, tank.DamageDealt);
        Assert.AreEqual(0, tank.DamageTaken);
        Assert.AreEqual(0, tank.Kills);
        Assert.IsTrue(tank.Alive);
    }

    [TestMethod]
    public void Project_NonResolvedOrNonFinitePoseFallsBackToOriginCamera()
    {
        // Chain-broken pose: origin fallback, but tanks still project.
        OverlayFrameProjection broken = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f, status: CameraPoseStatus.ChainBroken)),
            Fov,
            1920,
            1080);
        Assert.AreEqual(0.0, broken.CameraX);
        Assert.IsNull(broken.CameraYawRadians);
        Assert.HasCount(1, broken.Tanks);

        // NaN pose: never rendered, same fallback.
        OverlayFrameProjection nan = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(float.NaN, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.AreEqual(0.0, nan.CameraX);
        Assert.IsNull(nan.CameraYawRadians);
    }

    [TestMethod]
    public void Project_OmitsNonResolvedAndUndecodedTanks()
    {
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame(
                [
                    Tank(1, 0, 0, 10),
                    // Region resolved but position failed to decode: X null.
                    new LiveFrameTankState(2, Type10EntityPositionStatus.Resolved, null, 0f, 10f, null, null, null, null, "region-position-decode", false),
                    // Read failed entirely.
                    Tank(3, 0, 0, 10, status: Type10EntityPositionStatus.ReadFailed),
                    // Non-finite position: never projected.
                    Tank(4, float.NaN, 0f, 10f),
                ],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        Assert.HasCount(1, projection.Tanks);
        Assert.AreEqual(1, projection.Tanks[0].EntityId);
    }

    [TestMethod]
    public void Project_AppliesCameraYzSwap_StoredPosAConvention()
    {
        // CAM-010: GameCamera posA is stored (x, z, y) — the W2S seam must
        // yz-swap world->camera space. A stored (5, 3, 7) is world (5, 7, 3).
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(5f, 3f, 7f)),
            Fov,
            1920,
            1080);

        Assert.AreEqual(5.0, projection.CameraX);
        Assert.AreEqual(7.0, projection.CameraY);
        Assert.AreEqual(3.0, projection.CameraZ);

        // A tank at the swapped eye's world position projects with the
        // expected distance (10 m along +Z from the swapped eye).
        ProjectedTank tank = projection.Tanks.Single();
        Assert.IsNotNull(tank.ScreenX);
        Assert.IsTrue(tank.InViewport);
    }

    [TestMethod]
    public void Project_UsesBasisForwardForOrientation_NotRawYawPitch()
    {
        // CAM-012: forward = -row1 of the stride-4 basis. Basis row1 = (0,0,-1)
        // => forward = (0,0,1) (yaw 0, pitch 0), regardless of the raw
        // yaw/pitch fields (DAVA left-handed — not the packet convention).
        CameraPoseReadResult pose = Pose(
            2f, 3f, 4f, yaw: 1.2345f, pitch: 0.7f,
            basis: [1f, 0f, 0f, 0f, 0f, -1f, 0f, 1f, 0f]);
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(1, 2f, 4f, 14f)], pose),
            Fov,
            1920,
            1080);

        // Camera: swapped eye (2, 4, 3) with yaw/pitch derived from the
        // basis forward (0,0,1) — the raw (1.2345, 0.7) must NOT leak.
        Assert.AreEqual(2.0, projection.CameraX);
        Assert.AreEqual(4.0, projection.CameraY);
        Assert.AreEqual(3.0, projection.CameraZ);
        Assert.AreEqual(0.0, projection.CameraYawRadians!.Value, 1e-9);
        Assert.AreEqual(0.0, projection.CameraPitchRadians!.Value, 1e-9);

        // The tank sits 10 m along the camera forward from the swapped eye:
        // it must project to the screen CENTER (960, 540).
        ProjectedTank tank = projection.Tanks.Single();
        Assert.IsNotNull(tank.ScreenX);
        Assert.AreEqual(960.0, tank.ScreenX!.Value, 1e-6);
        Assert.AreEqual(540.0, tank.ScreenY!.Value, 1e-6);
    }

    [TestMethod]
    public void Project_FallsBackToRawYawPitch_WhenBasisMissing()
    {
        // Legacy pose without a persisted basis: the raw yaw/pitch fields are
        // the documented best-effort fallback (documented DAVA-vs-packet risk).
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f, yaw: 0.5f, pitch: -0.25f)),
            Fov,
            1920,
            1080);

        Assert.AreEqual(0.5, projection.CameraYawRadians!.Value, 1e-9);
        Assert.AreEqual(-0.25, projection.CameraPitchRadians!.Value, 1e-9);
    }

    [TestMethod]
    public void Project_EmptyFeedAndNoBeacons()
    {
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        Assert.IsEmpty(projection.Pips);
        Assert.IsEmpty(projection.Kills);
        Assert.IsEmpty(projection.Beacons);
    }

    [TestMethod]
    public void Project_ReplayTimeFromG2Label_ZeroWhenAbsent()
    {
        OverlayFrameProjection labeled = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f), replayTimeSeconds: 150.5),
            Fov,
            1920,
            1080);
        Assert.AreEqual(150.5, labeled.ReplayTime.TotalSeconds, 1e-9);

        OverlayFrameProjection unlabeled = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f), replayTimeSeconds: null),
            Fov,
            1920,
            1080);
        Assert.AreEqual(TimeSpan.Zero, unlabeled.ReplayTime);
    }

    [TestMethod]
    public void Project_SortsNearestFirst()
    {
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame(
                [
                    Tank(1, 0, 0, 50),
                    Tank(2, 0, 0, 10),
                    Tank(3, 0, 0, 20),
                ],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        CollectionAssert.AreEqual(
            NearestFirstEntityIds,
            projection.Tanks.Select(tank => tank.EntityId).ToArray());
    }

    [TestMethod]
    public void Project_HeadingFromYaw_OnlyWhenFinite()
    {
        OverlayFrameProjection withYaw = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10, yaw: 0.5f)], Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.IsNotNull(withYaw.Tanks[0].ScreenHeadingDegrees);

        OverlayFrameProjection noYaw = LiveFrameProjector.Project(
            Frame(
                [new LiveFrameTankState(2, Type10EntityPositionStatus.Resolved, 0f, 0f, 10f, null, null, null, null, null, true)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.IsNull(noYaw.Tanks[0].ScreenHeadingDegrees);
    }

    [TestMethod]
    public void Project_L1HealthMapsToBar_OnlyWhenBothFieldsPresent()
    {
        // Live L1 evidence: current 1228 / max 1550, alive byte true.
        OverlayFrameProjection withHp = LiveFrameProjector.Project(
            Frame(
                [Tank(1, 0, 0, 10, hpCurrent: 1228f, hpMax: 1550f, alive: true)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        ProjectedTank tank = withHp.Tanks.Single();
        Assert.AreEqual(1228L, tank.CurrentHealth);
        Assert.AreEqual(1550L, tank.MaxHealth);
        Assert.AreEqual((double)(1228f / 1550f), tank.HpFraction, 1e-9);
        Assert.IsTrue(tank.Alive);

        // Dead tank: current 0 / max 1550, alive byte false.
        OverlayFrameProjection dead = LiveFrameProjector.Project(
            Frame(
                [Tank(2, 0, 0, 10, hpCurrent: 0f, hpMax: 1550f, alive: false)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.AreEqual(0.0, dead.Tanks.Single().HpFraction, 1e-9);
        Assert.IsFalse(dead.Tanks.Single().Alive);

        // Partial evidence (max missing): stays the honest-unknown shape.
        OverlayFrameProjection partial = LiveFrameProjector.Project(
            Frame(
                [Tank(3, 0, 0, 10, hpCurrent: 800f)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.AreEqual(0.0, partial.Tanks.Single().HpFraction, 1e-9);
        Assert.AreEqual(0L, partial.Tanks.Single().CurrentHealth);
        Assert.AreEqual(0L, partial.Tanks.Single().MaxHealth);
    }

    [TestMethod]
    public void Project_PerIdRosterJoin_FillsNamesWhenTheIdIsInTheMap()
    {
        // The optional decoded-roster join (design
        // live-roster-name-join-design.md): an id present in the map gets
        // its participant's identity — the X4 live-nameplate gap.
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame(
                [Tank(3760578, 0, 0, 10)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080,
            participants: new Dictionary<long, Participant>
            {
                [3760578] = Participant(
                    3760578,
                    "Sniper",
                    "M4 Sherman",
                    "CLAN",
                    teamNumber: 1),
            });

        ProjectedTank tank = projection.Tanks.Single();
        Assert.AreEqual("Sniper", tank.PlayerName);
        Assert.AreEqual("M4 Sherman", tank.TankName);
        Assert.AreEqual("CLAN", tank.ClanTag);
        Assert.AreEqual(1, tank.TeamNumber);
    }

    [TestMethod]
    public void Project_PerIdRosterJoin_StaysAnonymousWithoutTheMap()
    {
        // No participants supplied: names/team stay null (today's behavior).
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(1, 0, 0, 10)], Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);

        ProjectedTank tank = projection.Tanks.Single();
        Assert.IsNull(tank.PlayerName);
        Assert.IsNull(tank.TankName);
        Assert.IsNull(tank.ClanTag);
        Assert.IsNull(tank.TeamNumber);
    }

    [TestMethod]
    public void Project_PerIdRosterJoin_UnknownIdStaysAnonymous()
    {
        // An id NOT in the map is never guessed — the fail-closed rule.
        OverlayFrameProjection projection = LiveFrameProjector.Project(
            Frame([Tank(99, 0, 0, 10)], Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080,
            participants: new Dictionary<long, Participant>
            {
                [1] = Participant(1, "Other", "T-34", null, teamNumber: 2),
            });

        ProjectedTank tank = projection.Tanks.Single();
        Assert.IsNull(tank.PlayerName);
        Assert.IsNull(tank.TankName);
        Assert.IsNull(tank.ClanTag);
        Assert.IsNull(tank.TeamNumber);
    }

    private static Participant Participant(
        long entityId,
        string? playerName,
        string? tankName,
        string? clanTag,
        int? teamNumber) => new(
        ParticipantId.New(),
        BattleSessionId.New(),
        AccountId: null,
        EntityId: entityId,
        TeamNumber: teamNumber,
        PlayerName: playerName,
        ClanTag: clanTag,
        VehicleCompactDescriptor: null,
        TankId: null,
        TankName: tankName,
        TankClass.Unknown,
        BotStatus.Unknown,
        EvidenceConfidence.Unknown,
        BattleStats: null,
        new EvidenceReference(
            SourceArtifactId.New(),
            ArchiveEntry: "meta.json",
            Offset: 0,
            Length: 0,
            new ContentHash(new string('0', ContentHash.Sha256HexLength))));
}
