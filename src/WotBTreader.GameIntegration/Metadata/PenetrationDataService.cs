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
    private readonly ILogger<PenetrationDataService> _logger;
    private readonly object _gate = new();

    private InstalledGameIdentity? _identity;
    private readonly Dictionary<string, string?> _nationByTankId = new(StringComparer.Ordinal);
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
        ILogger<PenetrationDataService> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(dvplReader);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _dvplReader = dvplReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<PenetrationContext?> ResolveAsync(
        ReplayDecodeProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);

        InstalledGameIdentity? identity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
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

        Dictionary<long, TankArmor> armorByEntity = [];
        Dictionary<long, IReadOnlyList<CollisionMeshPart>> meshesByEntity = [];
        foreach (Participant participant in projection.Participants)
        {
            if (participant.EntityId is not { } entityId || string.IsNullOrWhiteSpace(participant.TankId))
            {
                continue;
            }

            string? nation = await ResolveNationAsync(identity, participant.TankId, cancellationToken)
                .ConfigureAwait(false);
            if (nation is null)
            {
                continue;
            }

            TankArmor? armor = await ResolveArmorAsync(identity, nation, participant.TankId, cancellationToken)
                .ConfigureAwait(false);
            if (armor is { } resolvedArmor)
            {
                armorByEntity[entityId] = resolvedArmor;
            }

            // Best-effort collision meshes: when present, the badge uses their
            // true surface normals; when absent it falls back to the box model.
            IReadOnlyList<CollisionMeshPart>? mesh = await ResolveMeshAsync(
                identity, nation, participant.TankId, cancellationToken).ConfigureAwait(false);
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

    private static Participant? ResolveViewer(ReplayDecodeProjection projection)
    {
        if (projection.Session?.ViewpointParticipantId is not { } viewpointId)
        {
            return null;
        }

        return projection.Participants.FirstOrDefault(
            participant => participant.Id == viewpointId);
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

    private async ValueTask<string?> ResolveNationAsync(
        InstalledGameIdentity identity,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_nationByTankId.TryGetValue(tankId, out string? cached))
            {
                return cached;
            }
        }

        string? nation = null;
        foreach (string candidate in Nations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = VehiclePath(identity, candidate, tankId);
            if (File.Exists(path))
            {
                nation = candidate;
                break;
            }
        }

        lock (_gate)
        {
            _nationByTankId[tankId] = nation;
        }

        return nation;
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

        string? nation = await ResolveNationAsync(identity, viewer.TankId, cancellationToken)
            .ConfigureAwait(false);
        if (nation is null)
        {
            return [];
        }

        IReadOnlyList<string> shotNames = await ResolveStockGunShotsAsync(
            identity, nation, viewer.TankId, cancellationToken).ConfigureAwait(false);
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
        _nationByTankId.Clear();
        _armorByTankId.Clear();
        _gunShotsByTankId.Clear();
        _shellsByNation.Clear();
        _gunsByNation.Clear();
        _meshByTankId.Clear();
    }
}
