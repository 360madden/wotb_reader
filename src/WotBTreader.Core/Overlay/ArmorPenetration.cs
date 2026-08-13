namespace WotBTreader.Core.Overlay;

/// <summary>
/// A world-space ray: an origin and a unit direction. World axes follow the
/// decoded telemetry convention (X/Z horizontal, Y up — the same convention
/// as <see cref="WorldToScreen"/>).
/// </summary>
public readonly record struct AimRay(
    double OriginX,
    double OriginY,
    double OriginZ,
    double DirectionX,
    double DirectionY,
    double DirectionZ);

/// <summary>
/// A single armor plate: nominal thickness plus an outward unit normal and a
/// point on the plate's plane. For PN-2 the plate is an infinite plane — the
/// ray-plane hit, incidence, effective-armor, and ricochet math live here;
/// selecting WHICH finite plate of a hull box the ray actually strikes (and
/// its plate bounds) is the PN-3 wiring concern, not this module.
/// </summary>
public readonly record struct ArmorPlate(
    double Thickness,
    double NormalX,
    double NormalY,
    double NormalZ,
    double PlaneX,
    double PlaneY,
    double PlaneZ);

/// <summary>
/// A shell's penetration profile: base penetration, caliber, linear
/// penetration drop with distance, the auto-ricochet angle, and the shell
/// normalization (degrees the shell "digs in", reducing the effective
/// incidence). Ricochet and drop follow the WoT Blitz mechanics the PN design
/// records — AP/APCR shells auto-bounce at an incidence angle ≥ 70° from the
/// normal (suppressed by the 3× overmatch rule), penetration falls off with
/// range, and normalization is applied before both. The ±25% penetration
/// randomization the live game applies is NOT modeled here — it is a
/// validation target, never assumed (see
/// <c>docs/operations/pen-chance-design.md</c>).
/// </summary>
public readonly record struct ShellSpec(
    double Penetration0Mm,
    double CaliberMm,
    double DropPerMeterMm = 0.0,
    double RicochetDegrees = 70.0,
    double NormalizationDegrees = 0.0)
{
    /// <summary>
    /// Builds a profile from the install's <c>piercingPower</c> two-point
    /// range pair (penetration at 0 m and at <paramref name="maxDistance"/>),
    /// matching <c>components/guns.xml.dvpl</c>; the linear drop is derived
    /// from the two points. Fail-closed: an invalid max distance yields a
    /// profile with zero penetration, which
    /// <see cref="ArmorPenetration.Evaluate"/> rejects as Unknown.
    /// </summary>
    public static ShellSpec FromPiercingPower(
        double piercingPowerNearMm,
        double piercingPowerFarMm,
        double maxDistance,
        double caliberMm,
        double ricochetDegrees = 70.0,
        double normalizationDegrees = 0.0)
    {
        if (!double.IsFinite(maxDistance) || maxDistance <= 0
            || !double.IsFinite(piercingPowerNearMm)
            || !double.IsFinite(piercingPowerFarMm))
        {
            return new ShellSpec(0.0, caliberMm, 0.0, ricochetDegrees, normalizationDegrees);
        }

        double drop = Math.Max(0.0, (piercingPowerNearMm - piercingPowerFarMm) / maxDistance);
        return new ShellSpec(
            piercingPowerNearMm, caliberMm, drop, ricochetDegrees, normalizationDegrees);
    }
}

/// <summary>
/// The deterministic penetration verdict for one aim ray against one plate.
/// <c>Band</c> is the banded green/yellow/red classification;
/// <c>Ricochet</c> is true when the shot auto-bounces. Diagnostic fields are
/// null when the corresponding value is undefined (no hit, grazing, or
/// invalid input) — a null never masquerades as a number.
/// </summary>
public enum PenetrationBand
{
    /// <summary>Fail-closed: no valid verdict (invalid input, no hit, or
    /// degenerate geometry).</summary>
    Unknown = 0,

    /// <summary>Red — the effective armor exceeds the shell's penetration at
    /// this range, or the shot ricochets.</summary>
    NoPen = 1,

    /// <summary>Yellow — effective armor and penetration are within the
    /// caller's margin band of each other.</summary>
    Marginal = 2,

    /// <summary>Green — penetration at range comfortably beats the effective
    /// armor.</summary>
    Pen = 3,
}

/// <summary>
/// The full result of evaluating an aim ray against a plate with a shell.
/// </summary>
public readonly record struct PenetrationVerdict(
    PenetrationBand Band,
    double? HitDistance,
    double? IncidenceRadians,
    double? EffectiveArmorMm,
    double? PenetrationMmAtRange,
    bool Ricochet);

/// <summary>
/// Pure, fail-closed armor-penetration math for the overlay's penetration
/// indicator. Ray-plane hit → angle of incidence → effective armor → ricochet
/// (with overmatch) → penetration at range → a banded verdict.
///
/// Conventions:
///  - The incidence angle is measured FROM the plate normal: 0 = head-on
///    (best penetration), approaching 90° = grazing. Effective armor =
///    thickness / cos(incidence), so an angled plate multiplies its
///    protection — the standard WoT angling model.
///  - Ricochet: AP/APCR shells auto-bounce when the incidence is ≥ the
///    shell's ricochet angle (default 70°), UNLESS the caliber overmatches
///    (caliber &gt; 3 × nominal plate thickness).
///  - Normalization: the shell's normalization angle (per-shell, from the
///    install data) reduces the incidence before the ricochet and
///    effective-armor checks — a shell that "digs in" can avoid a ricochet.
///  - Penetration drops linearly with distance; it never goes below zero.
///  - The band is deterministic: Pen when penetration &gt; effective × (1 +
///    margin), NoPen when penetration &lt; effective × (1 − margin), Marginal
///    in between, and NoPen on any ricochet.
///
/// Fail-closed throughout: non-finite or degenerate inputs produce
/// <see cref="PenetrationBand.Unknown"/> (or null) — a NaN can never become a
/// green "will penetrate" verdict for the overlay.
/// </summary>
public static class ArmorPenetration
{
    /// <summary>Smallest cosine of incidence treated as a real (non-grazing)
    /// hit. Below this the plate is effectively parallel to the ray.</summary>
    private const double MinCosine = 1e-9;

    /// <summary>
    /// Signed ray-plane intersection distance along the ray, in world units.
    /// Returns null when the ray is parallel to the plane (|dot(dir, normal)|
    /// ≤ <see cref="MinCosine"/>) or the intersection lies at/behind the ray
    /// origin.
    /// </summary>
    public static double? HitDistance(AimRay ray, ArmorPlate plate)
    {
        if (!Finite(ray) || !Finite(plate))
        {
            return null;
        }

        double denom = ray.DirectionX * plate.NormalX
            + ray.DirectionY * plate.NormalY
            + ray.DirectionZ * plate.NormalZ;
        if (Math.Abs(denom) <= MinCosine)
        {
            return null;
        }

        double numer = (plate.PlaneX - ray.OriginX) * plate.NormalX
            + (plate.PlaneY - ray.OriginY) * plate.NormalY
            + (plate.PlaneZ - ray.OriginZ) * plate.NormalZ;
        double t = numer / denom;
        return t > 0.0 ? t : null;
    }

    /// <summary>
    /// Angle of incidence in radians, measured FROM the plate normal (0 =
    /// head-on). Computed from the absolute dot product, so the plate's two
    /// faces are treated symmetrically. Returns null when there is no hit.
    /// </summary>
    public static double? IncidenceRadians(AimRay ray, ArmorPlate plate)
    {
        double? distance = HitDistance(ray, plate);
        if (distance is null)
        {
            return null;
        }

        double cosIncidence = Math.Abs(
            ray.DirectionX * plate.NormalX
            + ray.DirectionY * plate.NormalY
            + ray.DirectionZ * plate.NormalZ);
        return Math.Acos(Math.Clamp(cosIncidence, 0.0, 1.0));
    }

    /// <summary>
    /// Effective armor thickness at an incidence angle: nominal thickness /
    /// cos(incidence). Returns null for non-finite or grazing incidence
    /// (cos ≤ <see cref="MinCosine"/>), where the model is degenerate.
    /// </summary>
    public static double? EffectiveArmor(double thickness, double incidenceRadians)
    {
        if (!double.IsFinite(thickness) || thickness <= 0
            || !double.IsFinite(incidenceRadians)
            || incidenceRadians < 0 || incidenceRadians > Math.PI / 2.0)
        {
            return null;
        }

        double cosine = Math.Cos(incidenceRadians);
        if (cosine <= MinCosine)
        {
            return null;
        }

        return thickness / cosine;
    }

    /// <summary>
    /// Auto-ricochet rule: true when the incidence reaches the ricochet angle
    /// (default 70° from the normal) AND the shell does NOT overmatch
    /// (caliber ≤ 3 × nominal plate thickness). Fail-closed: invalid inputs
    /// do not ricochet silently — but they also cannot produce a Pen verdict
    /// because <see cref="Evaluate"/> rejects them first.
    /// </summary>
    public static bool Ricochets(
        double incidenceRadians,
        double caliberMm,
        double thickness,
        double ricochetDegrees)
    {
        if (!double.IsFinite(incidenceRadians)
            || !double.IsFinite(caliberMm) || caliberMm <= 0
            || !double.IsFinite(thickness) || thickness <= 0
            || !double.IsFinite(ricochetDegrees)
            || ricochetDegrees <= 0 || ricochetDegrees >= 90)
        {
            return false;
        }

        if (incidenceRadians < ricochetDegrees * Math.PI / 180.0)
        {
            return false;
        }

        // 3× overmatch suppresses the ricochet.
        return caliberMm <= 3.0 * thickness;
    }

    /// <summary>
    /// Shell penetration at a distance, applying the linear drop. Never
    /// returns a negative value; invalid inputs return null.
    /// </summary>
    public static double? PenetrationAtRange(
        double penetration0Mm,
        double distance,
        double dropPerMeterMm)
    {
        if (!double.IsFinite(penetration0Mm) || penetration0Mm <= 0
            || !double.IsFinite(distance) || distance < 0
            || !double.IsFinite(dropPerMeterMm) || dropPerMeterMm < 0)
        {
            return null;
        }

        return Math.Max(0.0, penetration0Mm - dropPerMeterMm * distance);
    }

    /// <summary>
    /// Full deterministic verdict for an aim ray against a plate with a
    /// shell. Fail-closed: any invalid input or no-hit geometry returns
    /// <see cref="PenetrationBand.Unknown"/> with null diagnostics.
    /// </summary>
    /// <param name="ray">The aim ray (world-space origin + unit direction).</param>
    /// <param name="plate">The armor plate being evaluated.</param>
    /// <param name="shell">The shell's penetration profile.</param>
    /// <param name="margin">Symmetric classification band as a fraction of
    /// effective armor (default 0.1 = ±10%). Must be ≥ 0.</param>
    public static PenetrationVerdict Evaluate(
        AimRay ray,
        ArmorPlate plate,
        ShellSpec shell,
        double margin = 0.1)
    {
        if (!Finite(ray) || !Finite(plate) || !Finite(shell)
            || !double.IsFinite(margin) || margin < 0
            || plate.Thickness <= 0
            || shell.Penetration0Mm <= 0 || shell.CaliberMm <= 0
            || shell.DropPerMeterMm < 0
            || shell.RicochetDegrees <= 0 || shell.RicochetDegrees >= 90
            || shell.NormalizationDegrees < 0 || shell.NormalizationDegrees >= 90)
        {
            return Unknown();
        }

        double? distance = HitDistance(ray, plate);
        double? incidence = IncidenceRadians(ray, plate);
        if (distance is null || incidence is null)
        {
            return Unknown();
        }

        double normalizedIncidence = Math.Max(
            0.0, incidence.Value - shell.NormalizationDegrees * Math.PI / 180.0);

        bool ricochet = Ricochets(
            normalizedIncidence,
            shell.CaliberMm,
            plate.Thickness,
            shell.RicochetDegrees);

        double? effective = EffectiveArmor(plate.Thickness, normalizedIncidence);
        double? penAtRange = PenetrationAtRange(
            shell.Penetration0Mm, distance.Value, shell.DropPerMeterMm);
        if (effective is null || penAtRange is null)
        {
            return new PenetrationVerdict(
                PenetrationBand.Unknown,
                distance,
                incidence,
                effective,
                penAtRange,
                ricochet);
        }

        PenetrationBand band;
        if (ricochet)
        {
            band = PenetrationBand.NoPen;
        }
        else if (penAtRange.Value > effective.Value * (1.0 + margin))
        {
            band = PenetrationBand.Pen;
        }
        else if (penAtRange.Value < effective.Value * (1.0 - margin))
        {
            band = PenetrationBand.NoPen;
        }
        else
        {
            band = PenetrationBand.Marginal;
        }

        return new PenetrationVerdict(
            band, distance, incidence, effective, penAtRange, ricochet);
    }

    private static bool Finite(AimRay ray) =>
        double.IsFinite(ray.OriginX) && double.IsFinite(ray.OriginY)
        && double.IsFinite(ray.OriginZ)
        && double.IsFinite(ray.DirectionX) && double.IsFinite(ray.DirectionY)
        && double.IsFinite(ray.DirectionZ);

    private static bool Finite(ArmorPlate plate) =>
        double.IsFinite(plate.Thickness)
        && double.IsFinite(plate.NormalX) && double.IsFinite(plate.NormalY)
        && double.IsFinite(plate.NormalZ)
        && double.IsFinite(plate.PlaneX) && double.IsFinite(plate.PlaneY)
        && double.IsFinite(plate.PlaneZ);

    private static bool Finite(ShellSpec shell) =>
        double.IsFinite(shell.Penetration0Mm) && double.IsFinite(shell.CaliberMm)
        && double.IsFinite(shell.DropPerMeterMm)
        && double.IsFinite(shell.RicochetDegrees)
        && double.IsFinite(shell.NormalizationDegrees);

    private static PenetrationVerdict Unknown() =>
        new(PenetrationBand.Unknown, null, null, null, null, Ricochet: false);
}
