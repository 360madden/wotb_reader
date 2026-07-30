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

public sealed record MapMetadata(
    string MapId,
    string DisplayName,
    double? WorldMinX,
    double? WorldMaxX,
    double? WorldMinZ,
    double? WorldMaxZ,
    string GameVersion,
    ContentHash SourceHash);

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
