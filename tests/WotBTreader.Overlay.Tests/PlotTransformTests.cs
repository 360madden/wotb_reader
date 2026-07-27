using WotBTreader.Overlay.ViewModels;
using WotBTreader.Overlay.Views;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class PlotTransformTests
{
    private const double Tolerance = 1e-9;

    [TestMethod]
    public void Fit_EmptyPoints_ReturnsEmpty()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit([], 200, 100, 10);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Fit_SinglePoint_CentersOnCanvas()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(42.5, -7.25, 3)],
            200, 100, 10);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(100, result[0].X, Tolerance);
        Assert.AreEqual(50, result[0].Y, Tolerance);
        Assert.AreEqual(3, result[0].TeamNumber);
    }

    [TestMethod]
    public void Fit_TwoPoints_MapToOppositePaddingCorners()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(0, 0, 1), new PlotPoint(10, 20, 2)],
            210, 120, 10);

        Assert.AreEqual(2, result.Count);

        Assert.AreEqual(10, result[0].X, Tolerance);
        Assert.AreEqual(200, result[1].X, Tolerance);

        // Y orientation on screen is unspecified; both points must land on opposite padding edges.
        AssertIsPaddingEdge(result[0].Y, 10, 120);
        AssertIsPaddingEdge(result[1].Y, 10, 120);
        Assert.AreEqual(120, result[0].Y + result[1].Y, 2 * Tolerance);

        Assert.AreEqual(1, result[0].TeamNumber);
        Assert.AreEqual(2, result[1].TeamNumber);
    }

    [TestMethod]
    public void Fit_ZeroYExtent_CentersY()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(0, 5, 1), new PlotPoint(10, 5, 2)],
            210, 120, 10);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(10, result[0].X, Tolerance);
        Assert.AreEqual(200, result[1].X, Tolerance);
        Assert.AreEqual(60, result[0].Y, Tolerance);
        Assert.AreEqual(60, result[1].Y, Tolerance);
    }

    [TestMethod]
    public void Fit_PaddingExceedsHalfExtent_ClampsPaddingToZero()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(0, 0, 1), new PlotPoint(10, 20, 2)],
            10, 10, 20);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(0, result[0].X, Tolerance);
        Assert.AreEqual(10, result[1].X, Tolerance);
        AssertIsPaddingEdge(result[0].Y, 0, 10);
        AssertIsPaddingEdge(result[1].Y, 0, 10);
        Assert.AreEqual(10, result[0].Y + result[1].Y, 2 * Tolerance);

        foreach ((double X, double Y, int TeamNumber) point in result)
        {
            AssertInRange(point.X, 0, 10);
            AssertInRange(point.Y, 0, 10);
        }
    }

    [TestMethod]
    public void Fit_PreservesTeamNumberPerPoint()
    {
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(1, 1, 7), new PlotPoint(2, 3, 8), new PlotPoint(3, 2, 9)],
            100, 80, 5);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual(7, result[0].TeamNumber);
        Assert.AreEqual(8, result[1].TeamNumber);
        Assert.AreEqual(9, result[2].TeamNumber);
    }

    [TestMethod]
    public void Fit_WithWorldBounds_UsesFixedBoundsNotPerSessionExtents()
    {
        // Two points at (100,100) and (200,200), but world bounds span (0,0)-(1000,1000).
        // Without world bounds, these points would map to opposite corners.
        // With world bounds, they map to a small region covering 10% of the canvas.
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(100, 100, 1), new PlotPoint(200, 200, 2)],
            200, 100, 10,
            worldMinX: 0, worldMaxX: 1000, worldMinZ: 0, worldMaxZ: 1000);

        Assert.AreEqual(2, result.Count);

        // Point at (100,100) → 10% into usable = X:10+0.1*180=28, Y:10+0.1*80=18
        Assert.AreEqual(28, result[0].X, Tolerance);
        Assert.AreEqual(18, result[0].Y, Tolerance);

        // Point at (200,200) → 20% into usable = X:10+0.2*180=46, Y:10+0.2*80=26
        Assert.AreEqual(46, result[1].X, Tolerance);
        Assert.AreEqual(26, result[1].Y, Tolerance);

        Assert.AreEqual(1, result[0].TeamNumber);
        Assert.AreEqual(2, result[1].TeamNumber);
    }

    [TestMethod]
    public void Fit_ZeroExtentWorldBounds_FallsBackToPerSessionExtents()
    {
        // World bounds with zero extent are treated as invalid — falls back to per-session.
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(0, 0, 1), new PlotPoint(10, 20, 2)],
            210, 120, 10,
            worldMinX: 100, worldMaxX: 100, worldMinZ: 0, worldMaxZ: 0);

        Assert.AreEqual(2, result.Count);
        // Should fall back to per-session extents (0-10, 0-20)
        Assert.AreEqual(10, result[0].X, Tolerance);
        Assert.AreEqual(200, result[1].X, Tolerance);
    }

    [TestMethod]
    public void Fit_InvertedWorldBounds_FallsBackToPerSessionExtents()
    {
        // World bounds where max < min are invalid — falls back.
        IReadOnlyList<(double X, double Y, int TeamNumber)> result = PlotTransform.Fit(
            [new PlotPoint(0, 0, 1), new PlotPoint(10, 20, 2)],
            210, 120, 10,
            worldMinX: 500, worldMaxX: 100, worldMinZ: 0, worldMaxZ: 1000);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(10, result[0].X, Tolerance);
        Assert.AreEqual(200, result[1].X, Tolerance);
    }

    private static void AssertIsPaddingEdge(double value, double padding, double extent)
    {
        bool atLowEdge = Math.Abs(value - padding) <= Tolerance;
        bool atHighEdge = Math.Abs(value - (extent - padding)) <= Tolerance;
        Assert.IsTrue(atLowEdge != atHighEdge, "Value should sit exactly on one padding edge.");
    }

    private static void AssertInRange(double value, double min, double max)
    {
        Assert.IsTrue(value >= min && value <= max, "Value should be inside the canvas extent.");
    }
}
