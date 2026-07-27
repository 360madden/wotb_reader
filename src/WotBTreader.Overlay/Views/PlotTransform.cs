using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Views;

/// <summary>Maps plot points from replay coordinates into canvas coordinates.</summary>
public static class PlotTransform
{
    public static IReadOnlyList<(double X, double Y, int TeamNumber)> Fit(
        IReadOnlyList<PlotPoint> points,
        double width,
        double height,
        double padding)
    {
        if (points.Count == 0)
        {
            return Array.Empty<(double X, double Y, int TeamNumber)>();
        }

        if (width <= (2 * padding) || height <= (2 * padding))
        {
            padding = 0;
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        foreach (PlotPoint point in points)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        double extentX = maxX - minX;
        double extentY = maxY - minY;
        double usableWidth = width - (2 * padding);
        double usableHeight = height - (2 * padding);

        var fitted = new (double X, double Y, int TeamNumber)[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            PlotPoint point = points[i];
            double x = extentX > 0
                ? padding + ((point.X - minX) / extentX * usableWidth)
                : width / 2;
            double y = extentY > 0
                ? padding + ((point.Y - minY) / extentY * usableHeight)
                : height / 2;
            fitted[i] = (x, y, point.TeamNumber);
        }

        return fitted;
    }
}
