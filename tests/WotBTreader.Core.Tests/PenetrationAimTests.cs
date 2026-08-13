using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class PenetrationAimTests
{
    private static OverlayCamera Camera(double yaw = 0, double pitch = 0, double x = 0, double z = -100) =>
        new(x, 0, z, yaw, pitch, null);

    private static OverlayTankState Tank(long id, double x, double z, double? yaw = 0) =>
        new(id, x, 0, z, yaw, HpFraction: 1.0, Alive: true, TeamNumber: 1,
            PlayerName: null, ClanTag: null, TankName: null, TankClass: null,
            DistanceMeters: 0);

    private static readonly TankArmor Armor = new(FrontMm: 93.4, SideMm: 53.4, RearMm: 40);

    [TestMethod]
    public void BuildAimRay_Yaw0FacesPositiveZ()
    {
        AimRay? ray = PenetrationAim.BuildAimRay(Camera(yaw: 0, pitch: 0));

        Assert.IsNotNull(ray);
        Assert.AreEqual(0.0, ray.Value.DirectionX, 1e-9);
        Assert.AreEqual(0.0, ray.Value.DirectionY, 1e-9);
        Assert.AreEqual(1.0, ray.Value.DirectionZ, 1e-9);
    }

    [TestMethod]
    public void BuildAimRay_YawQuarterFacesPositiveX()
    {
        AimRay? ray = PenetrationAim.BuildAimRay(Camera(yaw: Math.PI / 2, pitch: 0));

        Assert.IsNotNull(ray);
        Assert.AreEqual(1.0, ray.Value.DirectionX, 1e-9);
        Assert.AreEqual(0.0, ray.Value.DirectionZ, 1e-9);
    }

    [TestMethod]
    public void BuildAimRay_NoRotation_Null()
    {
        OverlayCamera camera = new(0, 0, -100, null, null, null);
        Assert.IsNull(PenetrationAim.BuildAimRay(camera));
    }

    [TestMethod]
    public void AimedTankId_PicksNearest()
    {
        AimRay ray = PenetrationAim.BuildAimRay(Camera())!.Value;
        OverlayTankState[] tanks = [Tank(1, 0, 0), Tank(2, 0, 50)];

        Assert.AreEqual(1L, PenetrationAim.AimedTankId(ray, tanks));
    }

    [TestMethod]
    public void AimedTankId_OffAxis_NoHit()
    {
        AimRay ray = PenetrationAim.BuildAimRay(Camera())!.Value;
        OverlayTankState[] tanks = [Tank(1, 10, 0)];

        Assert.IsNull(PenetrationAim.AimedTankId(ray, tanks));
    }

    [TestMethod]
    public void AimedTankId_VerticalRay_Null()
    {
        AimRay vertical = new(0, -100, 0, 0, 1, 0);
        OverlayTankState[] tanks = [Tank(1, 0, 0)];

        Assert.IsNull(PenetrationAim.AimedTankId(vertical, tanks));
    }

    [TestMethod]
    public void EvaluateAgainst_FrontHeadOn_UsesFrontArmor()
    {
        // Tank faces +Z; ray arrives from +Z (in front) toward -Z.
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(0.0, verdict.IncidenceRadians!.Value, 1e-9);
        Assert.AreEqual(93.4, verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainst_SideShot_UsesSideArmor()
    {
        // Ray arrives from +X toward -X (perpendicular to the +Z facing).
        AimRay ray = new(100, 0, 0, -1, 0, 0);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(53.4, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainst_RearShot_UsesRearArmor()
    {
        // Ray arrives from -Z (behind) toward +Z.
        AimRay ray = new(0, 0, -100, 0, 0, 1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(40.0, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainst_NoHullYaw_Unknown()
    {
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: null), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }
}
