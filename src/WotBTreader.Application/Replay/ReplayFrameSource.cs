using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Replay;

/// <summary>
/// Builds overlay frames from a decoded replay projection
/// (<see cref="ISessionQueryRepository.GetProjectionAsync"/>): the viewpoint
/// entity is the camera, every participant with position evidence becomes a
/// tank, and HP/alive come from the canonical damage/destroyed events.
/// Nearest-sample lookups fail closed — a frame time outside an entity's
/// sample span omits that entity rather than fabricating an endpoint value.
/// One projection load per call; a host that renders continuously should
/// cache the projection and call the frame builder directly.
/// </summary>
public sealed class ReplayFrameSource : IOverlayFrameSource
{
    /// <summary>Replay-time window (seconds) for the event-feed pips: an
    /// event older than this is no longer "live" on the HUD.</summary>
    internal static readonly TimeSpan PipWindow = TimeSpan.FromSeconds(2);

    private readonly ISessionQueryRepository _sessions;
    private readonly IProjectionCache? _cache;
    private readonly IOverlayPenetrationData? _penetration;

    public ReplayFrameSource(ISessionQueryRepository sessions)
        : this(sessions, cache: null, penetration: null)
    {
    }

    public ReplayFrameSource(
        ISessionQueryRepository sessions,
        IProjectionCache? cache,
        IOverlayPenetrationData? penetration = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
        _cache = cache;
        _penetration = penetration;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<OverlayFrame>> GetFrameAsync(
        BattleSessionId sessionId,
        TimeSpan replayTime,
        CancellationToken cancellationToken,
        OverlayCamera? cameraOverride = null,
        string? shellName = null)
    {
        // The projection is immutable per session (every decode run creates a
        // fresh session id), so a cache hit skips re-reading every position /
        // event / raw record from storage — the dominant single-frame cost.
        ReplayDecodeProjection? cached = null;
        if (_cache is not null && _cache.TryGet(sessionId, out ReplayDecodeProjection? hit))
        {
            cached = hit;
        }

        OperationResult<ReplayDecodeProjection> projectionResult = cached is not null
            ? OperationResult.Success(cached)
            : await _sessions.GetProjectionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        if (!projectionResult.IsSuccess || projectionResult.Value is null)
        {
            return OperationResult.Failure<OverlayFrame>(projectionResult.Error!);
        }

        ReplayDecodeProjection projection = projectionResult.Value;
        if (projection.Session is null)
        {
            return OperationResult.Failure<OverlayFrame>(
                new ApplicationError(
                    "overlay.session.missing",
                    $"Battle session '{sessionId}' has no session record."));
        }

        _cache?.Store(sessionId, projection);

        // Penetration badge: install-derived armor + shell, resolved only
        // when a data source is wired. A null context (or an absent source)
        // omits the badge — never a fabricated verdict.
        PenetrationContext? penetration = null;
        if (_penetration is not null)
        {
            penetration = await _penetration
                .ResolveAsync(projection, cancellationToken)
                .ConfigureAwait(false);
        }

        return OperationResult.Success(
            BuildFrame(projection, replayTime, cameraOverride, penetration, shellName));
    }

    internal static OverlayFrame BuildFrame(
        ReplayDecodeProjection projection,
        TimeSpan replayTime,
        OverlayCamera? cameraOverride = null,
        PenetrationContext? penetration = null,
        string? shellName = null)
    {
        // Per-entity nearest-sample lookup over the decoded position stream.
        Dictionary<long, List<PositionSample>> byEntity = projection.Positions
            .Where(position => position.EntityId is not null)
            .GroupBy(position => position.EntityId!.Value)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(position => position.ReplayTime)
                .ToList());

        // Roster: entity id -> participant (first match; one entity per tank).
        Dictionary<long, Participant> roster = projection.Participants
            .Where(participant => participant.EntityId is not null)
            .GroupBy(participant => participant.EntityId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        // HP arc: cumulative damage and destroyed flags per victim entity.
        Dictionary<long, long> totalDamage = [];
        Dictionary<long, TimeSpan> destroyedAt = [];
        // Exact max health per entity from the type-5 spawn broadcast (first
        // broadcast per roster entity = max HP, verified on both replays).
        Dictionary<long, long> maxHealthByEntity = [];
        // Cumulative damage each entity has DEALT up to the frame time (the
        // scoreboard's damage-dealt column). One pass, like the HP arc.
        Dictionary<long, long> damageDealt = [];
        foreach (CanonicalEvent canonical in projection.Events)
        {
            if (canonical.EntityId is null || canonical.EntityId <= 0)
            {
                continue;
            }

            long entityId = canonical.EntityId.Value;
            switch (canonical.Kind)
            {
                case CanonicalEventKind.Damage when TryParseDamage(canonical.ValuesJson, out int damage):
                    totalDamage[entityId] = totalDamage.GetValueOrDefault(entityId) + damage;
                    if (canonical.ReplayTime <= replayTime
                        && TryParseAttacker(canonical.ValuesJson, out long attacker))
                    {
                        damageDealt[attacker] = damageDealt.GetValueOrDefault(attacker) + damage;
                    }

                    break;
                case CanonicalEventKind.MaxHealthObserved when TryParseMaxHealth(
                    canonical.ValuesJson, out long maxHealth):
                    maxHealthByEntity.TryAdd(entityId, maxHealth);
                    break;
                case CanonicalEventKind.Destroyed:
                    destroyedAt.TryAdd(entityId, canonical.ReplayTime);
                    break;
            }
        }

        // Camera: the verified memory camera when provided (the CAM-001
        // seam), else the viewpoint participant's entity (replay default).
        OverlayCamera camera = BuildCamera(projection, byEntity, replayTime, cameraOverride);

        // Event-feed pips: damage hits and destructions in the recent window.
        // The window is short so the HUD only shows the live feed; an event
        // at the frame time itself is the current tick and counts.
        List<OverlayEventPip> pips = BuildPips(projection, replayTime);

        // Kill log + per-killer counts for the scoreboard. Built once here so
        // the tank loop and the frame share the same attribution.
        List<OverlayKill> kills = BuildKills(projection, replayTime);
        Dictionary<long, long> killsByKiller = [];
        foreach (OverlayKill kill in kills)
        {
            if (kill.KillerEntityId is long killer)
            {
                killsByKiller[killer] = killsByKiller.GetValueOrDefault(killer) + 1;
            }
        }

        List<OverlayTankState> tanks = [];
        foreach ((long entityId, List<PositionSample> samples) in byEntity)
        {
            // Only roster entities are tanks. The position stream also carries
            // non-participant entities (a duplicate "self" stream, projectiles,
            // debris) that must not render as nameplates.
            if (!roster.TryGetValue(entityId, out Participant? participant))
            {
                continue;
            }

            PositionSample? nearest = FindAtOrBefore(samples, replayTime);
            if (nearest is null)
            {
                // No position evidence at or before the frame time: omit.
                continue;
            }
            long received = totalDamage.GetValueOrDefault(entityId);
            long damageTaken = CumulativeDamageBefore(projection.Events, entityId, replayTime);
            bool alive = !destroyedAt.TryGetValue(entityId, out TimeSpan destroyed)
                || destroyed > replayTime;
            long maxHealth = maxHealthByEntity.GetValueOrDefault(entityId);
            // Exact health fraction when the type-5 max HP decoded for this
            // tank (1 − taken/max); otherwise fall back to the observed
            // damage arc (1.0 when the tank took no damage). A destroyed tank
            // ends at 0 because the ledger credits its remaining HP at the
            // destroy marker, so taken reaches max.
            double hpFraction = maxHealth > 0
                ? Math.Clamp(1.0 - (double)damageTaken / maxHealth, 0.0, 1.0)
                : received <= 0
                    ? 1.0
                    : Math.Clamp(1.0 - (double)damageTaken / received, 0.0, 1.0);
            long currentHealth = maxHealth > 0
                ? Math.Max(maxHealth - damageTaken, 0)
                : 0;

            // Distance from the camera position.
            double distance = Math.Sqrt(
                (nearest.RawX - camera.X) * (nearest.RawX - camera.X)
                + (nearest.RawY - camera.Y) * (nearest.RawY - camera.Y)
                + (nearest.RawZ - camera.Z) * (nearest.RawZ - camera.Z));

            tanks.Add(new OverlayTankState(
                entityId,
                nearest.RawX,
                nearest.RawY,
                nearest.RawZ,
                nearest.Yaw,
                hpFraction,
                alive,
                participant?.TeamNumber,
                participant?.PlayerName,
                participant?.ClanTag,
                participant?.TankName,
                participant?.TankClass.ToString(),
                distance,
                damageDealt.GetValueOrDefault(entityId),
                damageTaken,
                killsByKiller.GetValueOrDefault(entityId),
                maxHealth,
                currentHealth));
        }

        tanks.Sort(static (left, right) => left.DistanceMeters.CompareTo(right.DistanceMeters));

        // Pen badge: the camera aim (replay == turret aim) scored against the
        // aimed tank's nominal armor with the selected viewer shell. Computed
        // after the camera and tanks exist; fail-closed to null when the data
        // is absent or the aim/face cannot be resolved. The viewpoint's own
        // tank is excluded from aim targets — its hull sits at the camera
        // origin and is never a penetration target.
        PenetrationBadge? penBadge = null;
        IReadOnlyList<ShellOption> penShells = [];
        string? penShell = null;
        if (penetration is not null)
        {
            penShells = penetration.AvailableShells;
            (ShellSpec viewerShell, string? activeShell) = penetration.SelectShell(shellName);
            penShell = activeShell;

            Participant? viewpoint = projection.Session?.ViewpointParticipantId is { } viewpointId
                ? projection.Participants.FirstOrDefault(
                    participant => participant.Id == viewpointId)
                : null;
            long? ownEntityId = viewpoint?.EntityId;
            int? ownTeam = viewpoint?.TeamNumber;
            // Own tank excluded; enemies only when the own team is known.
            // A tank whose team is unknown stays eligible (fail-open toward
            // showing — never hide a real enemy behind a decode gap).
            IReadOnlyList<OverlayTankState> aimTargets = tanks
                .Where(tank => ownEntityId is not { } ownId || tank.EntityId != ownId)
                .Where(tank => ownTeam is not { } team || tank.TeamNumber != team)
                .ToList();
            penBadge = PenetrationAim.ResolveBadge(
                camera,
                aimTargets,
                penetration.ArmorByEntity,
                viewerShell,
                meshesByEntity: penetration.MeshesByEntity);
        }

        return new OverlayFrame(
            replayTime, camera, tanks, pips, kills, penBadge, penShells, penShell);
    }

    /// <summary>
    /// Builds the kill feed: every destroy that has landed at or before the
    /// frame time, with killer attribution from the last damage event the
    /// victim received (allowing the small posthumous window observed on real
    /// replays, e.g. 3760571's kill hit ~1.7 s after its destroy marker). The
    /// killer is null when no damage evidence exists (environmental kill).
    /// Kills are ordered oldest first; the HUD renders newest first.
    /// </summary>
    private static List<OverlayKill> BuildKills(
        ReplayDecodeProjection projection,
        TimeSpan replayTime)
    {
        const double posthumousWindowSeconds = 3.0;
        List<CanonicalEvent> damageEvents = projection.Events
            .Where(ev => ev.Kind == CanonicalEventKind.Damage)
            .ToList();

        List<OverlayKill> kills = [];
        foreach (CanonicalEvent destroyed in projection.Events
                     .Where(ev => ev.Kind == CanonicalEventKind.Destroyed)
                     .Where(ev => ev.ReplayTime <= replayTime)
                     .OrderBy(ev => ev.ReplayTime))
        {
            if (destroyed.EntityId is null)
            {
                continue;
            }

            long victim = destroyed.EntityId.Value;
            // Most recent damage the victim received at or just after its
            // destroy instant: that attacker is the killer.
            CanonicalEvent? killShot = damageEvents
                .Where(ev => ev.EntityId == victim
                    && ev.ReplayTime <= destroyed.ReplayTime
                        + TimeSpan.FromSeconds(posthumousWindowSeconds))
                .OrderByDescending(ev => ev.ReplayTime)
                .FirstOrDefault();

            long? killer = null;
            if (killShot is not null
                && TryParseAttacker(killShot.ValuesJson, out long attacker))
            {
                killer = attacker;
            }

            kills.Add(new OverlayKill(victim, killer, destroyed.ReplayTime));
        }

        return kills;
    }

    private static bool TryParseAttacker(string? valuesJson, out long attacker)
    {
        attacker = 0;
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(valuesJson);
            if (document.RootElement.TryGetProperty(
                    "attackerEntityId",
                    out System.Text.Json.JsonElement value)
                && value.TryGetInt64(out long parsed))
            {
                attacker = parsed;
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable values stay unknown — never guessed.
        }

        return false;
    }

    private static OverlayCamera BuildCamera(
        ReplayDecodeProjection projection,
        Dictionary<long, List<PositionSample>> byEntity,
        TimeSpan replayTime,
        OverlayCamera? cameraOverride = null)
    {
        // CAM-001 seam: a caller-supplied camera (e.g. the verified live
        // cameraState pose) replaces the viewpoint approximation. Fail-closed:
        // a pose with any non-finite component (position or rotation) is
        // never rendered — it falls through to the replay viewpoint fallback
        // below instead of fabricating a pose or blanking every projection.
        if (cameraOverride is { } overrideCamera
            && double.IsFinite(overrideCamera.X)
            && double.IsFinite(overrideCamera.Y)
            && double.IsFinite(overrideCamera.Z)
            && overrideCamera.YawRadians is double finiteYaw
            && double.IsFinite(finiteYaw)
            && overrideCamera.PitchRadians is double finitePitch
            && double.IsFinite(finitePitch))
        {
            return overrideCamera;
        }

        Participant? viewpoint = projection.Session?.ViewpointParticipantId is null
            ? null
            : projection.Participants.FirstOrDefault(
                participant => participant.Id == projection.Session!.ViewpointParticipantId);
        long? entityId = viewpoint?.EntityId;
        if (entityId is null || !byEntity.TryGetValue(entityId.Value, out List<PositionSample>? samples))
        {
            // No viewpoint evidence: a camera at the origin with no rotation.
            return new OverlayCamera(0, 0, 0, null, null, null);
        }

        PositionSample? nearest = FindAtOrBefore(samples, replayTime);
        return nearest is null
            ? new OverlayCamera(0, 0, 0, null, null, null)
            : new OverlayCamera(
                nearest.RawX,
                nearest.RawY,
                nearest.RawZ,
                nearest.Yaw,
                nearest.Pitch,
                nearest.Roll);
    }

    private static long CumulativeDamageBefore(
        IReadOnlyList<CanonicalEvent> events,
        long entityId,
        TimeSpan replayTime)
    {
        long sum = 0;
        foreach (CanonicalEvent canonical in events)
        {
            if (canonical.EntityId == entityId
                && canonical.Kind == CanonicalEventKind.Damage
                && canonical.ReplayTime <= replayTime
                && TryParseDamage(canonical.ValuesJson, out int damage))
            {
                sum += damage;
            }
        }

        return sum;
    }

    /// <summary>Collects damage/destroyed events from the trailing
    /// <see cref="PipWindow"/> of replay time — the live feed the HUD floats
    /// over the affected tank's nameplate.</summary>
    private static List<OverlayEventPip> BuildPips(
        ReplayDecodeProjection projection,
        TimeSpan replayTime)
    {
        List<OverlayEventPip> pips = [];
        foreach (CanonicalEvent canonical in projection.Events)
        {
            if (canonical.EntityId is null || canonical.EntityId <= 0
                || canonical.ReplayTime <= replayTime - PipWindow
                || canonical.ReplayTime > replayTime)
            {
                continue;
            }

            switch (canonical.Kind)
            {
                case CanonicalEventKind.Damage when TryParseDamage(canonical.ValuesJson, out int damage) && damage > 0:
                    pips.Add(new OverlayEventPip(
                        canonical.EntityId.Value, canonical.Kind, damage, canonical.ReplayTime));
                    break;
                case CanonicalEventKind.Destroyed:
                    pips.Add(new OverlayEventPip(
                        canonical.EntityId.Value, canonical.Kind, 0, canonical.ReplayTime));
                    break;
            }
        }

        return pips;
    }

    private static PositionSample? FindAtOrBefore(
        List<PositionSample> samples,
        TimeSpan replayTime)
    {
        int index = samples.BinarySearch(samples[0] with { ReplayTime = replayTime },
            PositionSampleTimeComparer.Instance);
        if (index >= 0)
        {
            return samples[index];
        }

        int insertion = ~index;
        if (insertion == 0)
        {
            // Before the first sample: no ground truth exists there.
            return null;
        }

        return samples[insertion - 1];
    }

    private static bool TryParseDamage(string? valuesJson, out int damage)
    {
        damage = 0;
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(valuesJson);
            if (document.RootElement.TryGetProperty("damage", out System.Text.Json.JsonElement value)
                && value.TryGetInt32(out int parsed))
            {
                damage = parsed;
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable values stay unknown — never guessed.
        }

        return false;
    }

    private static bool TryParseMaxHealth(string? valuesJson, out long maxHealth)
    {
        maxHealth = 0;
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(valuesJson);
            if (document.RootElement.TryGetProperty("maxHealth", out System.Text.Json.JsonElement value)
                && value.TryGetInt64(out long parsed))
            {
                maxHealth = parsed;
                return true;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable values stay unknown — never guessed.
        }

        return false;
    }

    private sealed class PositionSampleTimeComparer : IComparer<PositionSample>
    {
        internal static readonly PositionSampleTimeComparer Instance = new();

        public int Compare(PositionSample? left, PositionSample? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null ? 0 : (left is null ? -1 : 1);
            }

            return left.ReplayTime.CompareTo(right.ReplayTime);
        }
    }
}
