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
    private readonly Dictionary<string, string?> _stockShellByTankId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ShellProfile>> _shellsByNation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, GunShellProfile>> _gunsByNation =
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
        }

        if (armorByEntity.Count == 0)
        {
            return null;
        }

        // The viewer's stock shell: the viewpoint participant's default shell
        // (loaded shell is not decodable today). A failure omits the badge.
        Participant? viewer = ResolveViewer(projection);
        ShellSpec? viewerShell = viewer is null
            ? null
            : await ResolveViewerShellAsync(identity, viewer, cancellationToken).ConfigureAwait(false);
        if (viewerShell is null)
        {
            return null;
        }

        return new PenetrationContext(armorByEntity, viewerShell.Value);
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

        VehicleArmorProfile profile = PenetrationDataParser.ParseVehicleArmor(
            payload.Value.Span, $"{nation}:{tankId}", MaxCharacters);
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

    private async ValueTask<ShellSpec?> ResolveViewerShellAsync(
        InstalledGameIdentity identity,
        Participant viewer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewer.TankId))
        {
            return null;
        }

        string? nation = await ResolveNationAsync(identity, viewer.TankId, cancellationToken)
            .ConfigureAwait(false);
        if (nation is null)
        {
            return null;
        }

        string? shellName = await ResolveStockShellAsync(identity, nation, viewer.TankId, cancellationToken)
            .ConfigureAwait(false);
        if (shellName is null)
        {
            return null;
        }

        ShellProfile? shell = await ResolveShellAsync(identity, nation, shellName, cancellationToken)
            .ConfigureAwait(false);
        GunShellProfile? gun = await ResolveGunAsync(identity, nation, shellName, cancellationToken)
            .ConfigureAwait(false);
        if (shell is null || gun is null)
        {
            return null;
        }

        return ShellSpec.FromPiercingPower(
            gun.PiercingPowerNearMm,
            gun.PiercingPowerFarMm,
            gun.MaxDistanceMeters,
            shell.CaliberMm,
            shell.RicochetDegrees,
            shell.NormalizationDegrees);
    }

    private async ValueTask<string?> ResolveStockShellAsync(
        InstalledGameIdentity identity,
        string nation,
        string tankId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stockShellByTankId.TryGetValue(tankId, out string? cached))
            {
                return cached;
            }
        }

        ReadOnlyMemory<byte>? payload = await ReadDvplAsync(
            VehiclePath(identity, nation, tankId), cancellationToken).ConfigureAwait(false);
        string? shellName = payload is null
            ? null
            : PenetrationDataParser.ParseStockGunShellName(payload.Value.Span, MaxCharacters);

        lock (_gate)
        {
            _stockShellByTankId[tankId] = shellName;
        }

        return shellName;
    }

    private async ValueTask<ShellProfile?> ResolveShellAsync(
        InstalledGameIdentity identity,
        string nation,
        string shellName,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ShellProfile> shells = await GetShellsAsync(identity, nation, cancellationToken)
            .ConfigureAwait(false);
        return shells.TryGetValue(shellName, out ShellProfile? shell) ? shell : null;
    }

    private async ValueTask<GunShellProfile?> ResolveGunAsync(
        InstalledGameIdentity identity,
        string nation,
        string shellName,
        CancellationToken cancellationToken)
    {
        Dictionary<string, GunShellProfile> guns = await GetGunsAsync(identity, nation, cancellationToken)
            .ConfigureAwait(false);
        return guns.TryGetValue(shellName, out GunShellProfile? gun) ? gun : null;
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
            foreach (ShellProfile shell in PenetrationDataParser.ParseShells(payload.Value.Span, MaxCharacters))
            {
                shells.TryAdd(shell.Name, shell);
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
            foreach (GunShellProfile gun in PenetrationDataParser.ParseGuns(payload.Value.Span, MaxCharacters))
            {
                // A shell belongs to one gun's shot list; keep the first.
                guns.TryAdd(gun.ShellName, gun);
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

    private void ClearCaches()
    {
        _nationByTankId.Clear();
        _armorByTankId.Clear();
        _stockShellByTankId.Clear();
        _shellsByNation.Clear();
        _gunsByNation.Clear();
    }
}
