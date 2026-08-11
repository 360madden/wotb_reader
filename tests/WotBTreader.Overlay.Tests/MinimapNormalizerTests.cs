using WotBTreader.ApiContracts;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class MinimapNormalizerTests
{
    [TestMethod]
    public void Normalize_CornersMapToUnitSquare()
    {
        // Boundary 0..1000 both axes: corners land exactly on 0/1.
        (double U, double V)? northWest = MinimapNormalizer.Normalize(0, 0, 0, 1000, 0, 1000);
        (double U, double V)? southEast = MinimapNormalizer.Normalize(1000, 1000, 0, 1000, 0, 1000);
        (double U, double V)? center = MinimapNormalizer.Normalize(500, 500, 0, 1000, 0, 1000);

        Assert.IsNotNull(northWest);
        Assert.AreEqual(0.0, northWest!.Value.U, 1e-12);
        Assert.AreEqual(0.0, northWest.Value.V, 1e-12);
        Assert.AreEqual(1.0, southEast!.Value.U, 1e-12);
        Assert.AreEqual(1.0, southEast.Value.V, 1e-12);
        Assert.AreEqual(0.5, center!.Value.U, 1e-12);
        Assert.AreEqual(0.5, center.Value.V, 1e-12);
    }

    [TestMethod]
    public void Normalize_OffsetsBoundary()
    {
        // Boundary shifted off the origin: -100..100 maps x=0 to 0.5.
        (double U, double V)? result = MinimapNormalizer.Normalize(0, -100, -100, 100, -100, 100);
        Assert.IsNotNull(result);
        Assert.AreEqual(0.5, result!.Value.U, 1e-12);
        Assert.AreEqual(0.0, result.Value.V, 1e-12);
    }

    [TestMethod]
    public void Normalize_DoesNotClampOutsideBoundary()
    {
        // Out-of-boundary points pass through unclamped — callers clamp at
        // draw time (the HUD and the schematic both do).
        (double U, double V)? result = MinimapNormalizer.Normalize(-500, 1500, 0, 1000, 0, 1000);
        Assert.IsNotNull(result);
        Assert.AreEqual(-0.5, result!.Value.U, 1e-12);
        Assert.AreEqual(1.5, result.Value.V, 1e-12);
    }

    [TestMethod]
    public void Normalize_DegenerateBoundary_ReturnsNull()
    {
        Assert.IsNull(MinimapNormalizer.Normalize(10, 10, 0, 0, 0, 1000));
        Assert.IsNull(MinimapNormalizer.Normalize(10, 10, 0, 1000, 0, 0));
        Assert.IsNull(MinimapNormalizer.Normalize(10, 10, 0, 0, 0, 0));
    }
}
