using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class AimGeometryTests
{
    private const double Tolerance = 15.0 * Math.PI / 180.0;

    [TestMethod]
    public void TargetDeadAhead_ZeroError()
    {
        // Hull yaw 0 faces +Z; target 20 m ahead on +Z.
        double error = AimGeometry.HullAimErrorRadians(
            yawRadians: 0, fromX: 0, fromZ: 0, toX: 0, toZ: 20);

        Assert.AreEqual(0.0, error, 1e-9);
        Assert.IsTrue(AimGeometry.HullAimsAt(0, 0, 0, 0, 20, Tolerance));
    }

    [TestMethod]
    public void TargetToTheRight_NegativeError_NinetyDegrees()
    {
        // +X is to the right when facing +Z; heading +pi/2, error -pi/2.
        double error = AimGeometry.HullAimErrorRadians(
            0, fromX: 0, fromZ: 0, toX: 20, toZ: 0);

        Assert.AreEqual(-Math.PI / 2.0, error, 1e-9);
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, 20, 0, Tolerance));
    }

    [TestMethod]
    public void HullTurnedQuarter_FacesTarget_ZeroError()
    {
        // Yaw +pi/2 faces +X (mirrors WorldToScreen.YawQuarterTurn);
        // target ahead at +X => dead ahead.
        double error = AimGeometry.HullAimErrorRadians(
            Math.PI / 2.0, fromX: 0, fromZ: 0, toX: 20, toZ: 0);

        Assert.AreEqual(0.0, error, 1e-9);
        Assert.IsTrue(AimGeometry.HullAimsAt(Math.PI / 2.0, 0, 0, 20, 0, Tolerance));
    }

    [TestMethod]
    public void TargetBehind_False()
    {
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, 0, -20, Tolerance));
    }

    [TestMethod]
    public void Boundary_ToleranceInclusive()
    {
        // Just inside the arc (|error| < tolerance, so far inside that atan2
        // rounding cannot push it over) => aimed; just outside => not.
        double inside = Math.Tan(Tolerance - 1e-6) * 20.0;
        double outside = Math.Tan(Tolerance + 1e-6) * 20.0;
        Assert.IsTrue(AimGeometry.HullAimsAt(0, 0, 0, inside, 20, Tolerance));
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, outside, 20, Tolerance));
    }

    [TestMethod]
    public void WrapAround_MinusPiToPi_Normalizes()
    {
        // Target placed along heading -3.0 rad: direction (sin(-3), cos(-3))
        // = (-sin 3, cos 3). yaw 3.0 vs heading -3.0 are 16.2 deg apart
        // across the -pi/pi wrap.
        double error = AimGeometry.HullAimErrorRadians(3.0, 0, 0,
            -Math.Sin(3.0) * 20.0, Math.Cos(3.0) * 20.0);

        // The target sits exactly along heading -3.0, so the error is yaw -
        // heading = 3.0 - (-3.0) = 6.0, normalized to 6.0 - 2pi ~= -0.283.
        double expected = 6.0 - 2.0 * Math.PI;
        Assert.AreEqual(expected, error, 1e-9);
    }

    [TestMethod]
    public void NonFiniteYaw_FailClosed()
    {
        Assert.IsFalse(AimGeometry.HullAimsAt(
            double.NaN, 0, 0, 0, 20, Tolerance));
        Assert.IsFalse(AimGeometry.HullAimsAt(
            double.PositiveInfinity, 0, 0, 0, 20, Tolerance));
    }

    [TestMethod]
    public void ZeroDistanceToTarget_FailClosed()
    {
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, 0, 0, Tolerance));
    }

    [TestMethod]
    public void OutOfRangeTolerance_FailClosed()
    {
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, 0, 20, 0));
        Assert.IsFalse(AimGeometry.HullAimsAt(0, 0, 0, 0, 20, Math.PI + 0.1));
    }
}
