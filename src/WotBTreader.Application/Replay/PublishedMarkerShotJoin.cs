using System.Text.Json;
using WotBTreader.Core;

namespace WotBTreader.Application.Replay;

/// <summary>
/// One G2-attested published-marker yaw/pitch sample on the decoded replay
/// clock. This is an angular/clock observation, not ExactGunRay and not CAM-013.
/// </summary>
public readonly record struct PublishedMarkerSample(
    TimeSpan ReplayTime,
    double MarkerYawRadians,
    double MarkerPitchRadians,
    bool SameDecodedClockProven);

/// <summary>
/// Count-only outcome of joining published-marker samples to viewpoint
/// <see cref="CanonicalEventKind.ShotImpact"/> events. No coordinates.
/// </summary>
public sealed record PublishedMarkerJoinSummary(
    int ViewpointShots,
    int Joined,
    int NoSampleBefore,
    int LagExceeded,
    int MissingAttacker,
    int MissingViewpoint,
    int MissingPosition);

/// <summary>
/// Joins G2-attested published-marker yaw/pitch samples to decoded
/// viewpoint-attacker ShotImpact events. Angular/clock join only - not
/// ExactGunRay and not CAM-013.
/// </summary>
public static class PublishedMarkerShotJoin
{
    private const double AimHeightMeters = 1.5;
    private const double DegenerateLength = 1e-6;
    private static readonly double MaxJoinAngleRadians = Math.PI / 18.0;

    public static TimeSpan MaxLag { get; } = TimeSpan.FromMilliseconds(250);

    public static PublishedMarkerJoinSummary Evaluate(
        ReplayDecodeProjection projection,
        IReadOnlyList<PublishedMarkerSample> samples,
        TimeSpan? maxLag = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(samples);
        TimeSpan lagLimit = maxLag ?? MaxLag;
        if (lagLimit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLag));
        }

        long? viewpointEntityId = ResolveViewpointEntityId(projection);
        if (viewpointEntityId is not { } viewpointId)
        {
            return new PublishedMarkerJoinSummary(
                ViewpointShots: 0,
                Joined: 0,
                NoSampleBefore: 0,
                LagExceeded: 0,
                MissingAttacker: 0,
                MissingViewpoint: 1,
                MissingPosition: 0);
        }

        Dictionary<long, List<PositionSample>> samplesByEntity = [];
        foreach (PositionSample sample in projection.Positions)
        {
            if (sample.EntityId is { } entityId)
            {
                if (!samplesByEntity.TryGetValue(entityId, out List<PositionSample>? entitySamples))
                {
                    entitySamples = [];
                    samplesByEntity[entityId] = entitySamples;
                }

                entitySamples.Add(sample);
            }
        }

        foreach (List<PositionSample> entitySamples in samplesByEntity.Values)
        {
            entitySamples.Sort(static (left, right) => left.ReplayTime.CompareTo(right.ReplayTime));
        }

        List<PublishedMarkerSample> clocked = [];
        foreach (PublishedMarkerSample sample in samples)
        {
            if (sample.SameDecodedClockProven)
            {
                clocked.Add(sample);
            }
        }

        clocked.Sort(static (left, right) => left.ReplayTime.CompareTo(right.ReplayTime));

        int viewpointShots = 0;
        int joined = 0;
        int noSampleBefore = 0;
        int lagExceeded = 0;
        int missingAttacker = 0;
        int missingPosition = 0;

        foreach (CanonicalEvent ev in projection.Events)
        {
            if (ev.Kind != CanonicalEventKind.ShotImpact)
            {
                continue;
            }

            if (!TryReadShot(ev.ValuesJson, out long attackerId, out long victimId, out _))
            {
                missingAttacker++;
                continue;
            }

            if (attackerId != viewpointId)
            {
                continue;
            }

            viewpointShots++;

            PositionSample? attackerSample = FindAtOrBefore(samplesByEntity, attackerId, ev.ReplayTime);
            PositionSample? victimSample = FindAtOrBefore(samplesByEntity, victimId, ev.ReplayTime);
            if (attackerSample is null || victimSample is null)
            {
                missingPosition++;
                continue;
            }

            PublishedMarkerSample? marker = FindMarkerAtOrBefore(clocked, ev.ReplayTime);
            if (marker is null)
            {
                noSampleBefore++;
                continue;
            }

            if (ev.ReplayTime - marker.Value.ReplayTime > lagLimit)
            {
                lagExceeded++;
                continue;
            }

            if (!TryMarkerDirection(marker.Value, out double markerDx, out double markerDy, out double markerDz)
                || !TryCenterLineDirection(attackerSample, victimSample, out double centerDx, out double centerDy, out double centerDz))
            {
                lagExceeded++;
                continue;
            }

            double dot = Math.Clamp(
                (markerDx * centerDx) + (markerDy * centerDy) + (markerDz * centerDz),
                -1.0,
                1.0);
            double error = Math.Acos(dot);
            if (error <= MaxJoinAngleRadians)
            {
                joined++;
            }
        }

        return new PublishedMarkerJoinSummary(
            ViewpointShots: viewpointShots,
            Joined: joined,
            NoSampleBefore: noSampleBefore,
            LagExceeded: lagExceeded,
            MissingAttacker: missingAttacker,
            MissingViewpoint: 0,
            MissingPosition: missingPosition);
    }

    // Same JSON contract as PenOfflineScorer.TryReadShot: victimEntityId,
    // penetrated, and attackerEntityId > 0. Duplicated so this join never
    // calls the pen scorer.
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

    // Same lookup as PenOfflineScorer.ResolveViewpointEntityId.
    private static long? ResolveViewpointEntityId(ReplayDecodeProjection projection)
    {
        if (projection.Session?.ViewpointParticipantId is not { } viewpointId)
        {
            return null;
        }

        return projection.Participants.FirstOrDefault(
            participant => participant.Id == viewpointId)?.EntityId;
    }

    private static PublishedMarkerSample? FindMarkerAtOrBefore(
        List<PublishedMarkerSample> samples,
        TimeSpan replayTime)
    {
        if (samples.Count == 0)
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

    // Same at-or-before idea as PenOfflineScorer.FindAtOrBefore.
    private static PositionSample? FindAtOrBefore(
        Dictionary<long, List<PositionSample>> samplesByEntity,
        long entityId,
        TimeSpan replayTime)
    {
        if (!samplesByEntity.TryGetValue(entityId, out List<PositionSample>? entitySamples)
            || entitySamples.Count == 0)
        {
            return null;
        }

        int index = entitySamples.BinarySearch(
            entitySamples[0] with { ReplayTime = replayTime },
            PositionSampleTimeComparer.Instance);
        if (index >= 0)
        {
            return entitySamples[index];
        }

        int insertion = ~index;
        return insertion == 0 ? null : entitySamples[insertion - 1];
    }

    // Coordinator convention: yaw = atan2(dx, dz), pitch = asin(dy/|dir|).
    private static bool TryMarkerDirection(
        PublishedMarkerSample sample,
        out double dx,
        out double dy,
        out double dz)
    {
        double cosPitch = Math.Cos(sample.MarkerPitchRadians);
        dx = Math.Sin(sample.MarkerYawRadians) * cosPitch;
        dy = Math.Sin(sample.MarkerPitchRadians);
        dz = Math.Cos(sample.MarkerYawRadians) * cosPitch;
        return TryNormalize(ref dx, ref dy, ref dz);
    }

    // Attacker hull + 1.5 m Y toward victim hull (PenOfflineScorer.CenterLineAim).
    private static bool TryCenterLineDirection(
        PositionSample attacker,
        PositionSample victim,
        out double dx,
        out double dy,
        out double dz)
    {
        double originX = attacker.RawX;
        double originY = attacker.RawY + AimHeightMeters;
        double originZ = attacker.RawZ;
        dx = victim.RawX - originX;
        dy = victim.RawY - originY;
        dz = victim.RawZ - originZ;
        return TryNormalize(ref dx, ref dy, ref dz);
    }

    private static bool TryNormalize(ref double dx, ref double dy, ref double dz)
    {
        double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (!double.IsFinite(length) || length <= DegenerateLength)
        {
            dx = 0;
            dy = 0;
            dz = 0;
            return false;
        }

        dx /= length;
        dy /= length;
        dz /= length;
        return true;
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
