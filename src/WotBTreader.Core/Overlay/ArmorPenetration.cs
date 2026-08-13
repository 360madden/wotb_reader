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
/// The shell family (the install's shells.xml <c>kind</c>). It controls the
/// auto-ricochet OVERMATCH rule: AP/APCR suppress the bounce when the caliber
/// overmatches the plate (caliber &gt; 3× thickness), HEAT ricochets at its
/// angle regardless of caliber (the 3-caliber rule does NOT apply to HEAT),
/// and HE never ricochets. <see cref="Unknown"/> keeps the pre-kind behavior
/// (the kinetic overmatch rule) for shells resolved without a family.
/// </summary>
public enum ShellKind
{
    /// <summary>No family resolved — treated as a generic kinetic shell (the
    /// 3× overmatch rule applies).</summary>
    Unknown = 0,

    /// <summary>Armor-piercing (the install's <c>ARMOR_PIERCING</c>).</summary>
    ArmorPiercing = 1,

    /// <summary>Armor-piercing composite rigid (<c>ARMOR_PIERCING_CR</c>).</summary>
    ArmorPiercingCr = 2,

    /// <summary>High-explosive (<c>HIGH_EXPLOSIVE</c>) — never ricochets.</summary>
    HighExplosive = 3,

    /// <summary>High-explosive anti-tank / HEAT (<c>HOLLOW_CHARGE</c>).</summary>
    HollowCharge = 4,
}

/// <summary>Maps the install's shells.xml <c>kind</c> string to its
/// <see cref="ShellKind"/> (the four families the game ships).</summary>
public static class ShellKinds
{
    public static ShellKind FromInstallName(string? kindName) => kindName switch
    {
        "ARMOR_PIERCING" => ShellKind.ArmorPiercing,
        "ARMOR_PIERCING_CR" => ShellKind.ArmorPiercingCr,
        "HIGH_EXPLOSIVE" => ShellKind.HighExplosive,
        "HOLLOW_CHARGE" => ShellKind.HollowCharge,
        _ => ShellKind.Unknown,
    };
}

/// <summary>
/// One available shell for the viewer's gun, surfaced to the HUD so it can
/// cycle the pen-badge shell: the install shell name, its family (the
/// shells.xml <c>kind</c>), and its resolved penetration profile. The first
/// option is the stock shell.
/// </summary>
public readonly record struct ShellOption(
    string Name,
    ShellKind Kind,
    ShellSpec Spec);

/// <summary>
/// A shell's penetration profile: base penetration, caliber, linear
/// penetration drop with distance, the auto-ricochet angle, and the shell
/// normalization (degrees the shell "digs in", reducing the effective
/// incidence). Ricochet, drop, and normalization follow the WoT Blitz
/// mechanics the PN design records (the official "Armor Penetration
/// Mechanics" support article), with the per-shell values read from the
/// install's shells.xml: AP/APCR auto-bounce at ricochetAngle 70° from the
/// normal (suppressed by the 3× overmatch rule), HEAT ricochets at 85°, and
/// HE carries NO ricochet angle (≤ 0 = never ricochet). Penetration falls
/// off with range; normalization is per-shell (AP 5°/15°, APCR 2°, HE/HEAT
/// 0) and is amplified by the two-caliber rule; the ricochet check runs on
/// the RAW impact angle before normalization applies. <see cref="Kind"/>
/// (the install's shells.xml family) controls the overmatch rule: HEAT
/// ricochets regardless of caliber, while AP/APCR overmatch suppresses the
/// bounce. The live game's ±5%
/// penetration randomization
/// (Update 6.0+) is NOT modeled here — it is a validation target, never
/// assumed (the ±25% figure is the DAMAGE spread, not penetration). See
/// <c>docs/operations/pen-chance-design.md</c>.
/// </summary>
public readonly record struct ShellSpec(
    double Penetration0Mm,
    double CaliberMm,
    double DropPerMeterMm = 0.0,
    double RicochetDegrees = 70.0,
    double NormalizationDegrees = 0.0,
    ShellKind Kind = ShellKind.Unknown)
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
        double normalizationDegrees = 0.0,
        ShellKind kind = ShellKind.Unknown)
    {
        if (!double.IsFinite(maxDistance) || maxDistance <= 0
            || !double.IsFinite(piercingPowerNearMm)
            || !double.IsFinite(piercingPowerFarMm))
        {
            return new ShellSpec(
                0.0, caliberMm, 0.0, ricochetDegrees, normalizationDegrees, kind);
        }

        double drop = Math.Max(0.0, (piercingPowerNearMm - piercingPowerFarMm) / maxDistance);
        return new ShellSpec(
            piercingPowerNearMm, caliberMm, drop, ricochetDegrees, normalizationDegrees, kind);
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
/// indicator. Ray-plane hit → angle of incidence → ricochet (on the RAW
/// angle, with overmatch) → normalization (with the two-caliber rule) →
/// effective armor → penetration at range → a banded verdict.
///
/// Conventions (the WoT Blitz mechanics, per the official "Armor
/// Penetration Mechanics" support article):
///  - The incidence angle is measured FROM the plate normal: 0 = head-on
///    (best penetration), approaching 90° = grazing. Effective armor =
///    thickness / cos(incidence), so an angled plate multiplies its
///    protection — the standard WoT angling model.
///  - Ricochet: a shell auto-bounces when the RAW incidence is ≥ its
///    ricochet angle (70° AP/APCR, 85° HEAT, ≤ 0 = never ricochet for HE),
///    UNLESS the caliber overmatches (caliber &gt; 3 × nominal plate
///    thickness). Normalization applies ONLY when there is no ricochet — it
///    never digs a shell out of a bounce.
///  - Normalization: per-shell from the install data (AP 5°/15°, APCR 2°,
///    HE/HEAT 0). The two-caliber rule amplifies it when caliber &gt; 2 ×
///    plate thickness: resulting = base × 1.4 × caliber / (2 × thickness).
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
    /// (70° AP/APCR, 85° HEAT — the install's per-shell <c>ricochetAngle</c>)
    /// AND the shell does NOT overmatch (caliber ≤ 3 × nominal plate
    /// thickness). A ricochet angle ≤ 0 means the shell NEVER ricochets (HE
    /// shells carry no <c>ricochetAngle</c> in the install data), so this
    /// returns false. Non-finite inputs also return false. The overmatch
    /// suppression applies to AP/APCR (and <see cref="ShellKind.Unknown"/>)
    /// only — HEAT (<see cref="ShellKind.HollowCharge"/>) ricochets at its
    /// angle regardless of caliber, per the WoT Blitz mechanics.
    /// </summary>
    public static bool Ricochets(
        double incidenceRadians,
        double caliberMm,
        double thickness,
        double ricochetDegrees,
        ShellKind kind = ShellKind.Unknown)
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

        // HEAT does not benefit from the 3× overmatch rule: it ricochets at
        // its (85°) angle regardless of caliber. AP/APCR are suppressed by
        // caliber > 3× thickness.
        if (kind == ShellKind.HollowCharge)
        {
            return true;
        }

        return caliberMm <= 3.0 * thickness;
    }

    /// <summary>
    /// The effective incidence after shell normalization, including the
    /// two-caliber rule: when the caliber is MORE than 2 × the nominal plate
    /// thickness (ignoring the impact angle), the base normalization is
    /// amplified to <c>base × 1.4 × caliber / (2 × thickness)</c>, per the
    /// WoT Blitz mechanics. Floored at 0 (a shell cannot normalize past
    /// head-on). Applied only when there is no ricochet — the ricochet check
    /// runs on the raw angle first.
    /// </summary>
    private static double NormalizedIncidence(
        double incidenceRadians,
        double caliberMm,
        double thickness,
        double normalizationDegrees)
    {
        double normDegrees = normalizationDegrees;
        if (caliberMm > 2.0 * thickness)
        {
            normDegrees = normalizationDegrees * 1.4 * caliberMm / (2.0 * thickness);
        }

        return Math.Max(0.0, incidenceRadians - normDegrees * Math.PI / 180.0);
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
            // RicochetDegrees ≤ 0 means "never ricochet" (HE shells carry no
            // ricochetAngle in the install data), not an invalid angle — only
            // a positive angle out of range is rejected.
            || shell.RicochetDegrees >= 90
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

        // Ricochet is checked on the RAW impact angle — normalization is
        // applied only when there is no ricochet (it never digs a shell out
        // of a bounce), per the WoT Blitz mechanics.
        bool ricochet = Ricochets(
            incidence.Value,
            shell.CaliberMm,
            plate.Thickness,
            shell.RicochetDegrees,
            shell.Kind);

        double normalizedIncidence = ricochet
            ? incidence.Value
            : NormalizedIncidence(
                incidence.Value, shell.CaliberMm, plate.Thickness, shell.NormalizationDegrees);

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
