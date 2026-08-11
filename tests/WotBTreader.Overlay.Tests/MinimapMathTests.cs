using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class MinimapMathTests
{
    [TestMethod]
    public void Normalize_MapsWorldExtentToZeroToOne()
    {
        // Boundary x[-100,100], z[0,200]: x=-100 -> u=0, z=0 -> v=0,
        // x=0 -> u=0.5, z=100 -> v=0.5, x=100 -> u=1, z=200 -> v=1.
        (double U, double V)? nw = MinimapMath.Normalize(-100, 0, -100, 100, 0, 200);
        Assert.IsNotNull(nw);
        Assert.AreEqual(0.0, nw!.Value.U, 1e-9);
        Assert.AreEqual(0.0, nw.Value.V, 1e-9);

        (double U, double V)? center = MinimapMath.Normalize(0, 100, -100, 100, 0, 200);
        Assert.AreEqual(0.5, center!.Value.U, 1e-9);
        Assert.AreEqual(0.5, center.Value.V, 1e-9);

        (double U, double V)? se = MinimapMath.Normalize(100, 200, -100, 100, 0, 200);
        Assert.AreEqual(1.0, se!.Value.U, 1e-9);
        Assert.AreEqual(1.0, se.Value.V, 1e-9);
    }

    [TestMethod]
    public void Normalize_OutOfBoundaryPositionStillMapsLinearly()
    {
        // Positions outside the observed envelope clamp-free: the dot lands
        // outside the panel and is clipped, exactly like the position plot.
        (double U, double V)? beyond = MinimapMath.Normalize(200, 300, 0, 100, 0, 100);
        Assert.AreEqual(2.0, beyond!.Value.U, 1e-9);
        Assert.AreEqual(3.0, beyond.Value.V, 1e-9);
    }

    [TestMethod]
    public void Normalize_DegenerateBoundaryReturnsNull()
    {
        Assert.IsNull(MinimapMath.Normalize(10, 10, 0, 0, 0, 100));
        Assert.IsNull(MinimapMath.Normalize(10, 10, 0, 100, 0, 0));
        Assert.IsNull(MinimapMath.Normalize(10, 10, 0, -5, 0, 100));
    }
}
