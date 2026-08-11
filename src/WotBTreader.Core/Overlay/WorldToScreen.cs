namespace WotBTreader.Core.Overlay;

/// <summary>
/// A projected point: pixel coordinates (0..viewport width/height, origin at
/// the TOP-LEFT, y growing downward) plus the camera-space depth used for
/// painter's-algorithm sorting. Only points in front of the camera project.
/// </summary>
public readonly record struct ScreenPoint(double X, double Y, double Depth)
{
    /// <summary>True when the point lies inside the viewport rectangle.</summary>
    public bool IsInsideViewport(double viewportWidth, double viewportHeight) =>
        X >= 0 && X <= viewportWidth && Y >= 0 && Y <= viewportHeight;
}

/// <summary>
/// Pure world-to-screen projection shared by the replay overlay and (later)
/// a live overlay: given the camera pose and a vertical field of view, a
/// world point becomes a pixel on the viewport.
///
/// Conventions (matching the decoded telemetry):
///  - World axes: X/Z horizontal, Y up. The packet yaw is the facing with
///    yaw ≈ atan2(dx, dz) (yaw 0 faces +Z, +pi/2 faces +X), pitch is the
///    vertical facing in the packet's flipped-sign convention (positive
///    pitch noses up in the game render — the sign is irrelevant here as
///    long as yaw/pitch/roll were persisted from the same packet).
///  - View: right-handed camera space with +X right, +Y up, +Z forward
///    (depth); the camera looks along its facing.
///  - Perspective: pinhole with focal = (height / 2) / tan(fov / 2), where
///    fov is the VERTICAL field of view in radians. Screen origin is the
///    top-left, Y flips (camera +Y is up, screen +Y is down).
///
/// Fail-closed: points at or behind the camera (depth ≤ 0) return null —
/// nothing is ever projected behind the viewer.
/// </summary>
public static class WorldToScreen
{
    /// <summary>Projects one world point with an explicit camera pose.</summary>
    /// <param name="eyeX">Camera world X.</param>
    /// <param name="eyeY">Camera world Y.</param>
    /// <param name="eyeZ">Camera world Z.</param>
    /// <param name="yaw">Camera facing yaw in radians (0 = +Z).</param>
    /// <param name="pitch">Camera pitch in radians (packet convention).</param>
    /// <param name="verticalFovRadians">Vertical field of view (&gt; 0, &lt; π).</param>
    /// <param name="viewportWidth">Viewport width in pixels (&gt; 0).</param>
    /// <param name="viewportHeight">Viewport height in pixels (&gt; 0).</param>
    /// <param name="worldX">Point world X.</param>
    /// <param name="worldY">Point world Y.</param>
    /// <param name="worldZ">Point world Z.</param>
    /// <returns>The projected pixel + depth, or null when the point is at or
    /// behind the camera or the pose/viewport is invalid.</returns>
    public static ScreenPoint? Project(
        double eyeX,
        double eyeY,
        double eyeZ,
        double yaw,
        double pitch,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight,
        double worldX,
        double worldY,
        double worldZ)
    {
        if (!double.IsFinite(verticalFovRadians) || verticalFovRadians <= 0
            || verticalFovRadians >= Math.PI
            || !double.IsFinite(viewportWidth) || viewportWidth <= 0
            || !double.IsFinite(viewportHeight) || viewportHeight <= 0
            || !double.IsFinite(yaw) || !double.IsFinite(pitch))
        {
            // A non-finite rotation would carry NaN through the basis into
            // the pixel coordinates, which the JSON serializers reject (and
            // would 500 the frame endpoint). Fail closed: no projection.
            return null;
        }

        // View basis from the pose. Forward follows the packet convention
        // (yaw 0 -> +Z): f = (sin yaw * cos pitch, sin pitch, cos yaw * cos pitch).
        double cosYaw = Math.Cos(yaw);
        double sinYaw = Math.Sin(yaw);
        double cosPitch = Math.Cos(pitch);
        double sinPitch = Math.Sin(pitch);
        double fx = sinYaw * cosPitch;
        double fy = sinPitch;
        double fz = cosYaw * cosPitch;

        // Right = normalize(cross(forward, worldUp)); with up = (0,1,0) this
        // is (cos yaw, 0, -sin yaw) and stays unit-length (world +X is on the
        // camera's right when facing +Z, matching the packet's yaw = atan2(dx,dz)).
        double rx = cosYaw;
        double ry = 0.0;
        double rz = -sinYaw;

        // Up = cross(forward, right): tilts FORWARD when the camera pitches up,
        // so the horizon drops below center — a same-height point lands below
        // the view center, the way a pitched-up camera actually renders.
        double ux = fy * rz - fz * ry;
        double uy = fz * rx - fx * rz;
        double uz = fx * ry - fy * rx;

        double dx = worldX - eyeX;
        double dy = worldY - eyeY;
        double dz = worldZ - eyeZ;

        double camX = dx * rx + dy * ry + dz * rz;
        double camY = dx * ux + dy * uy + dz * uz;
        double depth = dx * fx + dy * fy + dz * fz;
        if (depth <= 0)
        {
            return null;
        }

        double focal = (viewportHeight / 2.0) / Math.Tan(verticalFovRadians / 2.0);
        double screenX = viewportWidth / 2.0 + (camX / depth) * focal;
        double screenY = viewportHeight / 2.0 - (camY / depth) * focal;
        return new ScreenPoint(screenX, screenY, depth);
    }

    /// <summary>
    /// Projects a world point using an <see cref="OverlayCamera"/>. Fail-closed
    /// when the camera carries no rotation evidence (yaw/pitch null — samples
    /// decoded before migration 5) or the pose is otherwise invalid.
    /// </summary>
    public static ScreenPoint? Project(
        OverlayCamera camera,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight,
        double worldX,
        double worldY,
        double worldZ)
    {
        if (camera.YawRadians is null || camera.PitchRadians is null)
        {
            return null;
        }

        return Project(
            camera.X,
            camera.Y,
            camera.Z,
            camera.YawRadians.Value,
            camera.PitchRadians.Value,
            verticalFovRadians,
            viewportWidth,
            viewportHeight,
            worldX,
            worldY,
            worldZ);
    }

    /// <summary>
    /// Screen-space heading of an entity's facing, in degrees, clockwise from
    /// screen-up (0 = pointing away from the viewer, 90 = screen-right,
    /// 180 = toward the viewer). Projects the entity position and a probe
    /// point a short distance along its facing, so the direction is exactly
    /// the one the perspective viewport renders (off-center tanks point
    /// toward/away from the vanishing point, not a screen-constant vector).
    ///
    /// Fail-closed: null when the camera carries no rotation evidence, the
    /// entity or its probe is at/behind the camera, or the facing projects to
    /// a single pixel (facing exactly along the view axis).
    /// </summary>
    /// <param name="camera">The camera pose.</param>
    /// <param name="verticalFovRadians">Vertical field of view (&gt; 0, &lt; π).</param>
    /// <param name="viewportWidth">Viewport width in pixels (&gt; 0).</param>
    /// <param name="viewportHeight">Viewport height in pixels (&gt; 0).</param>
    /// <param name="worldX">Entity world X.</param>
    /// <param name="worldY">Entity world Y.</param>
    /// <param name="worldZ">Entity world Z.</param>
    /// <param name="yawRadians">Entity hull facing in radians (packet
    /// convention: 0 faces +Z, +π/2 faces +X).</param>
    /// <param name="probeDistance">World-space distance along the facing used
    /// for the second projection; small enough to stay local to the entity,
    /// large enough to stay numerically stable at range.</param>
    public static double? ScreenHeadingDegrees(
        OverlayCamera camera,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight,
        double worldX,
        double worldY,
        double worldZ,
        double yawRadians,
        double probeDistance = 8.0)
    {
        if (camera.YawRadians is null || camera.PitchRadians is null
            || !double.IsFinite(yawRadians) || probeDistance <= 0)
        {
            return null;
        }

        ScreenPoint? here = Project(
            camera,
            verticalFovRadians,
            viewportWidth,
            viewportHeight,
            worldX,
            worldY,
            worldZ);
        if (here is null)
        {
            return null;
        }

        // Facing convention: yaw 0 faces +Z, +pi/2 faces +X (the packet
        // convention WorldToScreen.Project already uses). The probe sits on
        // the horizontal plane so a tank's hull heading is what rotates.
        double probeX = worldX + Math.Sin(yawRadians) * probeDistance;
        double probeZ = worldZ + Math.Cos(yawRadians) * probeDistance;
        ScreenPoint? ahead = Project(
            camera,
            verticalFovRadians,
            viewportWidth,
            viewportHeight,
            probeX,
            worldY,
            probeZ);
        if (ahead is null)
        {
            return null;
        }

        double dx = ahead.Value.X - here.Value.X;
        double dy = ahead.Value.Y - here.Value.Y;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
        {
            // Facing exactly along the view axis: the nose is a single
            // pixel. The heading is genuinely unobservable — no arrow.
            return null;
        }

        // Screen Y grows DOWN, so the clockwise-from-up angle is
        // atan2(screen-right component, screen-up component).
        return Math.Atan2(dx, -dy) * 180.0 / Math.PI;
    }
}
