using WotBTreader.ApiContracts;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Host.Web.Contracts;

/// <summary>
/// Projects domain and storage types onto the portable wire contracts. This
/// lives in the host rather than in <c>WotBTreader.ApiContracts</c> because that
/// assembly deliberately has no reference to <c>Core</c>.
/// </summary>
internal static class ReadContractMapping
{
    public static BattleSessionResponse ToResponse(this BattleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new BattleSessionResponse
        {
            BattleSessionId = session.Id.Value.ToString("D"),
            GameVersion = session.GameVersion,
            ArenaIdentity = session.ArenaIdentity,
            MapId = session.MapId,
            MapName = session.MapName,
            BattleTimeUtc = session.BattleTimeUtc,
            Duration = session.Duration,
            ViewpointParticipantId = session.ViewpointParticipantId?.Value.ToString("D"),
        };
    }

    public static DecodeRunResponse ToResponse(this DecodeRun decodeRun)
    {
        ArgumentNullException.ThrowIfNull(decodeRun);
        return new DecodeRunResponse
        {
            DecodeRunId = decodeRun.Id.Value.ToString("D"),
            SourceArtifactId = decodeRun.SourceArtifactId.Value.ToString("D"),
            DecoderId = decodeRun.DecoderId,
            DecoderVersion = decodeRun.DecoderVersion,
            SchemaVersion = decodeRun.SchemaVersion,
            Status = decodeRun.Status.ToString(),
            Capabilities = DescribeCapabilities(decodeRun.Capabilities),
            StartedAtUtc = decodeRun.StartedAtUtc,
            CompletedAtUtc = decodeRun.CompletedAtUtc,
            FailureCode = decodeRun.FailureCode,
            FailureSummary = decodeRun.FailureSummary,
        };
    }

    public static ParticipantResponse ToResponse(this Participant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        // AccountId is deliberately not exposed. It is a durable cross-battle
        // identifier with no display purpose, so the API has no reason to hand
        // it to a client.
        return new ParticipantResponse
        {
            ParticipantId = participant.Id.Value.ToString("D"),
            EntityId = participant.EntityId,
            TeamNumber = participant.TeamNumber,
            PlayerName = participant.PlayerName,
            ClanTag = participant.ClanTag,
            VehicleCompactDescriptor = participant.VehicleCompactDescriptor,
            TankId = participant.TankId,
            TankName = participant.TankName,
            TankClass = participant.TankClass.ToString(),
            BotStatus = participant.BotStatus.ToString(),
            BotStatusConfidence = participant.BotStatusConfidence.ToString(),
            BattleStats = ToResponse(participant.BattleStats),
        };
    }

    private static BattleStatsResponse? ToResponse(BattleStats? stats)
    {
        if (stats is null)
        {
            return null;
        }

        return new BattleStatsResponse
        {
            CreditsEarned = stats.CreditsEarned,
            BaseXp = stats.BaseXp,
            Shots = stats.Shots,
            HitsDealt = stats.HitsDealt,
            PenetrationsDealt = stats.PenetrationsDealt,
            DamageDealt = stats.DamageDealt,
            DamageAssisted1 = stats.DamageAssisted1,
            DamageAssisted2 = stats.DamageAssisted2,
            HitsReceived = stats.HitsReceived,
            NonPenetratingHitsReceived = stats.NonPenetratingHitsReceived,
            PenetrationsReceived = stats.PenetrationsReceived,
            EnemiesDamaged = stats.EnemiesDamaged,
            EnemiesDestroyed = stats.EnemiesDestroyed,
            VictoryPointsEarned = stats.VictoryPointsEarned,
            VictoryPointsSeized = stats.VictoryPointsSeized,
            MmRating = stats.MmRating,
            DamageBlocked = stats.DamageBlocked,
        };
    }

    public static PositionSampleResponse ToResponse(this PositionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new PositionSampleResponse
        {
            ParticipantId = sample.ParticipantId?.Value.ToString("D"),
            EntityId = sample.EntityId,
            Sequence = sample.Sequence,
            ReplayTime = sample.ReplayTime,
            RawX = sample.RawX,
            RawY = sample.RawY,
            RawZ = sample.RawZ,
            NormalizedX = sample.NormalizedX,
            NormalizedY = sample.NormalizedY,
            RawCoordinateSpace = sample.RawCoordinateSpace.ToString(),
            NormalizedCoordinateSpace = sample.NormalizedCoordinateSpace?.ToString(),
        };
    }

    public static EventResponse ToResponse(this CanonicalEvent canonicalEvent)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        return new EventResponse
        {
            Kind = canonicalEvent.Kind.ToString(),
            ReplayTime = canonicalEvent.ReplayTime,
            ParticipantId = canonicalEvent.ParticipantId?.Value.ToString("D"),
            Summary = FormatSummary(canonicalEvent.Kind, canonicalEvent.ValuesJson),
        };
    }

    public static MapBoundaryResponse ToResponse(this MapBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        return new MapBoundaryResponse
        {
            MapId = boundary.MapId,
            MinX = boundary.MinX,
            MaxX = boundary.MaxX,
            MinZ = boundary.MinZ,
            MaxZ = boundary.MaxZ,
        };
    }

    public static SessionSummaryResponse ToResponse(this DecodeRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new SessionSummaryResponse
        {
            DecodeRun = summary.DecodeRun.ToResponse(),
            Session = summary.Session?.ToResponse(),
            ParticipantCount = summary.ParticipantCount,
            PositionCount = summary.PositionCount,
            EventCount = summary.EventCount,
            RawRecordCount = summary.RawRecordCount,
        };
    }

    /// <summary>Expands the capability flags into names a client can display.</summary>
    private static string[] DescribeCapabilities(ReplayCapability capabilities) =>
        [.. Enum.GetValues<ReplayCapability>()
            .Where(candidate => candidate != ReplayCapability.None && capabilities.HasFlag(candidate))
            .Select(static candidate => candidate.ToString())];

    private static string FormatSummary(CanonicalEventKind kind, string valuesJson)
    {
        if (kind == CanonicalEventKind.BattleEnded)
        {
            return "Battle ended";
        }

        if (kind == CanonicalEventKind.ParticipantObserved)
        {
            return "Joined battle";
        }

        if (kind == CanonicalEventKind.Position)
        {
            return "Position update";
        }

        if (kind == CanonicalEventKind.Damage)
        {
            try
            {
                using System.Text.Json.JsonDocument doc =
                    System.Text.Json.JsonDocument.Parse(valuesJson);
                if (doc.RootElement.TryGetProperty("damage", out System.Text.Json.JsonElement dmg))
                {
                    return $"Damage: {dmg.GetInt32()} HP";
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Fall through to generic summary.
            }

            return "Damage dealt";
        }

        if (kind == CanonicalEventKind.Destroyed)
        {
            return "Destroyed";
        }

        return kind.ToString();
    }
}
