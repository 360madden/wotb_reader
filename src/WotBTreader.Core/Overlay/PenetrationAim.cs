namespace WotBTreader.Core.Overlay;

/// <summary>
/// Nominal armor thickness per hull face, in millimeters — the first-order
/// stand-in for the install's per-group armor XML until the plate-slope
/// <c>.model</c> collision geometry is probed (PN-1's open sub-problem). The
/// caller supplies front/side/rear from the vehicle definition's armor
/// groups; side is symmetric (left == right). A face with thickness ≤ 0 is
/// UNKNOWN armor (not zero protection): <see cref="PenetrationAim"/> rejects
/// it as <see cref="PenetrationBand.Unknown"/> rather than fabricating a
/// will-penetrate verdict, so callers may pass 0 for a face whose nominal
/// thickness is not derivable (the install's armor XML declares the FRONT
/// via <c>primaryArmor</c> but not the side/rear face mapping).
/// </summary>
public readonly record struct TankArmor(
    double FrontMm,
    double SideMm,
    double RearMm);

/// <summary>
/// Which hull face an aim ray strikes, derived from the tank's facing and
/// the ray's direction (the four-face box model).
/// </summary>
public enum StruckFace
{
    /// <summary>No facing evidence or a degenerate ray — the struck face is
    /// not derivable.</summary>
    Unknown = 0,

    /// <summary>The tank's forward-facing plate.</summary>
    Front = 1,

    /// <summary>The tank's rear plate.</summary>
    Back = 2,

    /// <summary>Either side plate (symmetric).</summary>
    Side = 3,
}

/// <summary>
/// The penetration indicator's renderable result: which tank is aimed at,
/// which face the aim ray strikes, and the banded verdict with its
/// diagnostics. The verdict's <see cref="PenetrationVerdict.Band"/> is
/// <see cref="PenetrationBand.Unknown"/> when the face's armor is unknown or
/// the geometry is degenerate — the HUD
/// must not paint a green/red badge on an unknown.
/// </summary>
public readonly record struct PenetrationBadge(
    long AimedEntityId,
    StruckFace Face,
    PenetrationVerdict Verdict);

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
    /// Determines which hull face an aim ray strikes: the face whose outward
    /// normal most opposes the ray direction (the four-face box model).
    /// Returns <see cref="StruckFace.Unknown"/> when the tank has no facing
    /// evidence, its coordinates are non-finite, or the ray approaches no
    /// face (degenerate horizontal direction).
    /// </summary>
    public static StruckFace SelectStruckFace(AimRay ray, OverlayTankState tank)
    {
        (double Nx, double Nz)[] faces = FaceNormals(tank);
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
            return StruckFace.Unknown;
        }

        return bestFace switch
        {
            0 => StruckFace.Front,
            1 => StruckFace.Back,
            _ => StruckFace.Side,
        };
    }

    /// <summary>
    /// Scores the penetration chance of an aim ray against one tank, using a
    /// four-face box: the face whose outward normal most opposes the ray is
    /// the struck face, and its nominal thickness (front/side/rear) is the
    /// plate. Returns <see cref="PenetrationBand.Unknown"/> on any invalid
    /// input (no hull facing, non-finite coordinates, a ray that does not
    /// approach any face, or a struck face whose nominal thickness is
    /// unknown/zero).
    /// </summary>
    public static PenetrationVerdict EvaluateAgainst(
        AimRay ray,
        OverlayTankState tank,
        TankArmor armor,
        ShellSpec shell,
        double margin = 0.1)
    {
        (double Nx, double Nz)[] faces = FaceNormals(tank);
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
            // No facing evidence or the ray does not approach any face.
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

    /// <summary>
    /// Scores the penetration chance against one tank using the install
    /// collision MESH (PN-5): the world aim ray is transformed into the tank's
    /// local collision space, raycast against the triangle surface, and the
    /// struck triangle's OUTWARD surface normal drives the incidence angle —
    /// the true plate normal the four-face box approximated. The nominal face
    /// thickness (front/side/rear) is still selected from the struck face, so
    /// the effective-armor ANGLE is now geometric even while the thickness
    /// stays nominal. <paramref name="face"/> receives the struck face
    /// classified from the LOCAL surface normal (<see cref="StruckFace.Unknown"/>
    /// when no hit, no facing, or a top/bottom deck hit). Fail-closed: no
    /// facing, no hit, a top/bottom hit, or a degenerate mesh yields
    /// <see cref="PenetrationBand.Unknown"/>.
    ///
    /// Coordinate convention (probed 2026-08-13 on the real Churchill mesh):
    /// the <c>.scg</c> collision mesh is stored Z-UP — +X right, +Y FORWARD,
    /// +Z up (its rear face normal is −Y, its deck normal is +Z) — while the
    /// decoded world and the four-face box model are Y-UP (+Y up, +Z forward).
    /// The aim ray is therefore rotated into tank-local Y-up space and then
    /// Y↔Z-swapped into the mesh's Z-up space before the raycast.
    /// </summary>
    public static PenetrationVerdict EvaluateAgainstMesh(
        AimRay ray,
        OverlayTankState tank,
        CollisionMesh mesh,
        TankArmor armor,
        ShellSpec shell,
        out StruckFace face,
        double margin = 0.1)
    {
        if (tank.YawRadians is not { } yaw
            || !double.IsFinite(yaw)
            || !double.IsFinite(tank.X) || !double.IsFinite(tank.Y)
            || !double.IsFinite(tank.Z))
        {
            face = StruckFace.Unknown;
            return new PenetrationVerdict(
                PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
        }

        (double originX, double originZ) = ToLocal(ray.OriginX - tank.X, ray.OriginZ - tank.Z, yaw);
        (double dirX, double dirZ) = ToLocal(ray.DirectionX, ray.DirectionZ, yaw);
        AimRay localRay = new(
            originX, ray.OriginY - tank.Y, originZ,
            dirX, ray.DirectionY, dirZ);

        // Swap Y↔Z into the mesh's Z-up space (forward=+Y, up=+Z).
        AimRay meshRay = new(
            localRay.OriginX, localRay.OriginZ, localRay.OriginY,
            localRay.DirectionX, localRay.DirectionZ, localRay.DirectionY);

        MeshHit? hit = CollisionRaycast.Raycast(meshRay, mesh);
        if (hit is null)
        {
            face = StruckFace.Unknown;
            return new PenetrationVerdict(
                PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
        }

        face = ClassifyMeshFace(hit.Value.NormalX, hit.Value.NormalY, hit.Value.NormalZ);
        if (face == StruckFace.Unknown)
        {
            // A top/bottom (deck/belly) hit is not a front/side/rear face —
            // fail closed rather than borrowing a horizontal face's armor.
            return new PenetrationVerdict(
                PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
        }

        double thickness = face switch
        {
            StruckFace.Front => armor.FrontMm,
            StruckFace.Back => armor.RearMm,
            _ => armor.SideMm,
        };

        ArmorPlate plate = new(
            thickness,
            hit.Value.NormalX, hit.Value.NormalY, hit.Value.NormalZ,
            hit.Value.HitX, hit.Value.HitY, hit.Value.HitZ);
        return ArmorPenetration.Evaluate(meshRay, plate, shell, margin);
    }

    /// <summary>
    /// Resolves the full penetration badge for the current camera aim: build
    /// the aim ray, pick the nearest aimed tank, evaluate its struck face
    /// with that tank's armor and the shell, and wrap the verdict in a
    /// <see cref="PenetrationBadge"/>. When a collision mesh is available for
    /// the aimed tank it is used for the true surface normal (PN-5); otherwise
    /// the four-face box model applies. Returns null when the camera carries
    /// no rotation or no tank is aimed at (no badge — never a fabricated one).
    /// A tank absent from <paramref name="armorByEntity"/> or whose struck
    /// face is unknown yields a badge with <see cref="PenetrationBand.Unknown"/>
    /// (or no badge), so the HUD cannot paint a verdict it cannot derive.
    /// </summary>
    public static PenetrationBadge? ResolveBadge(
        OverlayCamera camera,
        IReadOnlyList<OverlayTankState> tanks,
        IReadOnlyDictionary<long, TankArmor> armorByEntity,
        ShellSpec shell,
        double margin = 0.1,
        IReadOnlyDictionary<long, CollisionMesh>? meshesByEntity = null)
    {
        ArgumentNullException.ThrowIfNull(tanks);
        ArgumentNullException.ThrowIfNull(armorByEntity);

        AimRay? ray = BuildAimRay(camera);
        if (ray is null)
        {
            return null;
        }

        long? aimedId = AimedTankId(ray.Value, tanks);
        if (aimedId is null)
        {
            return null;
        }

        OverlayTankState tank = tanks.First(item => item.EntityId == aimedId.Value);
        StruckFace face = SelectStruckFace(ray.Value, tank);
        if (!armorByEntity.TryGetValue(aimedId.Value, out TankArmor armor))
        {
            return new PenetrationBadge(
                aimedId.Value,
                face,
                new PenetrationVerdict(
                    PenetrationBand.Unknown, null, null, null, null, Ricochet: false));
        }

        PenetrationVerdict verdict;
        if (meshesByEntity is not null
            && meshesByEntity.TryGetValue(aimedId.Value, out CollisionMesh? mesh))
        {
            verdict = EvaluateAgainstMesh(
                ray.Value, tank, mesh, armor, shell, out StruckFace meshFace, margin);
            if (meshFace != StruckFace.Unknown)
            {
                face = meshFace;
            }
        }
        else
        {
            verdict = EvaluateAgainst(ray.Value, tank, armor, shell, margin);
        }

        return new PenetrationBadge(aimedId.Value, face, verdict);
    }

    /// <summary>
    /// Rotates a world-space XZ vector into the tank's local collision space
    /// (the inverse of the yaw rotation; the local mesh faces +Z forward).
    /// </summary>
    private static (double X, double Z) ToLocal(double x, double z, double yaw)
    {
        double cos = Math.Cos(yaw);
        double sin = Math.Sin(yaw);
        return ((x * cos) - (z * sin), (x * sin) + (z * cos));
    }

    /// <summary>
    /// Classifies a MESH-LOCAL (Z-up) surface normal into front/back/side:
    /// the collision mesh faces +Y forward in its local space (rear = −Y,
    /// deck = +Z). The dominant HORIZONTAL axis selects the face (sides are
    /// the two ±X normals, treated symmetrically). A dominant VERTICAL normal
    /// (|Z| &gt; |X| and |Z| &gt; |Y|) is a deck/belly hit — not a front/side/
    /// rear face — and returns <see cref="StruckFace.Unknown"/> so the caller
    /// fails closed instead of borrowing a horizontal face's armor.
    /// </summary>
    private static StruckFace ClassifyMeshFace(double nx, double ny, double nz)
    {
        double absX = Math.Abs(nx);
        double absY = Math.Abs(ny);
        double absZ = Math.Abs(nz);
        if (absZ > absX && absZ > absY)
        {
            return StruckFace.Unknown;
        }

        return absY >= absX
            ? (ny >= 0 ? StruckFace.Front : StruckFace.Back)
            : StruckFace.Side;
    }

    /// <summary>
    /// The four horizontal face normals for a tank's facing: front, back, and
    /// the two symmetric sides. An empty array (no facing evidence or
    /// non-finite coordinates) makes the caller fail closed to
    /// <see cref="StruckFace.Unknown"/>.
    /// </summary>
    private static (double Nx, double Nz)[] FaceNormals(OverlayTankState tank)
    {
        if (tank.YawRadians is not { } yaw
            || !double.IsFinite(yaw)
            || !double.IsFinite(tank.X) || !double.IsFinite(tank.Y)
            || !double.IsFinite(tank.Z))
        {
            return [];
        }

        // Facing in the XZ plane (yaw 0 → +Z), matching the packet convention.
        double fx = Math.Sin(yaw);
        double fz = Math.Cos(yaw);
        return
        [
            (fx, fz),                 // front
            (-fx, -fz),               // back
            (fz, -fx),                // side A
            (-fz, fx),                // side B
        ];
    }
}
