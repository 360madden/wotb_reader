using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class WorldToScreenTests
{
    private const double Fov = 90.0 * Math.PI / 180.0;
    private const double Width = 1920;
    private const double Height = 1080;

    [TestMethod]
    public void PointStraightAhead_ProjectsToCenter()
    {
        // Camera at origin, yaw 0 (facing +Z), pitch 0; point 10m ahead.
        ScreenPoint? point = WorldToScreen.Project(
            eyeX: 0, eyeY: 0, eyeZ: 0, yaw: 0, pitch: 0, Fov, Width, Height,
            worldX: 0, worldY: 0, worldZ: 10);

        Assert.IsNotNull(point);
        Assert.AreEqual(Width / 2, point.Value.X, 1e-6);
        Assert.AreEqual(Height / 2, point.Value.Y, 1e-6);
        Assert.AreEqual(10, point.Value.Depth, 1e-6);
    }

    [TestMethod]
    public void PointToTheRight_ProjectsRightOfCenter()
    {
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, Fov, Width, Height,
            worldX: 5, worldY: 0, worldZ: 10);

        Assert.IsNotNull(point);
        Assert.IsGreaterThan(Width / 2, point.Value.X);
        Assert.AreEqual(Height / 2, point.Value.Y, 1e-6);
    }

    [TestMethod]
    public void PointAbove_ProjectsAboveCenter()
    {
        // Screen Y grows downward, so an up-world point lands at Y < center.
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, Fov, Width, Height,
            worldX: 0, worldY: 5, worldZ: 10);

        Assert.IsNotNull(point);
        Assert.AreEqual(Width / 2, point.Value.X, 1e-6);
        Assert.IsLessThan(Height / 2, point.Value.Y);
    }

    [TestMethod]
    public void PointBehindCamera_ReturnsNull()
    {
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, Fov, Width, Height,
            worldX: 0, worldY: 0, worldZ: -10);

        Assert.IsNull(point);
    }

    [TestMethod]
    public void YawQuarterTurn_FacesPositiveX()
    {
        // Yaw +pi/2 rotates the facing from +Z to +X (packet convention).
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: Math.PI / 2, pitch: 0, Fov, Width, Height,
            worldX: 10, worldY: 0, worldZ: 0);

        Assert.IsNotNull(point);
        Assert.AreEqual(Width / 2, point.Value.X, 1e-6);
        Assert.AreEqual(Height / 2, point.Value.Y, 1e-6);
    }

    [TestMethod]
    public void PitchLooksUp_ProjectsHorizonBelowCenter()
    {
        // Camera pitched up: the world point straight ahead drops below center.
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0.5, Fov, Width, Height,
            worldX: 0, worldY: 0, worldZ: 10);

        Assert.IsNotNull(point);
        Assert.IsGreaterThan(Height / 2, point.Value.Y);
    }

    [TestMethod]
    public void WiderFov_BringsPointCloserToCenter()
    {
        ScreenPoint? narrow = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, verticalFovRadians: 60.0 * Math.PI / 180.0, Width, Height,
            worldX: 5, worldY: 0, worldZ: 10);
        ScreenPoint? wide = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, verticalFovRadians: 120.0 * Math.PI / 180.0, Width, Height,
            worldX: 5, worldY: 0, worldZ: 10);

        Assert.IsNotNull(narrow);
        Assert.IsNotNull(wide);
        double centerDistance(double x) => Math.Abs(x - Width / 2);
        Assert.IsLessThan(
            centerDistance(narrow.Value.X), centerDistance(wide.Value.X));
    }

    [TestMethod]
    public void CameraWithoutRotationEvidence_ReturnsNull()
    {
        OverlayCamera camera = new(0, 0, 0, YawRadians: null, PitchRadians: null, RollRadians: null);

        ScreenPoint? point = WorldToScreen.Project(camera, Fov, Width, Height, 0, 0, 10);

        Assert.IsNull(point);
    }

    [TestMethod]
    public void InvalidViewport_ReturnsNull()
    {
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: 0, Fov, viewportWidth: 0, viewportHeight: Height,
            worldX: 0, worldY: 0, worldZ: 10);

        Assert.IsNull(point);
    }

    [TestMethod]
    public void NonFiniteYaw_ReturnsNull()
    {
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: double.NaN, pitch: 0, Fov, Width, Height,
            worldX: 0, worldY: 0, worldZ: 10);

        Assert.IsNull(point);
    }

    [TestMethod]
    public void NonFinitePitch_ReturnsNull()
    {
        ScreenPoint? point = WorldToScreen.Project(
            0, 0, 0, yaw: 0, pitch: double.PositiveInfinity, Fov, Width, Height,
            worldX: 0, worldY: 0, worldZ: 10);

        Assert.IsNull(point);
    }

    [TestMethod]
    public void InsideViewport_ReflectsBounds()
    {
        var inside = new ScreenPoint(100, 100, 10);
        var outside = new ScreenPoint(5000, 100, 10);

        Assert.IsTrue(inside.IsInsideViewport(Width, Height));
        Assert.IsFalse(outside.IsInsideViewport(Width, Height));
    }

    [TestMethod]
    public void ScreenHeading_TankFacingRight_PointsScreenRight()
    {
        // Camera at origin facing +Z; tank ahead at (0,0,10) facing +X. The
        // probe 8m along +X projects right of the tank's center projection:
        // heading 90 degrees = clockwise from screen-up = screen-right.
        OverlayCamera camera = new(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: null);

        double? heading = WorldToScreen.ScreenHeadingDegrees(
            camera, Fov, Width, Height, worldX: 0, worldY: 0, worldZ: 10,
            yawRadians: Math.PI / 2);

        Assert.IsNotNull(heading);
        Assert.AreEqual(90.0, heading.Value, 1e-6);
    }

    [TestMethod]
    public void ScreenHeading_OffCenterTankFacingAway_PointsToVanishingPoint()
    {
        // Tank offset to the RIGHT of the view axis (x=5, z=10) facing +Z
        // (away): its nose converges on the vanishing point, which on screen
        // lies LEFT of the tank's projection — heading -90 (screen-left).
        // This is the perspective-correct behavior the two-point probe
        // produces (a screen-constant vector would instead point straight up).
        OverlayCamera camera = new(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: null);

        double? heading = WorldToScreen.ScreenHeadingDegrees(
            camera, Fov, Width, Height, worldX: 5, worldY: 0, worldZ: 10,
            yawRadians: 0);

        Assert.IsNotNull(heading);
        Assert.AreEqual(-90.0, heading.Value, 1e-6);
    }

    [TestMethod]
    public void ScreenHeading_OffCenterTankFacingCamera_PointsAwayFromVanishingPoint()
    {
        // Tank offset to the RIGHT (x=5, z=10) facing -Z (toward the
        // camera): its nose comes at the viewer, so on screen it converges
        // AWAY from the vanishing point — the probe lands between the tank
        // and the camera and projects further from center (screen-right, +90).
        // The mirror of the facing-away case above.
        OverlayCamera camera = new(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: null);

        double? heading = WorldToScreen.ScreenHeadingDegrees(
            camera, Fov, Width, Height, worldX: 5, worldY: 0, worldZ: 10,
            yawRadians: Math.PI);

        Assert.IsNotNull(heading);
        Assert.AreEqual(90.0, heading.Value, 1e-6);
    }

    [TestMethod]
    public void ScreenHeading_FacingAlongViewAxis_ReturnsNull()
    {
        // Tank dead-ahead facing exactly +Z (the view axis): the probe
        // projects to the SAME pixel as the tank, so the heading is
        // unobservable — fail closed with no arrow.
        OverlayCamera camera = new(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: null);

        double? heading = WorldToScreen.ScreenHeadingDegrees(
            camera, Fov, Width, Height, worldX: 0, worldY: 0, worldZ: 10,
            yawRadians: 0);

        Assert.IsNull(heading);
    }

    [TestMethod]
    public void ScreenHeading_NoCameraRotationEvidence_ReturnsNull()
    {
        OverlayCamera camera = new(0, 0, 0, YawRadians: null, PitchRadians: null, RollRadians: null);

        double? heading = WorldToScreen.ScreenHeadingDegrees(
            camera, Fov, Width, Height, worldX: 0, worldY: 0, worldZ: 10,
            yawRadians: 0.5);

        Assert.IsNull(heading);
    }
}
