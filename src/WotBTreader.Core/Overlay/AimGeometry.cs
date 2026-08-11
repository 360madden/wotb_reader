namespace WotBTreader.Core.Overlay;

/// <summary>
/// Hull-aim geometry for the overlay threat layer. Uses the packet yaw
/// convention proven on the type-10 stream (yaw 0 = facing +Z;
/// forward = (sin yaw, cos yaw) in the xz-plane; heading to a point =
/// atan2(dx, dz)) — the same convention as <see cref="WorldToScreen"/>.
///
/// HONEST LIMIT (type-7 survey, 2026-08-11): the replay carries NO turret
/// angle and no lock/target state, so "aims at" here is HULL-only. A hull
/// aimed at you is not necessarily turret-aimed at you (the turret may be
/// traversed elsewhere), but a hull pointing away means the turret cannot
/// be aimed at you within the hull arc — the hull check is the necessary
/// condition and the best the replay data supports. True lock/target state
/// is a live-memory discovery target, not a replay decode.
/// </summary>
public static class AimGeometry
{
    /// <summary>
    /// Signed angular error in [-π, π] between the hull facing
    /// (<paramref name="yawRadians"/>) and the heading from (fromX, fromZ)
    /// toward (toX, toZ). Zero = the target is dead ahead of the hull.
    /// Sign follows error = yaw − heading with +X right / +Z forward: a
    /// target to the RIGHT of the facing (heading +π/2 at yaw 0) gives
    /// error −π/2; to the LEFT gives positive. The sign itself is not part
    /// of the threat contract; callers use the absolute value.
    /// </summary>
    public static double HullAimErrorRadians(
        double yawRadians,
        double fromX,
        double fromZ,
        double toX,
        double toZ)
    {
        double heading = Math.Atan2(toX - fromX, toZ - fromZ);
        double error = yawRadians - heading;
        if (error > Math.PI)
        {
            error -= 2 * Math.PI;
        }
        else if (error < -Math.PI)
        {
            error += 2 * Math.PI;
        }

        return error;
    }

    /// <summary>
    /// True when the target lies within the gun arc ahead of the hull:
    /// |<see cref="HullAimErrorRadians"/>| ≤ toleranceRadians. Fail-closed:
    /// non-finite inputs, a zero/absent direction to the target (from == to),
    /// or an out-of-range tolerance return false — a NaN never turns into a
    /// "targeted" flag for the overlay.
    /// </summary>
    public static bool HullAimsAt(
        double yawRadians,
        double fromX,
        double fromZ,
        double toX,
        double toZ,
        double toleranceRadians)
    {
        if (!double.IsFinite(yawRadians) ||
            !double.IsFinite(fromX) || !double.IsFinite(fromZ) ||
            !double.IsFinite(toX) || !double.IsFinite(toZ) ||
            !double.IsFinite(toleranceRadians) ||
            toleranceRadians <= 0 || toleranceRadians > Math.PI)
        {
            return false;
        }

        double dx = toX - fromX;
        double dz = toZ - fromZ;
        if (dx == 0.0 && dz == 0.0)
        {
            // Target coincides with the shooter: the aim is undefined, so a
            // "targeted" flag must not fire.
            return false;
        }

        return Math.Abs(HullAimErrorRadians(yawRadians, fromX, fromZ, toX, toZ))
            <= toleranceRadians;
    }
}
