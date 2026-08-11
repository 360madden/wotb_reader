namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// Maps a replay-raw world position into normalized 0..1 minimap panel
/// coordinates against the session's map boundary. Pure and unit-testable —
/// the HUD scales the normalized point onto its fixed-size minimap panel.
/// </summary>
public static class MinimapMath
{
    /// <summary>
    /// Normalizes a world (x, z) into (u, v) in [0, 1], where u = 0 is the
    /// western boundary and v = 0 the northern (min-z) boundary. Returns
    /// null when the boundary is degenerate (no extent), so callers fail
    /// closed instead of drawing a garbage dot.
    /// </summary>
    public static (double U, double V)? Normalize(
        double worldX,
        double worldZ,
        double minX,
        double maxX,
        double minZ,
        double maxZ)
    {
        double extentX = maxX - minX;
        double extentZ = maxZ - minZ;
        if (extentX <= 0 || extentZ <= 0)
        {
            return null;
        }

        return ((worldX - minX) / extentX, (worldZ - minZ) / extentZ);
    }
}
