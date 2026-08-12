namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// Pure math for the own-tank edge marker (no WPF rendering, unit-testable).
/// </summary>
public static class OwnMarkerMath
{
    /// <summary>
    /// Distance from the viewport edge the marker sits at, so the chevron
    /// stays fully visible.
    /// </summary>
    public const double Margin = 28.0;

    /// <summary>
    /// Clamps a projected viewport pixel to the rect inset by
    /// <see cref="Margin"/>. Returns null when the rect is degenerate
    /// (width/height below twice the margin — fail-closed, never draw a
    /// marker on a collapsed viewport).
    /// </summary>
    public static (double X, double Y)? ClampToViewport(
        double x,
        double y,
        double viewportWidth,
        double viewportHeight,
        double margin = Margin)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
            || viewportWidth <= 2 * margin || viewportHeight <= 2 * margin)
        {
            return null;
        }

        return (
            Math.Clamp(x, margin, viewportWidth - margin),
            Math.Clamp(y, margin, viewportHeight - margin));
    }

    /// <summary>
    /// Direction (radians) from the clamped marker position toward the tank's
    /// actual projection — the chevron apex points this way. 0 = +X,
    /// +π/2 = +Y (viewport pixels, top-left origin).
    /// </summary>
    public static double AngleToward(
        double actualX,
        double actualY,
        double clampedX,
        double clampedY) =>
        Math.Atan2(actualY - clampedY, actualX - clampedX);
}
