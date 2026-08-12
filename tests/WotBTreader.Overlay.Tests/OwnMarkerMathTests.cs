using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class OwnMarkerMathTests
{
    [TestMethod]
    public void ClampToViewport_ClampsIntoInsetRect()
    {
        // A projection below the viewport bottom clamps to the bottom edge
        // (inset by Margin); an X already inside stays put.
        (double X, double Y)? clamped =
            OwnMarkerMath.ClampToViewport(358.9, 500.0, 640.0, 360.0);

        Assert.IsNotNull(clamped);
        Assert.AreEqual(358.9, clamped!.Value.X, 1e-9);
        Assert.AreEqual(360.0 - OwnMarkerMath.Margin, clamped.Value.Y, 1e-9);
    }

    [TestMethod]
    public void ClampToViewport_ClampsBothAxesOutside()
    {
        (double X, double Y)? clamped =
            OwnMarkerMath.ClampToViewport(-100.0, -100.0, 640.0, 360.0);

        Assert.IsNotNull(clamped);
        Assert.AreEqual(OwnMarkerMath.Margin, clamped!.Value.X, 1e-9);
        Assert.AreEqual(OwnMarkerMath.Margin, clamped.Value.Y, 1e-9);
    }

    [TestMethod]
    public void ClampToViewport_DegenerateViewport_FailsClosed()
    {
        // A collapsed viewport can never host a visible marker.
        Assert.IsNull(OwnMarkerMath.ClampToViewport(10, 10, 30, 30));
        Assert.IsNull(OwnMarkerMath.ClampToViewport(double.NaN, 10, 640, 360));
    }

    [TestMethod]
    public void AngleToward_PointsDownForBelowViewportTank()
    {
        // Tank below the viewport: the chevron points toward +Y (downward),
        // π/2 in the top-left-origin pixel convention.
        double angle = OwnMarkerMath.AngleToward(358.9, 500.0, 358.9, 360.0 - OwnMarkerMath.Margin);
        Assert.AreEqual(Math.PI / 2.0, angle, 1e-9);
    }

    [TestMethod]
    public void AngleToward_PointsRightForRightSideTank()
    {
        double angle = OwnMarkerMath.AngleToward(800.0, 180.0, 640.0 - OwnMarkerMath.Margin, 180.0);
        Assert.AreEqual(0.0, angle, 1e-9);
    }
}
