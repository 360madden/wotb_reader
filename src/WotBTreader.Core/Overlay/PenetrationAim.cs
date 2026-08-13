namespace WotBTreader.Core.Overlay;

/// <summary>
/// Nominal armor thickness per hull face, in millimeters — the first-order
/// stand-in for the install's per-group armor XML until the plate-slope
/// <c>.model</c> collision geometry is probed (PN-1's open sub-problem). The
/// caller supplies front/side/rear from the vehicle definition's armor
/// groups; side is symmetric (left == right).
/// </summary>
public readonly record struct TankArmor(
    double FrontMm,
    double SideMm,
    double RearMm);

/// <summary>
/// PN-3 aim resolution: turn the replay camera pose into an aim ray, pick the
/// tank the ray strikes first (a vertical-cylinder approximation), and score
/// the penetration chance against that tank's facing face — the computation
/// the overlay's pen badge consumes. Pure and fail-closed; the WPF badge
/// rendering and frame-model plumbing sit on top of this.
///
/// Honest limits (recorded, never hidden):
///  - The hull is a vertical cylinder for ray-picking and a four-face box
///    (front/back/left/right, facing-derived normals) for armor — nominal
///    thickness, no plate slope. True slope/normal comes from the
///    <c>.model</c> collision geometry (PN-1 open sub-problem).
///  - The plate plane passes through the tank CENTER, so the hit distance
///    (and thus the range drop) is approximate.
///  - Aim-line == camera forward (CAM-013: the replay camera drives the
///    viewed tank's turret). Live mode needs T1 turret discovery.
/// </summary>
public static class PenetrationAim
{
    /// <summary>Default hull radius (m) for the cylinder pick test.</summary>
    public const double DefaultTankRadiusMeters = 1.5;

    /// <summary>
    /// Builds the world-space aim ray from a camera pose, using the same
    /// forward convention as <see cref="WorldToScreen"/> (yaw 0 → +Z; forward
    /// = (sin yaw·cos pitch, sin pitch, cos yaw·cos pitch)). Fail-closed:
    /// null when the camera carries no rotation or the pose is non-finite.
    /// </summary>
    public static AimRay? BuildAimRay(OverlayCamera camera)
    {
        if (camera.YawRadians is null || camera.PitchRadians is null
            || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y)
            || !double.IsFinite(camera.Z)
            || !double.IsFinite(camera.YawRadians.Value)
            || !double.IsFinite(camera.PitchRadians.Value))
        {
            return null;
        }

        double yaw = camera.YawRadians.Value;
        double pitch = camera.PitchRadians.Value;
        double fx = Math.Sin(yaw) * Math.Cos(pitch);
        double fy = Math.Sin(pitch);
        double fz = Math.Cos(yaw) * Math.Cos(pitch);
        return new AimRay(camera.X, camera.Y, camera.Z, fx, fy, fz);
    }

    /// <summary>
    /// Returns the entity id of the nearest tank (by ray distance) whose
    /// vertical cylinder the ray strikes, or null when no tank is aimed at.
    /// Only the caller's candidate list is considered (callers filter
    /// alive/enemy/team). Fail-closed on a degenerate ray (no horizontal
    /// travel) or invalid tank coordinates.
    /// </summary>
    public static long? AimedTankId(
        AimRay ray,
        IReadOnlyList<OverlayTankState> tanks,
        double tankRadiusMeters = DefaultTankRadiusMeters)
    {
        if (!double.IsFinite(tankRadiusMeters) || tankRadiusMeters <= 0)
        {
            return null;
        }

        double dx = ray.DirectionX;
        double dz = ray.DirectionZ;
        double a = (dx * dx) + (dz * dz);
        if (a <= 1e-12)
        {
            // No horizontal travel: the ray cannot pick a tank on the ground.
            return null;
        }

        double bestT = double.PositiveInfinity;
        long? bestId = null;
        foreach (OverlayTankState tank in tanks)
        {
            if (!double.IsFinite(tank.X) || !double.IsFinite(tank.Z))
            {
                continue;
            }

            double ox = ray.OriginX - tank.X;
            double oz = ray.OriginZ - tank.Z;
            double b = 2.0 * ((dx * ox) + (dz * oz));
            double c = (ox * ox) + (oz * oz) - (tankRadiusMeters * tankRadiusMeters);
            double disc = (b * b) - (4.0 * a * c);
            if (disc < 0)
            {
                continue;
            }

            double root = Math.Sqrt(disc);
            double t0 = (-b - root) / (2.0 * a);
            double t1 = (-b + root) / (2.0 * a);
            double entry = t0 >= 0 ? t0 : t1;
            if (entry < 0 || entry >= bestT)
            {
                continue;
            }

            bestT = entry;
            bestId = tank.EntityId;
        }

        return bestId;
    }

    /// <summary>
    /// Scores the penetration chance of an aim ray against one tank, using a
    /// four-face box: the face whose outward normal most opposes the ray is
    /// the struck face, and its nominal thickness (front/side/rear) is the
    /// plate. Returns <see cref="PenetrationBand.Unknown"/> on any invalid
    /// input (no hull facing, non-finite coordinates, or a ray that does not
    /// approach any face).
    /// </summary>
    public static PenetrationVerdict EvaluateAgainst(
        AimRay ray,
        OverlayTankState tank,
        TankArmor armor,
        ShellSpec shell,
        double margin = 0.1)
    {
        if (tank.YawRadians is null
            || !double.IsFinite(tank.X) || !double.IsFinite(tank.Y)
            || !double.IsFinite(tank.Z)
            || !double.IsFinite(tank.YawRadians.Value))
        {
            return new PenetrationVerdict(
                PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
        }

        double yaw = tank.YawRadians.Value;
        // Facing in the XZ plane (yaw 0 → +Z), matching the packet convention.
        double fx = Math.Sin(yaw);
        double fz = Math.Cos(yaw);

        // Four face normals: front, back, and the two symmetric sides.
        (double Nx, double Nz)[] faces =
        [
            (fx, fz),                 // front
            (-fx, -fz),               // back
            (fz, -fx),                // side A
            (-fz, fx),                // side B
        ];

        double best = double.NegativeInfinity;
        int bestFace = -1;
        for (int i = 0; i < faces.Length; i++)
        {
            double approach = -((ray.DirectionX * faces[i].Nx) + (ray.DirectionZ * faces[i].Nz));
            if (approach > best)
            {
                best = approach;
                bestFace = i;
            }
        }

        if (bestFace < 0 || best <= 0)
        {
            // The ray does not approach any face (degenerate direction).
            return new PenetrationVerdict(
                PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
        }

        double thickness = bestFace switch
        {
            0 => armor.FrontMm,
            1 => armor.RearMm,
            _ => armor.SideMm,
        };

        ArmorPlate plate = new(
            thickness,
            faces[bestFace].Nx, 0, faces[bestFace].Nz,
            tank.X, tank.Y, tank.Z);
        return ArmorPenetration.Evaluate(ray, plate, shell, margin);
    }
}
