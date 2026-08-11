namespace WotBTreader.ApiContracts;

/// <summary>
/// The single world-to-minimap normalization contract shared by the WPF HUD
/// (<c>MinimapMath</c>) and the offline CLI preview rasterizer
/// (<c>FrameRasterizer</c>): maps a replay-raw world (x, z) into normalized
/// 0..1 panel coordinates against a map boundary, where u = 0 is the western
/// (min-x) boundary, v = 0 the northern (min-z) boundary, and both grow
/// eastward/southward. Values outside the boundary are NOT clamped here —
/// callers clamp at draw time; callers with a fixed-size panel do so with
/// their own inset. Returns null when the boundary has no extent (fail
/// closed — no garbage dot). Lives in ApiContracts because it is the shared
/// contract between the host-side preview tooling and the loopback overlay
/// client, which may only reference this project.
/// </summary>
public static class MinimapNormalizer
{
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
