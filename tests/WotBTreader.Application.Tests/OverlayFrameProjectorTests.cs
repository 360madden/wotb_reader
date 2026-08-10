using WotBTreader.Application.Replay;
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
            });

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
            });

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
            []);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(frame, Fov, 1920, 1080);

        Assert.AreEqual(TimeSpan.FromSeconds(42), projection.ReplayTime);
        Assert.AreEqual(1, projection.CameraX!.Value, 1e-9);
        Assert.AreEqual(0.5, projection.CameraYawRadians!.Value, 1e-9);
        Assert.AreEqual(-0.1, projection.CameraPitchRadians!.Value, 1e-9);
    }
}
