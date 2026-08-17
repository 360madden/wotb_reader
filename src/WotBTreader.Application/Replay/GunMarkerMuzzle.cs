namespace WotBTreader.Application.Replay;

/// <summary>
/// Reconstructs the GetGunMarkerPosition start pose from the published
/// rotator+0x50 hit (pos3+dir3+scalar). Hash-bound statics
/// (FUN_01ec12b0): the published position is the ray-march hit, the
/// published direction is the normalized hit-minus-start, and
/// scalar = 2 * |hit-start| * param3. This is not ExactGunRay until a
/// live in-band origin repeats.
/// </summary>
public static class GunMarkerMuzzle
{
    private const double Degenerate = 1e-6;

    /// <summary>
    /// Start = hit - dir * (scalar / (2 * param3)). Returns false when
    /// inputs are non-finite, param3 is not positive, or the implied
    /// distance is not finite and non-negative.
    /// </summary>
    public static bool TryReconstructStart(
        double hitX,
        double hitY,
        double hitZ,
        double dirX,
        double dirY,
        double dirZ,
        double scalar,
        double param3,
        out double startX,
        out double startY,
        out double startZ)
    {
        startX = 0;
        startY = 0;
        startZ = 0;
        if (!IsFinite(hitX) || !IsFinite(hitY) || !IsFinite(hitZ)
            || !IsFinite(dirX) || !IsFinite(dirY) || !IsFinite(dirZ)
            || !IsFinite(scalar) || !IsFinite(param3)
            || param3 <= Degenerate)
        {
            return false;
        }

        double distance = scalar / (2.0 * param3);
        if (!IsFinite(distance) || distance < 0)
        {
            return false;
        }

        startX = hitX - (dirX * distance);
        startY = hitY - (dirY * distance);
        startZ = hitZ - (dirZ * distance);
        return IsFinite(startX) && IsFinite(startY) && IsFinite(startZ);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
