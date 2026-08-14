using System.Text.Json;
using WotBTreader.Application.Game;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Replay;

/// <summary>
/// One externally-supplied aim observation for the scorer's aim-override
/// seam: a replay-clock instant and the aim ray (camera/turret) at that
/// instant. The live CAM-013 capture supplies these at shot time so the
/// scorer validates the TRUE aim instead of the center-line proxy — the
/// decisive PN-4 aim source the replay stream itself cannot provide.
/// </summary>
public readonly record struct AimSample(TimeSpan ReplayTime, AimRay Aim);

/// <summary>
/// One decoded shot's scoring row: the roster identities (with tank names for
/// localization), the shell used, and either the scored model row or the
/// reason the shot was skipped (no attacker attribution, unresolvable tank
/// data, or no position samples at the shot time). Skipped shots never carry
/// a fabricated verdict.
/// </summary>
public sealed record OfflinePenShot(
    long Sequence,
    TimeSpan ReplayTime,
    long AttackerEntityId,
    long VictimEntityId,
    string? AttackerTankName,
    string? VictimTankName,
    string? ShellName,
    PenValidationShotRow? Row,
    string? Error);

/// <summary>
/// The offline PN-4 result for one decoded session: the scored model report
/// over every joinable <c>ShotImpact</c> event, plus the per-shot rows
/// (including skipped ones with their reason) so disagreements localize.
/// </summary>
public sealed record OfflinePenScoreReport(
    int SkippedShots,
    PenValidationReport Validation,
    IReadOnlyList<OfflinePenShot> Shots);

/// <summary>
/// Validates the deterministic pen model against DECODED shot outcomes
/// (PN-4's offline half): every <see cref="CanonicalEventKind.ShotImpact"/>
/// event with an attacker attribution is scored with the attacker's stock
/// shell, the victim's nominal armor + collision mesh, and the
/// attacker→victim CENTER-LINE aim (the only offline aim — the replay stream
/// carries no turret/gun aim, so the ricochet rule is untestable here and the
/// report's band accuracy is the armor-vs-pen term only). Pure orchestration:
/// all geometry lives in <see cref="PenetrationAim"/> /
/// <see cref="PenValidation"/> (Core); all install data in
/// <see cref="IOverlayPenetrationData"/>. Fail-closed: a shot that cannot be
/// fully resolved is skipped with its reason, never guessed.
/// </summary>
public interface IPenOfflineScorer
{
    /// <summary>
    /// Scores the decoded session's shots. <paramref name="aimOverrides"/>
    /// replaces the center-line proxy with true aim observations for the
    /// VIEWPOINT tank's own shots (the CAM-013 camera is the viewer's chase
    /// camera, so only viewer-fired shots have a true aim): a shot whose
    /// attacker is the viewpoint tank uses the nearest override at-or-before
    /// its replay time, falling back to the center-line when none exists.
    /// Every other shot keeps the center-line proxy (fail-closed, never a
    /// fabricated aim).
    /// </summary>
    ValueTask<OfflinePenScoreReport> ScoreAsync(
        ReplayDecodeProjection projection,
        IReadOnlyList<AimSample>? aimOverrides,
        CancellationToken cancellationToken);
}

/// <summary>The <see cref="IPenOfflineScorer"/> implementation.</summary>
public sealed class PenOfflineScorer : IPenOfflineScorer
{
    private const double AimHeightMeters = 1.5;

    private readonly IOverlayPenetrationData _penetrationData;

    public PenOfflineScorer(IOverlayPenetrationData penetrationData)
    {
        ArgumentNullException.ThrowIfNull(penetrationData);
        _penetrationData = penetrationData;
    }

    /// <inheritdoc />
    public async ValueTask<OfflinePenScoreReport> ScoreAsync(
        ReplayDecodeProjection projection,
        IReadOnlyList<AimSample>? aimOverrides,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);

        long? viewpointEntityId = ResolveViewpointEntityId(projection);
        List<AimSample>? aimSamples = aimOverrides is null or { Count: 0 }
            ? null
            : aimOverrides.OrderBy(static sample => sample.ReplayTime).ToList();

        Dictionary<long, Participant> byEntity = [];
        foreach (Participant participant in projection.Participants)
        {
            if (participant.EntityId is { } entityId)
            {
                byEntity.TryAdd(entityId, participant);
            }
        }

        Dictionary<long, List<PositionSample>> samplesByEntity = [];
        foreach (PositionSample sample in projection.Positions)
        {
            if (sample.EntityId is { } entityId)
            {
                if (!samplesByEntity.TryGetValue(entityId, out List<PositionSample>? samples))
                {
                    samples = [];
                    samplesByEntity[entityId] = samples;
                }

                samples.Add(sample);
            }
        }

        foreach (List<PositionSample> samples in samplesByEntity.Values)
        {
            samples.Sort(static (left, right) => left.ReplayTime.CompareTo(right.ReplayTime));
        }

        List<OfflinePenShot> shots = [];
        List<ScoredShot> scorable = [];
        int skipped = 0;

        foreach (CanonicalEvent ev in projection.Events)
        {
            if (ev.Kind != CanonicalEventKind.ShotImpact)
            {
                continue;
            }

            if (!TryReadShot(ev.ValuesJson, out long attackerId, out long victimId, out bool penetrated))
            {
                continue;
            }

            if (!byEntity.TryGetValue(attackerId, out Participant? attacker))
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, null, null, "attacker not in roster"));
                continue;
            }

            if (!byEntity.TryGetValue(victimId, out Participant? victim))
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, attacker, null, "victim not in roster"));
                continue;
            }

            PenetrationTankData? attackerData = await _penetrationData
                .ResolveTankAsync(attacker.TankId ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
            if (attackerData is null || attackerData.Shells.Count == 0)
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, attacker, victim, "attacker tank data unavailable"));
                continue;
            }

            PenetrationTankData? victimData = await _penetrationData
                .ResolveTankAsync(victim.TankId ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
            if (victimData is null)
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, attacker, victim, "victim tank data unavailable"));
                continue;
            }

            PositionSample? attackerSample = FindAtOrBefore(samplesByEntity, attackerId, ev.ReplayTime);
            PositionSample? victimSample = FindAtOrBefore(samplesByEntity, victimId, ev.ReplayTime);
            if (attackerSample is null || victimSample is null)
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, attacker, victim, "position sample missing at shot time"));
                continue;
            }

            AimRay? aim = null;
            if (attacker.EntityId == viewpointEntityId)
            {
                AimSample? overrideSample = FindAimAtOrBefore(aimSamples, ev.ReplayTime);
                if (overrideSample is { } sample)
                {
                    // The pen math assumes a UNIT direction (the incidence
                    // cosine is a raw dot product), so a capture tool's
                    // non-unit ray is normalized here; a degenerate ray falls
                    // through to the center-line proxy.
                    aim = NormalizeAim(sample.Aim);
                }
            }

            aim ??= CenterLineAim(attackerSample, victimSample);
            if (aim is null)
            {
                skipped++;
                shots.Add(ErrorShot(ev, attackerId, victimId, attacker, victim, "attacker and victim coincide"));
                continue;
            }

            OverlayTankState victimState = new(
                victimId,
                victimSample.RawX,
                victimSample.RawY,
                victimSample.RawZ,
                victimSample.Yaw,
                HpFraction: 1.0,
                Alive: true,
                victim.TeamNumber,
                victim.PlayerName,
                victim.ClanTag,
                victim.TankName,
                victim.TankClass.ToString(),
                DistanceMeters: 0);

            ShellOption shell = attackerData.Shells[0];
            scorable.Add(new ScoredShot(
                aim.Value,
                victimState,
                victimData.Mesh ?? [],
                victimData.Armor,
                shell.Spec,
                penetrated));
            shots.Add(new OfflinePenShot(
                ev.Sequence,
                ev.ReplayTime,
                attackerId,
                victimId,
                attacker.TankName,
                victim.TankName,
                shell.Name,
                Row: null,
                Error: null));
        }

        PenValidationReport validation = PenValidation.Score(scorable);

        // Fill the scored rows back into the shot list in order.
        int rowIndex = 0;
        List<OfflinePenShot> completed = new(shots.Count);
        foreach (OfflinePenShot shot in shots)
        {
            completed.Add(shot.Error is null && rowIndex < validation.Rows.Count
                ? shot with { Row = validation.Rows[rowIndex++] }
                : shot);
        }

        return new OfflinePenScoreReport(skipped, validation, completed);
    }

    private static bool TryReadShot(
        string valuesJson,
        out long attackerId,
        out long victimId,
        out bool penetrated)
    {
        attackerId = 0;
        victimId = 0;
        penetrated = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(valuesJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("victimEntityId", out JsonElement victim)
                || victim.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("penetrated", out JsonElement pen)
                || pen.ValueKind != JsonValueKind.True && pen.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("attackerEntityId", out JsonElement attacker)
                || attacker.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            victimId = victim.GetInt64();
            attackerId = attacker.GetInt64();
            penetrated = pen.GetBoolean();
            return attackerId > 0 && victimId > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static OfflinePenShot ErrorShot(
        CanonicalEvent ev,
        long attackerId,
        long victimId,
        Participant? attacker,
        Participant? victim,
        string error) => new(
        ev.Sequence,
        ev.ReplayTime,
        attackerId,
        victimId,
        attacker?.TankName,
        victim?.TankName,
        ShellName: null,
        Row: null,
        Error: error);

    /// <summary>
    /// The offline center-line aim proxy (PN-4's documented offline limit):
    /// origin at the attacker's hull center plus a nominal gun height, aimed
    /// at the victim's hull center. Returns null when the two coincide.
    /// </summary>
    private static AimRay? CenterLineAim(PositionSample attacker, PositionSample victim)
    {
        double originX = attacker.RawX;
        double originY = attacker.RawY + AimHeightMeters;
        double originZ = attacker.RawZ;
        double dx = victim.RawX - originX;
        double dy = victim.RawY - originY;
        double dz = victim.RawZ - originZ;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!double.IsFinite(length) || length <= 1e-6)
        {
            return null;
        }

        return new AimRay(
            originX, originY, originZ,
            dx / length, dy / length, dz / length);
    }

    internal static AimRay? NormalizeAim(AimRay aim)
    {
        double length = Math.Sqrt(
            (aim.DirectionX * aim.DirectionX)
            + (aim.DirectionY * aim.DirectionY)
            + (aim.DirectionZ * aim.DirectionZ));
        if (!double.IsFinite(length) || length <= 1e-6)
        {
            return null;
        }

        return new AimRay(
            aim.OriginX, aim.OriginY, aim.OriginZ,
            aim.DirectionX / length, aim.DirectionY / length, aim.DirectionZ / length);
    }

    private static long? ResolveViewpointEntityId(ReplayDecodeProjection projection)
    {
        if (projection.Session?.ViewpointParticipantId is not { } viewpointId)
        {
            return null;
        }

        return projection.Participants.FirstOrDefault(
            participant => participant.Id == viewpointId)?.EntityId;
    }

    private static AimSample? FindAimAtOrBefore(
        List<AimSample>? samples,
        TimeSpan replayTime)
    {
        if (samples is null or { Count: 0 })
        {
            return null;
        }

        int low = 0;
        int high = samples.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (samples[mid].ReplayTime <= replayTime)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found >= 0 ? samples[found] : null;
    }

    private static PositionSample? FindAtOrBefore(
        Dictionary<long, List<PositionSample>> samplesByEntity,
        long entityId,
        TimeSpan replayTime)
    {
        if (!samplesByEntity.TryGetValue(entityId, out List<PositionSample>? samples)
            || samples.Count == 0)
        {
            return null;
        }

        int index = samples.BinarySearch(samples[0] with { ReplayTime = replayTime },
            PositionSampleTimeComparer.Instance);
        if (index >= 0)
        {
            return samples[index];
        }

        int insertion = ~index;
        return insertion == 0 ? null : samples[insertion - 1];
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
