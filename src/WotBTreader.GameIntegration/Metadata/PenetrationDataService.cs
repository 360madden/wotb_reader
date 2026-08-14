using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Dvpl;

namespace WotBTreader.GameIntegration.Metadata;

/// <summary>
/// Supplies the overlay pen badge's install-static inputs (per-tank nominal
/// armor + the viewpoint tank's stock shell) by reading the installed game's
/// DVPL/XML resources read-only. Every lookup is cached per installed-game
/// identity (the executable SHA-256) and re-validated on identity change.
///
/// Honest limits (recorded, never hidden): the armor XML declares the FRONT
/// face via <c>primaryArmor</c> but not the side/rear mapping, so only the
/// front is nominal (side/rear are 0 = unknown and the badge resolver fails
/// them closed). The viewer shell is its STOCK gun's first shell — the loaded
/// shell is not decodable today (see the pen design doc). Any resolution
/// failure yields null so the frame omits the badge rather than fabricating
/// a verdict.
/// </summary>
public sealed class PenetrationDataService : IOverlayPenetrationData
{
    private const long MaxCharacters = 8 * 1024 * 1024;
    private const long MaxMeshBytes = 16 * 1024 * 1024;

    private static readonly string[] Nations =
    [
        "ussr", "germany", "usa", "china", "france",
        "uk", "japan", "european", "other",
    ];

    private readonly IGameInstallationDiscovery _discovery;
    private readonly IDvplReader _dvplReader;
    private readonly IInstalledGameMetadataProvider _metadataProvider;
    private readonly ILogger<PenetrationDataService> _logger;
    private readonly object _gate = new();

    private InstalledGameIdentity? _identity;
    private readonly Dictionary<string, (string Nation, string Tank)?> _locationByTankId =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TankArmor> _armorByTankId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>?> _gunShotsByTankId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ShellProfile>> _shellsByNation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, GunShellProfile>> _gunsByNation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<CollisionMeshPart>?> _meshByTankId =
        new(StringComparer.Ordinal);

    public PenetrationDataService(
        IGameInstallationDiscovery discovery,
        IDvplReader dvplReader,
        IInstalledGameMetadataProvider metadataProvider,
        ILogger<PenetrationDataService> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(dvplReader);
        ArgumentNullException.ThrowIfNull(metadataProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _dvplReader = dvplReader;
        _metadataProvider = metadataProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PenetrationContext?> ResolveAsync(
        ReplayDecodeProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);

        InstalledGameIdentity? identity =
            await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        Dictionary<long, TankArmor> armorByEntity = [];
        Dictionary<long, IReadOnlyList<CollisionMeshPart>> meshesByEntity = [];
        foreach (Participant participant in projection.Participants)
        {
            if (participant.EntityId is not { } entityId || string.IsNullOrWhiteSpace(participant.TankId))
            {
                continue;
            }

            // The decoded TankId is the enrichment's `nation:tank` VehicleId
            // (e.g. `germany:PzIV`) or a raw compact descriptor when the
            // enrichment missed. Both forms resolve to the install's
            // nation + bare tank file name.
            (string Nation, string Tank)? location =
                await ResolveTankLocationAsync(identity, participant.TankId, cancellationToken)
                    .ConfigureAwait(false);
            if (location is not { } resolved)
            {
                continue;
            }

            TankArmor? armor = await ResolveArmorAsync(identity, resolved.Nation, resolved.Tank, cancellationToken)
                .ConfigureAwait(false);
            if (armor is { } resolvedArmor)
            {
                armorByEntity[entityId] = resolvedArmor;
            }

            // Best-effort collision meshes: when present, the badge uses their
            // true surface normals; when absent it falls back to the box model.
            IReadOnlyList<CollisionMeshPart>? mesh = await ResolveMeshAsync(
                identity, resolved.Nation, resolved.Tank, cancellationToken).ConfigureAwait(false);
            if (mesh is not null && mesh.Count > 0)
            {
                meshesByEntity[entityId] = mesh;
            }
        }

        if (armorByEntity.Count == 0)
        {
            return null;
        }

        // The viewer's shells: the viewpoint participant's stock-gun shots
        // (the loaded shell is not decodable today, so every shot of the
        // stock gun is offered as a manual choice; the first is the default).
        // No shells omits the badge.
        Participant? viewer = ResolveViewer(projection);
        IReadOnlyList<ShellOption> shells = viewer is null
            ? []
            : await ResolveViewerShellsAsync(identity, viewer, cancellationToken).ConfigureAwait(false);
        if (shells.Count == 0)
        {
            return null;
        }

        return new PenetrationContext(
            armorByEntity,
            shells[0].Spec,
            meshesByEntity.Count > 0 ? meshesByEntity : null,
            shells);
    }

    /// <inheritdoc />
    public async ValueTask<PenetrationTankData?> ResolveTankAsync(
        string tankId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tankId))
        {
            return null;
        }

        InstalledGameIdentity? identity =
            await EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        (string Nation, string Tank)? location =
            await ResolveTankLocationAsync(identity, tankId, cancellationToken)
                .ConfigureAwait(false);
        if (location is not { } resolved)
        {
            return null;
        }

        TankArmor? armor = await ResolveArmorAsync(identity, resolved.Nation, resolved.Tank, cancellationToken)
            .ConfigureAwait(false);
        if (armor is not { } resolvedArmor)
        {
            return null;
        }

        IReadOnlyList<CollisionMeshPart>? mesh =
            await ResolveMeshAsync(identity, resolved.Nation, resolved.Tank, cancellationToken)
                .ConfigureAwait(false);

        IReadOnlyList<ShellOption> shells =
            await BuildShellOptionsAsync(identity, resolved.Nation, resolved.Tank, cancellationToken)
                .ConfigureAwait(false);
        if (shells.Count == 0)
        {
            return null;
        }

        return new PenetrationTankData(
            resolvedArmor,
            mesh is { Count: > 0 } ? mesh : null,
            shells);
    }

    private static Participant? ResolveViewer(ReplayDecodeProjection projection)
    {
        if (projection.Session?.ViewpointParticipantId is not { } viewpointId)
        {
            return null;
        }

        return projection.Participants.FirstOrDefault(
            participant => participant.Id == viewpointId);
    }

    /// <summary>
    /// Returns the cached installed-game identity, discovering it once and
    /// clearing the per-tank caches when the identity changes (a different
    /// install or version invalidates every cached profile).
    /// </summary>
    private async ValueTask<InstalledGameIdentity?> EnsureIdentityAsync(
        CancellationToken cancellationToken)
    {
        InstalledGameIdentity? identity =
            await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_identity is null
                || !string.Equals(_identity.ExecutableSha256.Value, identity.ExecutableSha256.Value, StringComparison.Ordinal)
                || !string.Equals(_identity.ResourceRoot, identity.ResourceRoot, StringComparison.OrdinalIgnoreCase))
            {
                ClearCaches();
                _identity = identity;
            }
        }

        return identity;
    }

    private async ValueTask<InstalledGameIdentity?> GetIdentityAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_identity is not null)
            {
                return _identity;
            }
        }

        OperationResult<InstalledGameIdentity> result =
            await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Penetration data unavailable: {Code}",
                    result.Error?.Code ?? "discovery.failed");
            }

            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Resolves a decoded TankId to the install's (nation, bare tank file
    /// name) for all three forms the store carries: the enrichment's
    /// <c>nation:tank</c> VehicleId (nation verified by the vehicle file's
    /// existence), a raw integer compact descriptor (resolved through the
    /// installed-game metadata index, which owns the descriptor→VehicleId
    /// table), and a bare tank name (scanned across the known nations).
    /// Fail-closed: null when no form resolves. Cached per input string.
    /// </summary>
    private async ValueTask<(string Nation, string Tank)?> ResolveTankLocationAsync(
        InstalledGameIdentity identity,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_locationByTankId.TryGetValue(tankId, out (string, string)? cached))
            {
                return cached;
            }
        }

        (string? explicitNation, string bareTank) = SplitTankId(tankId);
        (string Nation, string Tank)? location;
        if (explicitNation is not null)
        {
            // The enrichment's `nation:tank` VehicleId names the nation
            // explicitly; verify the file exists (fail-closed) and use it.
            location = File.Exists(VehiclePath(identity, explicitNation, bareTank))
                ? (explicitNation, bareTank)
                : null;
        }
        else if (int.TryParse(bareTank, out int descriptor))
        {
            // A raw compact descriptor (the decode-time enrichment missed):
            // resolve it through the installed-game metadata index, which
            // owns the descriptor → `nation:tank` table.
            location = await ResolveDescriptorLocationAsync(identity, descriptor, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            location = null;
            foreach (string candidate in Nations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(VehiclePath(identity, candidate, bareTank)))
                {
                    location = (candidate, bareTank);
                    break;
                }
            }
        }

        lock (_gate)
        {
            _locationByTankId[tankId] = location;
        }

        return location;
    }

    /// <summary>
    /// Resolves a raw vehicle compact descriptor to its (nation, tank file
    /// name) through the installed-game metadata index. The provider probes
    /// and caches its own descriptor snapshot per executable identity, so the
    /// per-tank probe here is cheap after the first resolution.
    /// </summary>
    private async ValueTask<(string Nation, string Tank)?> ResolveDescriptorLocationAsync(
        InstalledGameIdentity identity,
        int descriptor,
        CancellationToken cancellationToken)
    {
        OperationResult<GameMetadataContext> contextResult =
            await _metadataProvider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!contextResult.IsSuccess || contextResult.Value is null)
        {
            return null;
        }

        OperationResult<VehicleMetadata> vehicleResult = await _metadataProvider
            .ResolveVehicleAsync(contextResult.Value, descriptor, cancellationToken)
            .ConfigureAwait(false);
        if (!vehicleResult.IsSuccess || vehicleResult.Value is null)
        {
            return null;
        }

        (string? nation, string tank) = SplitTankId(vehicleResult.Value.VehicleId);
        return nation is null ? null : (nation, tank);
    }

    /// <summary>
    /// Splits a tank id that carries an explicit nation prefix (the
    /// enrichment's <c>nation:tank</c> VehicleId form, e.g.
    /// <c>germany:PzIV</c>) into its nation and bare tank file name. A bare
    /// name (no colon) yields a null nation and the id unchanged.
    /// </summary>
    private static (string? Nation, string Tank) SplitTankId(string tankId)
    {
        int colon = tankId.IndexOf(':');
        if (colon > 0 && colon < tankId.Length - 1)
        {
            return (tankId[..colon], tankId[(colon + 1)..]);
        }

        return (null, tankId);
    }

    private async ValueTask<IReadOnlyList<CollisionMeshPart>?> ResolveMeshAsync(
        InstalledGameIdentity identity,
        string nation,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_meshByTankId.TryGetValue(tankId, out IReadOnlyList<CollisionMeshPart>? cached))
            {
                return cached;
            }
        }

        IReadOnlyList<CollisionMeshPart>? mesh = null;
        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            CollisionMeshPath(identity, nation, tankId), cancellationToken).ConfigureAwait(false);
        if (payload is not null)
        {
            try
            {
                mesh = CollisionMeshParser.ParseAll(payload.Value.Span, MaxMeshBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                // A corrupt mesh omits the badge, never an exception through
                // the frame path (the fail-closed contract).
                mesh = null;
            }
        }

        lock (_gate)
        {
            _meshByTankId[tankId] = mesh;
        }

        return mesh;
    }

    private async ValueTask<TankArmor?> ResolveArmorAsync(
        InstalledGameIdentity identity,
        string nation,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_armorByTankId.TryGetValue(tankId, out TankArmor cached))
            {
                return cached;
            }
        }

        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            VehiclePath(identity, nation, tankId), cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        VehicleArmorProfile profile;
        try
        {
            profile = PenetrationDataParser.ParseVehicleArmor(
                payload.Value.Span, $"{nation}:{tankId}", MaxCharacters);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or DecoderFallbackException)
        {
            // A corrupt or oversized vehicle XML omits the badge, never an
            // exception through the frame path (the fail-closed contract).
            return null;
        }

        TankArmor armor = PenetrationContext.NominalArmor(profile);
        if (armor.FrontMm <= 0)
        {
            // No declared primary (frontal) armor — nothing honest to show.
            return null;
        }

        lock (_gate)
        {
            _armorByTankId[tankId] = armor;
        }

        return armor;
    }

    private async ValueTask<IReadOnlyList<ShellOption>> ResolveViewerShellsAsync(
        InstalledGameIdentity identity,
        Participant viewer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewer.TankId))
        {
            return [];
        }

        (string Nation, string Tank)? location =
            await ResolveTankLocationAsync(identity, viewer.TankId, cancellationToken)
                .ConfigureAwait(false);
        if (location is not { } resolved)
        {
            return [];
        }

        return await BuildShellOptionsAsync(identity, resolved.Nation, resolved.Tank, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the stock gun's shell options for one tank: the vehicle XML's
    /// shot names joined against the nation's shells.xml (per-shell physics)
    /// and guns.xml (per-shot piercing profile). Shared by the viewer-shell
    /// lane (<see cref="ResolveAsync"/>) and the per-tank lane
    /// (<see cref="ResolveTankAsync"/>).
    /// </summary>
    private async ValueTask<IReadOnlyList<ShellOption>> BuildShellOptionsAsync(
        InstalledGameIdentity identity,
        string nation,
        string tankId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> shotNames = await ResolveStockGunShotsAsync(
            identity, nation, tankId, cancellationToken).ConfigureAwait(false);
        if (shotNames.Count == 0)
        {
            return [];
        }

        Dictionary<string, ShellProfile> shells = await GetShellsAsync(identity, nation, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, GunShellProfile> guns = await GetGunsAsync(identity, nation, cancellationToken)
            .ConfigureAwait(false);

        List<ShellOption> options = [];
        foreach (string shellName in shotNames)
        {
            if (!shells.TryGetValue(shellName, out ShellProfile? shell)
                || !guns.TryGetValue(shellName, out GunShellProfile? gun))
            {
                continue;
            }

            ShellKind kind = ShellKinds.FromInstallName(shell.Kind);
            options.Add(new ShellOption(
                shellName,
                kind,
                ShellSpec.FromPiercingPower(
                    gun.PiercingPowerNearMm,
                    gun.PiercingPowerFarMm,
                    gun.MaxDistanceMeters,
                    shell.CaliberMm,
                    shell.RicochetDegrees,
                    shell.NormalizationDegrees,
                    kind)));
        }

        return options;
    }

    private async ValueTask<IReadOnlyList<string>> ResolveStockGunShotsAsync(
        InstalledGameIdentity identity,
        string nation,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_gunShotsByTankId.TryGetValue(tankId, out IReadOnlyList<string>? cached))
            {
                return cached ?? [];
            }
        }

        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            VehiclePath(identity, nation, tankId), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> shotNames = [];
        if (payload is not null)
        {
            try
            {
                shotNames = PenetrationDataParser.ParseGunShotNames(
                    payload.Value.Span, MaxCharacters);
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or DecoderFallbackException)
            {
                shotNames = [];
            }
        }

        lock (_gate)
        {
            _gunShotsByTankId[tankId] = shotNames;
        }

        return shotNames;
    }

    private async ValueTask<Dictionary<string, ShellProfile>> GetShellsAsync(
        InstalledGameIdentity identity,
        string nation,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_shellsByNation.TryGetValue(nation, out Dictionary<string, ShellProfile>? cached))
            {
                return cached;
            }
        }

        Dictionary<string, ShellProfile> shells = new(StringComparer.Ordinal);
        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            ComponentPath(identity, nation, "shells"), cancellationToken).ConfigureAwait(false);
        if (payload is not null)
        {
            try
            {
                foreach (ShellProfile shell in PenetrationDataParser.ParseShells(payload.Value.Span, MaxCharacters))
                {
                    shells.TryAdd(shell.Name, shell);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or DecoderFallbackException)
            {
                // A corrupt shells.xml yields an empty table; the viewer-shell
                // lookup fails closed rather than throwing.
            }
        }

        lock (_gate)
        {
            _shellsByNation[nation] = shells;
        }

        return shells;
    }

    private async ValueTask<Dictionary<string, GunShellProfile>> GetGunsAsync(
        InstalledGameIdentity identity,
        string nation,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_gunsByNation.TryGetValue(nation, out Dictionary<string, GunShellProfile>? cached))
            {
                return cached;
            }
        }

        Dictionary<string, GunShellProfile> guns = new(StringComparer.Ordinal);
        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            ComponentPath(identity, nation, "guns"), cancellationToken).ConfigureAwait(false);
        if (payload is not null)
        {
            try
            {
                foreach (GunShellProfile gun in PenetrationDataParser.ParseGuns(payload.Value.Span, MaxCharacters))
                {
                    // A shell belongs to one gun's shot list; keep the first.
                    guns.TryAdd(gun.ShellName, gun);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException or DecoderFallbackException)
            {
                // A corrupt guns.xml yields an empty table; the viewer-shell
                // lookup fails closed rather than throwing.
            }
        }

        lock (_gate)
        {
            _gunsByNation[nation] = guns;
        }

        return guns;
    }

    private async ValueTask<ReadOnlyMemory<byte>?> ReadDvplAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        OperationResult<DvplPayload> result =
            await _dvplReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is not null
            ? result.Value.Data
            : null;
    }

    private static string VehiclePath(InstalledGameIdentity identity, string nation, string tankId) =>
        Path.Combine(identity.ResourceRoot, "XML", "item_defs", "vehicles", nation, $"{tankId}.xml.dvpl");

    private static string ComponentPath(InstalledGameIdentity identity, string nation, string kind) =>
        Path.Combine(identity.ResourceRoot, "XML", "item_defs", "vehicles", nation, "components", $"{kind}.xml.dvpl");

    private static string CollisionMeshPath(InstalledGameIdentity identity, string nation, string tankId) =>
        Path.Combine(identity.ResourceRoot, "3d", "Tanks", "CollisionMeshes", $"{nation}-{tankId}.scg.dvpl");

    private void ClearCaches()
    {
        _locationByTankId.Clear();
        _armorByTankId.Clear();
        _gunShotsByTankId.Clear();
        _shellsByNation.Clear();
        _gunsByNation.Clear();
        _meshByTankId.Clear();
    }
}
