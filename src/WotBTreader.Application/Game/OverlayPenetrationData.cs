using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Game;

/// <summary>
/// The install-derived inputs the pen badge needs for one session: the
/// nominal armor per roster entity (front/side/rear) and the viewpoint
/// tank's shell profile. A face whose nominal thickness is 0 is UNKNOWN
/// (the install's armor XML declares the front via <c>primaryArmor</c> but
/// not the side/rear face mapping), so the badge resolver fails it closed to
/// <see cref="PenetrationBand.Unknown"/> rather than guessing.
/// </summary>
public sealed record PenetrationContext(
    IReadOnlyDictionary<long, TankArmor> ArmorByEntity,
    ShellSpec ViewerShell,
    IReadOnlyDictionary<long, IReadOnlyList<CollisionMeshPart>>? MeshesByEntity = null,
    IReadOnlyList<ShellOption>? Shells = null)
{
    /// <summary>The viewer's available shells (empty when none resolved). The
    /// first option is the stock shell and equals <see cref="ViewerShell"/>.</summary>
    public IReadOnlyList<ShellOption> AvailableShells => Shells ?? [];

    /// <summary>
    /// Selects the shell to score the pen badge with: the option named by
    /// <paramref name="requestedName"/> when present, else the stock shell
    /// (the first option). Returns the spec plus its name, or
    /// <see cref="ViewerShell"/> with a null name when no options exist.
    /// </summary>
    public (ShellSpec Spec, string? Name) SelectShell(string? requestedName)
    {
        foreach (ShellOption option in AvailableShells)
        {
            if (string.Equals(option.Name, requestedName, StringComparison.Ordinal))
            {
                return (option.Spec, option.Name);
            }
        }

        return AvailableShells.Count == 0
            ? (ViewerShell, null)
            : (AvailableShells[0].Spec, AvailableShells[0].Name);
    }

    /// <summary>
    /// Derives the nominal <see cref="TankArmor"/> from a parsed vehicle armor
    /// profile. The FRONT face is the thickest group named by
    /// <c>primaryArmor</c> (the declared frontal plate family); the turret
    /// front is the thickest turret group named by the turret's
    /// <c>primaryArmor</c>. Side/rear (hull and turret) are NOT declared by
    /// the armor XML, so they stay 0 = unknown and the badge resolver fails
    /// them closed to <see cref="PenetrationBand.Unknown"/> rather than
    /// guessing.
    /// </summary>
    public static TankArmor NominalArmor(VehicleArmorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        HashSet<string> primary = new(profile.PrimaryArmorGroups, StringComparer.Ordinal);
        double front = 0;
        foreach (ArmorGroup group in profile.HullGroups)
        {
            if (primary.Contains(group.Name))
            {
                front = Math.Max(front, group.ThicknessMm);
            }
        }

        HashSet<string> turretPrimary = new(profile.TurretPrimaryArmorGroups, StringComparer.Ordinal);
        double turretFront = 0;
        foreach (ArmorGroup group in profile.TurretGroups)
        {
            if (turretPrimary.Contains(group.Name))
            {
                turretFront = Math.Max(turretFront, group.ThicknessMm);
            }
        }

        return new TankArmor(
            FrontMm: front, SideMm: 0, RearMm: 0, TurretFrontMm: turretFront);
    }
}

/// <summary>
/// The install-derived inputs for ONE roster tank (any tank — attacker or
/// victim — not just the viewer): nominal armor, the best-effort collision
/// mesh, and the stock gun's shell options. Drives the offline PN-4 scorer,
/// which validates the pen model against decoded shot outcomes. Fail-closed:
/// null when the install is absent, the tank's armor cannot be resolved, or
/// its stock gun has no shells — never a fabricated profile.
/// </summary>
public sealed record PenetrationTankData(
    TankArmor Armor,
    IReadOnlyList<CollisionMeshPart>? Mesh,
    IReadOnlyList<ShellOption> Shells);

/// <summary>
/// Supplies the install-static penetration data (armor + shell) the overlay
/// frame needs to render the pen indicator. Implementations read the game
/// install read-only (the <c>PenetrationDataParser</c> lane); a null result
/// means the data is unavailable for this session and the frame simply omits
/// the badge — never a fabricated verdict.
/// </summary>
public interface IOverlayPenetrationData
{
    /// <summary>
    /// Resolves the penetration context for a decoded session: nominal armor
    /// for every roster entity and the viewpoint tank's shell. Fail-closed:
    /// null when the install is absent, an entity's armor cannot be resolved,
    /// or the viewer's shell is unknown.
    /// </summary>
    ValueTask<PenetrationContext?> ResolveAsync(
        ReplayDecodeProjection projection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the penetration inputs for ONE tank by its decoded TankId
    /// (the enrichment's <c>nation:tank</c> VehicleId form or a bare name).
    /// Used by the offline PN-4 scorer to validate the pen model against
    /// decoded shot outcomes for arbitrary attacker/victim pairs. Fail-closed:
    /// null when the install is absent or the tank's armor/shells cannot be
    /// resolved.
    /// </summary>
    ValueTask<PenetrationTankData?> ResolveTankAsync(
        string tankId,
        CancellationToken cancellationToken);
}
