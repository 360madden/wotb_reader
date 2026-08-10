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
    private readonly ISessionQueryRepository _sessions;

    public ReplayFrameSource(ISessionQueryRepository sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<OverlayFrame>> GetFrameAsync(
        BattleSessionId sessionId,
        TimeSpan replayTime,
        CancellationToken cancellationToken)
    {
        OperationResult<ReplayDecodeProjection> projectionResult =
            await _sessions.GetProjectionAsync(sessionId, cancellationToken)
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

        return OperationResult.Success(BuildFrame(projection, replayTime));
    }

    internal static OverlayFrame BuildFrame(
        ReplayDecodeProjection projection,
        TimeSpan replayTime)
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
                    break;
                case CanonicalEventKind.Destroyed:
                    destroyedAt.TryAdd(entityId, canonical.ReplayTime);
                    break;
            }
        }

        // Camera: the viewpoint participant's entity.
        OverlayCamera camera = BuildCamera(projection, byEntity, replayTime);

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
            bool alive = !destroyedAt.TryGetValue(entityId, out TimeSpan destroyed)
                || destroyed > replayTime;
            // 1.0 when the tank took no damage; otherwise the fraction of its
            // observed damage arc NOT yet received at this frame time.
            double hpFraction = received <= 0
                ? 1.0
                : Math.Clamp(1.0 - (double)CumulativeDamageBefore(
                    projection.Events, entityId, replayTime) / received, 0.0, 1.0);

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
                distance));
        }

        tanks.Sort(static (left, right) => left.DistanceMeters.CompareTo(right.DistanceMeters));
        return new OverlayFrame(replayTime, camera, tanks);
    }

    private static OverlayCamera BuildCamera(
        ReplayDecodeProjection projection,
        Dictionary<long, List<PositionSample>> byEntity,
        TimeSpan replayTime)
    {
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
