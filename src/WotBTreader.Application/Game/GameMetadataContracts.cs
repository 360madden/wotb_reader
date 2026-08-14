using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Game;

public sealed record InstalledGameIdentity(
    string ExecutablePath,
    string ProductVersion,
    ContentHash ExecutableSha256,
    string ResourceRoot,
    IReadOnlyList<string> DlcRoots);

public sealed record GameMetadataContext(
    InstalledGameIdentity Identity,
    string ProviderVersion,
    ContentHash SourceSetHash,
    DateTimeOffset LoadedAtUtc);

public sealed record VehicleMetadata(
    int CompactDescriptor,
    string VehicleId,
    string DisplayName,
    TankClass TankClass,
    string Nation,
    string GameVersion,
    ContentHash SourceHash);

/// <summary>
/// Exact-build map metadata. <paramref name="SceneResourcePath"/> preserves
/// the install's relative <c>localName</c> scene path so resource consumers can
/// resolve name-based assets from a numeric replay arena ID without guessing.
/// </summary>
public sealed record MapMetadata(
    string MapId,
    string DisplayName,
    double? WorldMinX,
    double? WorldMaxX,
    double? WorldMinZ,
    double? WorldMaxZ,
    string GameVersion,
    ContentHash SourceHash,
    string? SceneResourcePath = null);

/// <summary>A named armor group and its nominal thickness in millimeters.</summary>
public readonly record struct ArmorGroup(string Name, double ThicknessMm);

/// <summary>
/// One vehicle's armor model from the install's detailed definition XML:
/// per-group hull and turret thickness plus the group names the definition
/// declares as primary (front). The group → face (front/side/rear) mapping is
/// NOT baked in here — that is the plate-geometry concern (the <c>.scg</c>
/// collision mesh), not this store.
/// </summary>
public sealed record VehicleArmorProfile(
    string VehicleId,
    IReadOnlyList<ArmorGroup> HullGroups,
    IReadOnlyList<ArmorGroup> TurretGroups,
    IReadOnlyList<string> PrimaryArmorGroups,
    IReadOnlyList<string> TurretPrimaryArmorGroups);

/// <summary>
/// The first (stock) gun's identity and ordered shell names from a vehicle
/// definition. The gun identity is required when joining to
/// <c>components/guns.xml.dvpl</c>: a shell name can be shared by multiple
/// guns with different piercing profiles.
/// </summary>
public sealed record StockGunProfile(
    string GunName,
    IReadOnlyList<string> ShellNames);

/// <summary>
/// A shell's penetration-relevant stats from <c>components/shells.xml.dvpl</c>.
/// <see cref="Kind"/> is the shell family (ARMOR_PIERCING / _CR / HIGH_EXPLOSIVE
/// / HOLLOW_CHARGE); ricochet and normalization are the degrees the game uses.
/// </summary>
public sealed record ShellProfile(
    string Name,
    string Kind,
    double CaliberMm,
    double NormalizationDegrees,
    double RicochetDegrees);

/// <summary>
/// One gun→shell pairing from <c>components/guns.xml.dvpl</c>: the shell's
/// two-point penetration range (near = 0 m, far = <see cref="MaxDistanceMeters"/>),
/// muzzle speed, and max range — the inputs the pen model's range drop consumes.
/// </summary>
public sealed record GunShellProfile(
    string GunName,
    string ShellName,
    double PiercingPowerNearMm,
    double PiercingPowerFarMm,
    double MaxDistanceMeters,
    double SpeedMetersPerSecond);

/// <summary>
/// Reads exact-version metadata from a local game installation without
/// modifying it. All operations are read-only and evidence-bounded.
/// </summary>
public interface IInstalledGameMetadataProvider
{
    /// <summary>
    /// Probes the local game installation and returns identity metadata,
    /// including the executable SHA-256 and version string.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Game identity and metadata context on success.</returns>
    ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a compact vehicle descriptor to a vehicle metadata record
    /// containing display name, class, and nation.
    /// </summary>
    /// <param name="context">Metadata context from a prior <see cref="ProbeAsync"/> call.</param>
    /// <param name="compactDescriptor">Game-specific compact descriptor integer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Vehicle metadata on success.</returns>
    ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
        GameMetadataContext context,
        int compactDescriptor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a map identifier to a map metadata record containing
    /// display name and world boundary coordinates.
    /// </summary>
    /// <param name="context">Metadata context from a prior <see cref="ProbeAsync"/> call.</param>
    /// <param name="mapId">Map identifier string (e.g. "maps/fort_frontier").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Map metadata on success.</returns>
    ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
        GameMetadataContext context,
        string mapId,
        CancellationToken cancellationToken);
}
