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

/// <summary>Reads exact-version metadata from a local game installation without modifying it.</summary>
public interface IInstalledGameMetadataProvider
{
    ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
        CancellationToken cancellationToken);

    ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
        GameMetadataContext context,
        int compactDescriptor,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
        GameMetadataContext context,
        string mapId,
        CancellationToken cancellationToken);
}
