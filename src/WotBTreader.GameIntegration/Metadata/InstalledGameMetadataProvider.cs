using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;

namespace WotBTreader.GameIntegration.Metadata;

/// <summary>
/// Resolves vehicle and map metadata from exact-version local resources. Cached
/// projections are keyed by executable identity and every selected source hash.
/// </summary>
public sealed class InstalledGameMetadataProvider : IInstalledGameMetadataProvider
{
    private const string ProviderVersion = "wotb-installed-metadata/1.0.0";

    // The country compact id each nation contributes to a vehicle compact
    // descriptor: descriptor = (vehicleTypeId << 8) | countryId, where
    // countryId = (index << 4) | 1 and index is the game's Country enum
    // order (germany = 0, ussr = 1, usa = 2, china = 3, france = 4,
    // uk = 5, japan = 6, european = 7, other = 8). Pinned 2026-08-14
    // against the ground-truth 11.19.0.10 replay + install: germany = 1
    // (PzIV `0`/Nashorn `46`), usa = 33 (M4_Sherman `4` → `1057`),
    // uk = 81 (GB08_Churchill_I `11` → `2897`, GB63_TOG_II `210` →
    // `53841`); the 0xN1 spacing is observed across the session's
    // descriptors (1/17/33/81/97/113/129). The earlier 0–8 enumeration
    // matched only germany and silently dropped every other nation's tanks
    // from the vehicle-name/armor enrichment.
    private static readonly (string Name, int Id)[] Nations =
    [
        ("ussr", 17),
        ("germany", 1),
        ("usa", 33),
        ("china", 49),
        ("france", 65),
        ("uk", 81),
        ("japan", 97),
        ("european", 113),
        ("other", 129),
    ];

    private readonly IGameInstallationDiscovery _discovery;
    private readonly IDvplReader _dvplReader;
    private readonly GameIntegrationOptions _options;
    private readonly ILogger<InstalledGameMetadataProvider> _logger;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, MetadataSnapshot> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a version-gated installed-game metadata provider.</summary>
    public InstalledGameMetadataProvider(
        IGameInstallationDiscovery discovery,
        IDvplReader dvplReader,
        GameIntegrationOptions options,
        ILogger<InstalledGameMetadataProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(dvplReader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _discovery = discovery;
        _dvplReader = dvplReader;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<GameMetadataContext>> ProbeAsync(
        CancellationToken cancellationToken)
    {
        OperationResult<InstalledGameIdentity> discoveryResult =
            await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!discoveryResult.IsSuccess)
        {
            return OperationResult.Failure<GameMetadataContext>(discoveryResult.Error!);
        }

        InstalledGameIdentity identity = discoveryResult.Value!;
        if (!_options.SupportedProductVersions.Contains(identity.ProductVersion))
        {
            return OperationResult.Failure<GameMetadataContext>(
                new ApplicationError(
                    "game.metadata.unsupported_version",
                    $"Installed WotB version '{identity.ProductVersion}' is not supported."));
        }

        OperationResult<SourceManifest> manifestResult =
            await BuildManifestAsync(identity, cancellationToken).ConfigureAwait(false);
        if (!manifestResult.IsSuccess)
        {
            return OperationResult.Failure<GameMetadataContext>(manifestResult.Error!);
        }

        return OperationResult.Success(
            new GameMetadataContext(
                identity,
                ProviderVersion,
                manifestResult.Value!.SourceSetHash,
                DateTimeOffset.UtcNow),
            manifestResult.Warnings.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<VehicleMetadata>> ResolveVehicleAsync(
        GameMetadataContext context,
        int compactDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        OperationResult<MetadataSnapshot> snapshotResult =
            await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess)
        {
            return OperationResult.Failure<VehicleMetadata>(
                snapshotResult.Error!,
                snapshotResult.Warnings.ToArray());
        }

        MetadataSnapshot snapshot = snapshotResult.Value!;
        if (!snapshot.Vehicles.TryGetValue(compactDescriptor, out VehicleMetadata? metadata))
        {
            return OperationResult.Failure<VehicleMetadata>(
                new ApplicationError(
                    "game.metadata.vehicle_not_found",
                    "The vehicle compact descriptor is not present in this exact game build."),
                snapshotResult.Warnings.ToArray());
        }

        return OperationResult.Success(metadata, snapshotResult.Warnings.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<MapMetadata>> ResolveMapAsync(
        GameMetadataContext context,
        string mapId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return OperationResult.Failure<MapMetadata>(
                new ApplicationError("game.metadata.invalid_map_id", "A map ID is required."));
        }

        OperationResult<MetadataSnapshot> snapshotResult =
            await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess)
        {
            return OperationResult.Failure<MapMetadata>(
                snapshotResult.Error!,
                snapshotResult.Warnings.ToArray());
        }

        MetadataSnapshot snapshot = snapshotResult.Value!;
        if (!snapshot.Maps.TryGetValue(mapId.Trim(), out MapMetadata? metadata))
        {
            return OperationResult.Failure<MapMetadata>(
                new ApplicationError(
                    "game.metadata.map_not_found",
                    "The map ID is not present in this exact game build."),
                snapshotResult.Warnings.ToArray());
        }

        return OperationResult.Success(metadata, snapshotResult.Warnings.ToArray());
    }

    private async ValueTask<OperationResult<MetadataSnapshot>> GetSnapshotAsync(
        GameMetadataContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.ProviderVersion, ProviderVersion, StringComparison.Ordinal))
        {
            return OperationResult.Failure<MetadataSnapshot>(
                new ApplicationError(
                    "game.metadata.provider_mismatch",
                    "The metadata context belongs to a different provider version."));
        }

        if (!_options.SupportedProductVersions.Contains(context.Identity.ProductVersion))
        {
            return OperationResult.Failure<MetadataSnapshot>(
                new ApplicationError(
                    "game.metadata.unsupported_version",
                    "The metadata context targets an unsupported exact game version."));
        }

        OperationResult<InstalledGameIdentity> currentIdentityResult =
            await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!currentIdentityResult.IsSuccess)
        {
            return OperationResult.Failure<MetadataSnapshot>(currentIdentityResult.Error!);
        }

        InstalledGameIdentity currentIdentity = currentIdentityResult.Value!;
        if (!IdentityMatches(context.Identity, currentIdentity))
        {
            return OperationResult.Failure<MetadataSnapshot>(
                new ApplicationError(
                    "game.metadata.context_stale",
                    "The installed game identity changed after this context was probed.",
                    Retryable: true));
        }

        OperationResult<SourceManifest> manifestResult =
            await BuildManifestAsync(currentIdentity, cancellationToken).ConfigureAwait(false);
        if (!manifestResult.IsSuccess)
        {
            return OperationResult.Failure<MetadataSnapshot>(manifestResult.Error!);
        }

        SourceManifest manifest = manifestResult.Value!;
        if (manifest.SourceSetHash != context.SourceSetHash)
        {
            return OperationResult.Failure<MetadataSnapshot>(
                new ApplicationError(
                    "game.metadata.context_stale",
                    "Installed metadata changed after this context was probed.",
                    Retryable: true));
        }

        string cacheKey = string.Join(
            ':',
            context.Identity.ProductVersion,
            context.Identity.ExecutableSha256.Value,
            context.SourceSetHash.Value);

        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out MetadataSnapshot? cached))
            {
                return OperationResult.Success(cached, manifestResult.Warnings.ToArray());
            }
        }

        OperationResult<MetadataSnapshot> buildResult =
            await BuildSnapshotAsync(context, manifest, cancellationToken).ConfigureAwait(false);
        if (!buildResult.IsSuccess)
        {
            return buildResult;
        }

        lock (_cacheGate)
        {
            if (_cache.Count >= _options.MaxMetadataCacheEntries)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = buildResult.Value!;
        }

        return OperationResult.Success(
            buildResult.Value!,
            manifestResult.Warnings.Concat(buildResult.Warnings).Distinct(StringComparer.Ordinal).ToArray());
    }

    private async ValueTask<OperationResult<SourceManifest>> BuildManifestAsync(
        InstalledGameIdentity identity,
        CancellationToken cancellationToken)
    {
        ResourceOverlay overlay = new(identity.ResourceRoot, identity.DlcRoots);
        List<LoadedResource> resources = [];
        List<string> warnings = [];
        ApplicationError? resourceError = null;

        foreach ((string nation, _) in Nations)
        {
            await AddIfPresentAsync($"XML/item_defs/vehicles/{nation}/list.xml.dvpl")
                .ConfigureAwait(false);
            if (resourceError is not null)
            {
                return OperationResult.Failure<SourceManifest>(resourceError);
            }
        }

        await AddIfPresentAsync("maps.yaml.dvpl").ConfigureAwait(false);
        if (resourceError is not null)
        {
            return OperationResult.Failure<SourceManifest>(resourceError);
        }

        await AddIfPresentAsync("Strings/en.yaml.dvpl").ConfigureAwait(false);
        if (resourceError is not null)
        {
            return OperationResult.Failure<SourceManifest>(resourceError);
        }

        if (resources.Count == 0)
        {
            return OperationResult.Failure<SourceManifest>(
                new ApplicationError(
                    "game.metadata.resources_missing",
                    "No supported installed metadata resources were found."));
        }

        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hasher, ProviderVersion);
        Append(hasher, identity.ProductVersion);
        Append(hasher, identity.ExecutableSha256.Value);
        foreach (LoadedResource resource in resources.OrderBy(
                     item => item.Resource.RelativePath,
                     StringComparer.Ordinal))
        {
            Append(hasher, resource.Resource.RelativePath.Replace('\\', '/'));
            Append(hasher, resource.Resource.LayerId);
            Append(hasher, resource.Payload.SourceHash.Value);
        }

        ContentHash sourceSetHash = new(Convert.ToHexString(hasher.GetHashAndReset()));
        return OperationResult.Success(
            new SourceManifest(sourceSetHash, resources),
            warnings.ToArray());

        async ValueTask AddIfPresentAsync(string relativePath)
        {
            ResolvedGameResource? resource = overlay.Resolve(relativePath);
            if (resource is null)
            {
                warnings.Add($"Installed metadata resource '{relativePath}' is unavailable.");
                return;
            }

            OperationResult<DvplPayload> readResult =
                await _dvplReader.ReadAsync(resource.AbsolutePath, cancellationToken).ConfigureAwait(false);
            if (!readResult.IsSuccess)
            {
                resourceError = readResult.Error;
                return;
            }

            resources.Add(new LoadedResource(resource, readResult.Value!));
        }
    }

    private async ValueTask<OperationResult<MetadataSnapshot>> BuildSnapshotAsync(
        GameMetadataContext context,
        SourceManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> localization = LoadLocalization(manifest);
            ContentHash? localizationHash = manifest.Resources
                .FirstOrDefault(resource => ResourceEquals(resource, "Strings/en.yaml.dvpl"))
                ?.Payload.SourceHash;

            Dictionary<int, VehicleMetadata> vehicles = [];
            foreach ((string nation, int nationId) in Nations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = $"XML/item_defs/vehicles/{nation}/list.xml.dvpl";
                LoadedResource? listResource = manifest.Resources.FirstOrDefault(
                    resource => ResourceEquals(resource, relativePath));
                if (listResource is null)
                {
                    continue;
                }

                IReadOnlyList<VehicleDefinition> definitions =
                    MetadataResourceParser.ParseVehicleList(
                        listResource.Payload.Data.Span,
                        nation,
                        nationId,
                        listResource.Payload.SourceHash,
                        _options.MaxMetadataCharacters);

                foreach (VehicleDefinition definition in definitions)
                {
                    string displayName = localization.TryGetValue(
                        definition.LocalizationKey,
                        out string? localized)
                        ? localized
                        : definition.VehicleId[(definition.VehicleId.IndexOf(':') + 1)..];

                    ContentHash sourceHash = CombineHashes(
                        definition.DefinitionHash,
                        localizationHash);
                    vehicles.TryAdd(
                        definition.CompactDescriptor,
                        new VehicleMetadata(
                            definition.CompactDescriptor,
                            definition.VehicleId,
                            displayName,
                            definition.TankClass,
                            definition.Nation,
                            context.Identity.ProductVersion,
                            sourceHash));
                }
            }

            Dictionary<string, MapMetadata> maps = new(StringComparer.OrdinalIgnoreCase);
            LoadedResource? mapsResource = manifest.Resources.FirstOrDefault(
                resource => ResourceEquals(resource, "maps.yaml.dvpl"));
            if (mapsResource is not null)
            {
                IReadOnlyList<MapDefinition> definitions = MetadataResourceParser.ParseMaps(
                    mapsResource.Payload.Data.Span,
                    mapsResource.Payload.SourceHash,
                    _options.MaxMetadataCharacters);
                foreach (MapDefinition definition in definitions)
                {
                    string localizationKey =
                        $"#maps:{definition.MapId}:{definition.LocalName ?? string.Empty}";
                    string displayName = localization.TryGetValue(localizationKey, out string? localized)
                        ? localized
                        : definition.MapId;
                    MapMetadata metadata = new(
                        definition.MapId,
                        displayName,
                        WorldMinX: null,
                        WorldMaxX: null,
                        WorldMinZ: null,
                        WorldMaxZ: null,
                        context.Identity.ProductVersion,
                        CombineHashes(definition.DefinitionHash, localizationHash),
                        definition.LocalName);

                    maps.TryAdd(definition.MapId, metadata);
                    if (definition.NumericId is int numericId)
                    {
                        maps.TryAdd(numericId.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata);
                    }
                }
            }

            return OperationResult.Success(
                new MetadataSnapshot(vehicles, maps),
                localization.Count == 0
                    ? ["English installed localization was unavailable; stable internal names are used."]
                    : []);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or XmlException or DecoderFallbackException or OverflowException)
        {
            _logger.LogWarning(
                new EventId(3110, "InstalledMetadataParseFailed"),
                "Installed metadata parsing failed ({ExceptionType}).",
                exception.GetType().Name);
            return OperationResult.Failure<MetadataSnapshot>(
                new ApplicationError(
                    "game.metadata.invalid_resource",
                    "An installed metadata resource is malformed or outside parser limits."));
        }
    }

    private IReadOnlyDictionary<string, string> LoadLocalization(SourceManifest manifest)
    {
        LoadedResource? resource = manifest.Resources.FirstOrDefault(
            item => ResourceEquals(item, "Strings/en.yaml.dvpl"));
        return resource is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : MetadataResourceParser.ParseLocalization(
                resource.Payload.Data.Span,
                _options.MaxMetadataCharacters);
    }

    private static bool ResourceEquals(LoadedResource resource, string relativePath) =>
        string.Equals(
            resource.Resource.RelativePath.Replace('\\', '/'),
            relativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IdentityMatches(
        InstalledGameIdentity expected,
        InstalledGameIdentity current)
    {
        try
        {
            return string.Equals(
                       Path.GetFullPath(expected.ExecutablePath),
                       Path.GetFullPath(current.ExecutablePath),
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       expected.ProductVersion,
                       current.ProductVersion,
                       StringComparison.OrdinalIgnoreCase) &&
                   expected.ExecutableSha256 == current.ExecutableSha256 &&
                   string.Equals(
                       Path.GetFullPath(expected.ResourceRoot),
                       Path.GetFullPath(current.ResourceRoot),
                       StringComparison.OrdinalIgnoreCase) &&
                   expected.DlcRoots.Count == current.DlcRoots.Count &&
                   expected.DlcRoots.Zip(current.DlcRoots).All(
                       pair => string.Equals(
                           Path.GetFullPath(pair.First),
                           Path.GetFullPath(pair.Second),
                           StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static ContentHash CombineHashes(ContentHash first, ContentHash? second)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hasher, first.Value);
        if (second is not null)
        {
            Append(hasher, second.Value);
        }

        return new ContentHash(Convert.ToHexString(hasher.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private sealed record LoadedResource(
        ResolvedGameResource Resource,
        DvplPayload Payload);

    private sealed record SourceManifest(
        ContentHash SourceSetHash,
        IReadOnlyList<LoadedResource> Resources);

    private sealed record MetadataSnapshot(
        IReadOnlyDictionary<int, VehicleMetadata> Vehicles,
        IReadOnlyDictionary<string, MapMetadata> Maps);

}
