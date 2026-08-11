using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
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
        CameraPoseStatus status = CameraPoseStatus.Resolved) => new(
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
        Basis: [],
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
        Type10EntityPositionStatus status = Type10EntityPositionStatus.Resolved) => new(
        entityId,
        status,
        x,
        y,
        z,
        yaw,
        Hp: null,
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
                    new LiveFrameTankState(2, Type10EntityPositionStatus.Resolved, null, 0f, 10f, null, null, "region-position-decode", false),
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
                [new LiveFrameTankState(2, Type10EntityPositionStatus.Resolved, 0f, 0f, 10f, null, null, null, true)],
                Pose(0f, 0f, 0f)),
            Fov,
            1920,
            1080);
        Assert.IsNull(noYaw.Tanks[0].ScreenHeadingDegrees);
    }
}
