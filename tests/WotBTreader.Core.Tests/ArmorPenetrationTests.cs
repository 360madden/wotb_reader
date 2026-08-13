using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class ArmorPenetrationTests
{
    private static AimRay RayFromZ(double z) =>
        new(0, 0, z, 0, 0, 1);

    /// <summary>A plate at Z=0 whose normal tilts <paramref name="phi"/>
    /// radians from +Z toward +X (phi 0 = head-on to a +Z ray).</summary>
    private static ArmorPlate TiltedPlate(double thickness, double phi) =>
        new(
            thickness,
            Math.Sin(phi), 0, Math.Cos(phi),
            0, 0, 0);

    private static ShellSpec Shell(
        double penetration0,
        double caliber = 100,
        double dropPerMeter = 0) =>
        new(penetration0, caliber, dropPerMeter);

    [TestMethod]
    public void HeadOn_PenetrationBeatsArmor_Pen()
    {
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: 0),
            Shell(penetration0: 200));

        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
        Assert.IsFalse(verdict.Ricochet);
        Assert.AreEqual(100.0, verdict.HitDistance!.Value, 1e-9);
        Assert.AreEqual(0.0, verdict.IncidenceRadians!.Value, 1e-9);
        Assert.AreEqual(100.0, verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(200.0, verdict.PenetrationMmAtRange!.Value, 1e-9);
    }

    [TestMethod]
    public void Angled45_EffectiveArmorIsThicknessOverCosine()
    {
        double expected = 100.0 / Math.Cos(Math.PI / 4.0);

        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: Math.PI / 4.0),
            Shell(penetration0: 200));

        Assert.AreEqual(Math.PI / 4.0, verdict.IncidenceRadians!.Value, 1e-9);
        Assert.AreEqual(expected, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void Grazing70_NoOvermatch_Ricochets_NoPen()
    {
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: 70.0 * Math.PI / 180.0),
            Shell(penetration0: 1000, caliber: 100));

        Assert.IsTrue(verdict.Ricochet);
        Assert.AreEqual(PenetrationBand.NoPen, verdict.Band);
    }

    [TestMethod]
    public void Grazing70_Overmatch_NoRicochet_CanPen()
    {
        // Caliber 40 > 3 × 10 = 30 thickness suppresses the 70° ricochet;
        // effective armor is 10 / cos(70°) ≈ 29.24, well under 100.
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 10, phi: 70.0 * Math.PI / 180.0),
            Shell(penetration0: 100, caliber: 40));

        Assert.IsFalse(verdict.Ricochet);
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void PenetrationDropsLinearlyWithRange()
    {
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 50, phi: 0),
            Shell(penetration0: 200, caliber: 100, dropPerMeter: 1));

        Assert.AreEqual(100.0, verdict.PenetrationMmAtRange!.Value, 1e-9);
        // 100 > 50 × 1.1 => still a Pen, despite the range drop.
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void WithinMargin_Marginal()
    {
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: 0),
            Shell(penetration0: 100));

        Assert.AreEqual(PenetrationBand.Marginal, verdict.Band);
    }

    [TestMethod]
    public void PenetrationBelowEffective_NoPen()
    {
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: 0),
            Shell(penetration0: 80));

        // 80 < 100 × 0.9 = 90 => NoPen.
        Assert.AreEqual(PenetrationBand.NoPen, verdict.Band);
    }

    [TestMethod]
    public void ParallelRay_Unknown()
    {
        AimRay ray = new(0, 0, -100, 1, 0, 0);
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            ray, TiltedPlate(100, 0), Shell(200));

        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
        Assert.IsNull(verdict.HitDistance);
    }

    [TestMethod]
    public void RayPointingAway_Unknown()
    {
        // Origin already in front of the plate, moving away along +Z.
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(100), TiltedPlate(100, 0), Shell(200));

        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void NonFiniteInputs_FailClosed()
    {
        AimRay nan = new(double.NaN, 0, -100, 0, 0, 1);
        Assert.AreEqual(
            PenetrationBand.Unknown,
            ArmorPenetration.Evaluate(nan, TiltedPlate(100, 0), Shell(200)).Band);

        Assert.AreEqual(
            PenetrationBand.Unknown,
            ArmorPenetration.Evaluate(
                RayFromZ(-100), TiltedPlate(double.NaN, 0), Shell(200)).Band);

        Assert.AreEqual(
            PenetrationBand.Unknown,
            ArmorPenetration.Evaluate(
                RayFromZ(-100), TiltedPlate(100, 0), Shell(double.NaN)).Band);
    }

    [TestMethod]
    public void PenetrationAtRange_ClampsAtZero()
    {
        Assert.AreEqual(
            0.0,
            ArmorPenetration.PenetrationAtRange(10, distance: 50, dropPerMeterMm: 1)!.Value,
            1e-9);
    }

    [TestMethod]
    public void Ricochets_InvalidInput_False()
    {
        Assert.IsFalse(ArmorPenetration.Ricochets(double.NaN, 100, 10, 70));
        Assert.IsFalse(ArmorPenetration.Ricochets(
            70.0 * Math.PI / 180.0, caliberMm: 100, thickness: 10, ricochetDegrees: 90));
    }

    [TestMethod]
    public void Normalization_DigsIn_AvoidsRicochet()
    {
        // 70° incidence with 5° normalization => 65° effective incidence:
        // under the 70° ricochet threshold, so a high-pen shell penetrates.
        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 10, phi: 70.0 * Math.PI / 180.0),
            new ShellSpec(
                Penetration0Mm: 100,
                CaliberMm: 100,
                DropPerMeterMm: 0,
                RicochetDegrees: 70,
                NormalizationDegrees: 5));

        Assert.IsFalse(verdict.Ricochet);
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void Normalization_ReducesEffectiveArmor()
    {
        // 20° incidence, 5° normalization => effective = 100 / cos(15°).
        double expected = 100.0 / Math.Cos(15.0 * Math.PI / 180.0);

        PenetrationVerdict verdict = ArmorPenetration.Evaluate(
            RayFromZ(-100),
            TiltedPlate(thickness: 100, phi: 20.0 * Math.PI / 180.0),
            new ShellSpec(100, 100, 0, 70, NormalizationDegrees: 5));

        Assert.AreEqual(expected, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void FromPiercingPower_MapsTwoPointsToLinearDrop()
    {
        // piercingPower "25 19" over 350 m => drop = 6/350 per meter.
        ShellSpec shell = ShellSpec.FromPiercingPower(
            piercingPowerNearMm: 25, piercingPowerFarMm: 19, maxDistance: 350, caliberMm: 15);

        Assert.AreEqual(25.0, shell.Penetration0Mm, 1e-9);
        Assert.AreEqual((25.0 - 19.0) / 350.0, shell.DropPerMeterMm, 1e-9);

        // At 175 m (mid-range) penetration is the midpoint of the two points.
        Assert.AreEqual(
            22.0,
            ArmorPenetration.PenetrationAtRange(
                shell.Penetration0Mm, distance: 175, shell.DropPerMeterMm)!.Value,
            1e-9);
    }

    [TestMethod]
    public void FromPiercingPower_InvalidMaxDistance_FailClosed()
    {
        ShellSpec shell = ShellSpec.FromPiercingPower(25, 19, maxDistance: 0, caliberMm: 15);

        Assert.AreEqual(
            PenetrationBand.Unknown,
            ArmorPenetration.Evaluate(RayFromZ(-100), TiltedPlate(100, 0), shell).Band);
    }
}
