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
