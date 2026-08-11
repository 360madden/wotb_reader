using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class OverlayFrameProjectorTests
{
    private const double Fov = 90.0 * Math.PI / 180.0;

    [TestMethod]
    public void Project_ProjectsInFrontTanksAndMarksBehindAsNull()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                // In front of the camera: projects to center.
                new OverlayTankState(1, 0, 0, 10, 0.1, 1.0, true, 1, "A", null, "TankA", "Heavy", 10),
                // Behind the camera: never projected.
                new OverlayTankState(2, 0, 0, -10, 0.1, 0.5, false, 2, "B", null, "TankB", "Heavy", 10),
            },
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.HasCount(2, projection.Tanks);
        ProjectedTank front = projection.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(960, front.ScreenX!.Value, 1e-6);
        Assert.AreEqual(540, front.ScreenY!.Value, 1e-6);
        Assert.IsTrue(front.InViewport);
        ProjectedTank behind = projection.Tanks.Single(tank => tank.EntityId == 2);
        Assert.IsNull(behind.ScreenX);
        Assert.IsFalse(behind.InViewport);
    }

    [TestMethod]
    public void Project_CarriesWorldPositionForMinimap()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, -123.5, 40, 78.25, 0.1, 1.0, true, 1, "A", null, "TankA", "Heavy", 10),
                // Behind the camera: world coords must survive even when the
                // screen projection is null (the minimap draws god-view).
                new OverlayTankState(2, 55, 40, -200, 0.1, 0.5, false, 2, "B", null, "TankB", "Heavy", 10),
            },
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        ProjectedTank front = projection.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(-123.5, front.WorldX, 1e-9);
        Assert.AreEqual(78.25, front.WorldZ, 1e-9);
        ProjectedTank behind = projection.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(55, behind.WorldX, 1e-9);
        Assert.AreEqual(-200, behind.WorldZ, 1e-9);
        Assert.IsNull(behind.ScreenX);
    }

    [TestMethod]
    public void Project_CarriesScoreboardTotalsThrough()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, 0, 0, 10, 0.1, 1.0, true, 1, "A", null, "TankA", "Heavy", 10,
                    DamageDealt: 1200, DamageTaken: 400, Kills: 2),
                new OverlayTankState(2, 0, 0, 50, 0.1, 0.0, false, 2, "B", null, "TankB", "Heavy", 50,
                    DamageDealt: 800, DamageTaken: 900, Kills: 0),
            },
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        ProjectedTank leader = projection.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(1200, leader.DamageDealt);
        Assert.AreEqual(400, leader.DamageTaken);
        Assert.AreEqual(2, leader.Kills);
        ProjectedTank behind = projection.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(800, behind.DamageDealt);
        Assert.AreEqual(900, behind.DamageTaken);
        Assert.AreEqual(0, behind.Kills);
    }

    [TestMethod]
    public void Project_CarriesKillFeedThrough()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(20),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            [],
            new[]
            {
                new OverlayKill(50, 70, TimeSpan.FromSeconds(20)),
                new OverlayKill(51, null, TimeSpan.FromSeconds(30)),
            });

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.HasCount(2, projection.Kills);
        Assert.AreEqual(50, projection.Kills[0].VictimEntityId);
        Assert.AreEqual(70, projection.Kills[0].KillerEntityId);
        Assert.IsNull(projection.Kills[1].KillerEntityId);
    }

    [TestMethod]
    public void Project_SortsByDistanceNearestFirst()
    {
        OverlayFrame frame = new(
            TimeSpan.Zero,
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(3, 0, 0, 300, null, 1.0, true, 2, "Far", null, "FarTank", "Heavy", 300),
                new OverlayTankState(1, 0, 0, 10, null, 1.0, true, 1, "Near", null, "NearTank", "Heavy", 10),
                new OverlayTankState(2, 0, 0, 50, null, 1.0, true, 1, "Mid", null, "MidTank", "Heavy", 50),
            },
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.AreEqual(1, projection.Tanks[0].EntityId);
        Assert.AreEqual(2, projection.Tanks[1].EntityId);
        Assert.AreEqual(3, projection.Tanks[2].EntityId);
    }

    [TestMethod]
    public void Project_CarriesCameraAndReplayTime()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(42),
            new OverlayCamera(1, 2, 3, YawRadians: 0.5, PitchRadians: -0.1, RollRadians: 0),
            [],
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.AreEqual(TimeSpan.FromSeconds(42), projection.ReplayTime);
        Assert.AreEqual(1, projection.CameraX!.Value, 1e-9);
        Assert.AreEqual(0.5, projection.CameraYawRadians!.Value, 1e-9);
        Assert.AreEqual(-0.1, projection.CameraPitchRadians!.Value, 1e-9);
    }

    [TestMethod]
    public void Project_ProjectsVisibleBeaconsAndDropsBehindCamera()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(
            frame,
            Fov,
            1920,
            1080,
            new[]
            {
                new OverlayBeacon("Front", 0, 0, 100, "#FFD700", null, null),
                new OverlayBeacon("Behind", 0, 0, -100, "#FF0000", null, null),
            });

        Assert.HasCount(2, projection.Beacons);
        ProjectedBeacon front = projection.Beacons.Single(beacon => beacon.Name == "Front");
        Assert.AreEqual(960, front.ScreenX!.Value, 1e-6);
        Assert.AreEqual(540, front.ScreenY!.Value, 1e-6);
        Assert.IsTrue(front.InViewport);
        Assert.AreEqual(100, front.DistanceMeters, 1e-9);
        // World coords ride through for the minimap (god-view), even for the
        // behind-camera beacon whose screen projection is null.
        Assert.AreEqual(0.0, front.WorldX, 1e-9);
        Assert.AreEqual(100.0, front.WorldZ, 1e-9);
        ProjectedBeacon behind = projection.Beacons.Single(beacon => beacon.Name == "Behind");
        Assert.IsNull(behind.ScreenX);
        Assert.IsFalse(behind.InViewport);
        Assert.AreEqual(0.0, behind.WorldX, 1e-9);
        Assert.AreEqual(-100.0, behind.WorldZ, 1e-9);
    }

    [TestMethod]
    public void Project_FiltersBeaconsByReplayTimeWindow()
    {
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(50),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(
            frame,
            Fov,
            1920,
            1080,
            new[]
            {
                new OverlayBeacon("Always", 0, 0, 100, "#FFD700", null, null),
                new OverlayBeacon("EarlyOnly", 0, 0, 100, "#FFD700", null, TimeSpan.FromSeconds(10)),
                new OverlayBeacon("NotYet", 0, 0, 100, "#FFD700", TimeSpan.FromSeconds(60), null),
                new OverlayBeacon("MidWindow", 0, 0, 100, "#FFD700", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)),
            });

        Assert.HasCount(2, projection.Beacons);
        Assert.IsTrue(projection.Beacons.Any(beacon => beacon.Name == "Always"));
        Assert.IsFalse(projection.Beacons.Any(beacon => beacon.Name == "EarlyOnly"));
        Assert.IsFalse(projection.Beacons.Any(beacon => beacon.Name == "NotYet"));
        Assert.IsTrue(projection.Beacons.Any(beacon => beacon.Name == "MidWindow"));
    }

    [TestMethod]
    public void Project_PipsAnchorAtAffectedTankPixel_OnlyWhenInViewport()
    {
        // A damage pip and a death pip for an in-viewport tank anchor at its
        // pixel; a pip for a behind-camera tank is dropped entirely.
        OverlayFrame frame = new(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, 0, 0, 10, 0.1, 0.6, true, 1, "A", null, "TankA", "Heavy", 10),
                new OverlayTankState(2, 0, 0, -10, 0.1, 0.0, false, 2, "B", null, "TankB", "Heavy", 10),
            },
            new[]
            {
                new OverlayEventPip(1, CanonicalEventKind.Damage, 60, TimeSpan.FromSeconds(9.5)),
                new OverlayEventPip(1, CanonicalEventKind.Destroyed, 0, TimeSpan.FromSeconds(9.8)),
                new OverlayEventPip(2, CanonicalEventKind.Damage, 90, TimeSpan.FromSeconds(9.5)),
            });

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.HasCount(2, projection.Pips);
        ProjectedPip damage = projection.Pips.Single(pip => pip.Kind == CanonicalEventKind.Damage);
        Assert.AreEqual(1, damage.EntityId);
        Assert.AreEqual(60, damage.Damage);
        Assert.AreEqual(960, damage.ScreenX, 1e-6);
        Assert.AreEqual(540, damage.ScreenY, 1e-6);
        ProjectedPip death = projection.Pips.Single(pip => pip.Kind == CanonicalEventKind.Destroyed);
        Assert.AreEqual(1, death.EntityId);
    }

    [TestMethod]
    public void Project_NoPipsWhenNoneProvided()
    {
        OverlayFrame frame = new(
            TimeSpan.Zero,
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.HasCount(0, projection.Pips);
    }
}
