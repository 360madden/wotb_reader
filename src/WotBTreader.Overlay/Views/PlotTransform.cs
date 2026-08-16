using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Views;

/// <summary>Maps plot points from replay coordinates into canvas coordinates.</summary>
public static class PlotTransform
{
    /// <summary>
    /// Fits points to canvas using per-session extents (fallback behaviour).
    /// Use the overload with world bounds for map-stable minimap projection.
    /// </summary>
    public static IReadOnlyList<(double X, double Y, int TeamNumber)> Fit(
        IReadOnlyList<PlotPoint> points,
        double width,
        double height,
        double padding)
    {
        return Fit(points, width, height, padding, null, null, null, null);
    }

    /// <summary>
    /// Fits points to canvas using fixed world bounds when all four are
    /// provided, falling back to per-session extents otherwise. Fixed bounds
    /// ensure the position plot overlays the game's minimap consistently
    /// regardless of which area of the map a particular battle covered.
    /// </summary>
    public static IReadOnlyList<(double X, double Y, int TeamNumber)> Fit(
        IReadOnlyList<PlotPoint> points,
        double width,
        double height,
        double padding,
        double? worldMinX,
        double? worldMaxX,
        double? worldMinZ,
        double? worldMaxZ)
    {
        if (points.Count == 0)
        {
            return Array.Empty<(double X, double Y, int TeamNumber)>();
        }

        if (width <= (2 * padding) || height <= (2 * padding))
        {
            padding = 0;
        }

        double minX, maxX, minZ, maxZ;
        bool useWorld = worldMinX.HasValue && worldMaxX.HasValue
            && worldMinZ.HasValue && worldMaxZ.HasValue
            && double.IsFinite(worldMinX.Value) && double.IsFinite(worldMaxX.Value)
            && double.IsFinite(worldMinZ.Value) && double.IsFinite(worldMaxZ.Value)
            && worldMaxX.Value > worldMinX.Value
            && worldMaxZ.Value > worldMinZ.Value;

        if (useWorld)
        {
            minX = worldMinX!.Value;
            maxX = worldMaxX!.Value;
            minZ = worldMinZ!.Value;
            maxZ = worldMaxZ!.Value;
        }
        else
        {
            minX = double.MaxValue;
            maxX = double.MinValue;
            minZ = double.MaxValue;
            maxZ = double.MinValue;
            foreach (PlotPoint point in points)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Y);
                maxZ = Math.Max(maxZ, point.Y);
            }
        }

        double extentX = maxX - minX;
        double extentZ = maxZ - minZ;
        double usableWidth = width - (2 * padding);
        double usableHeight = height - (2 * padding);

        var fitted = new (double X, double Y, int TeamNumber)[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            PlotPoint point = points[i];
            double x = extentX > 0
                ? padding + ((point.X - minX) / extentX * usableWidth)
                : width / 2;
            double y = extentZ > 0
                ? padding + ((point.Y - minZ) / extentZ * usableHeight)
                : height / 2;
            fitted[i] = (x, y, point.TeamNumber);
        }

        return fitted;
    }
}
