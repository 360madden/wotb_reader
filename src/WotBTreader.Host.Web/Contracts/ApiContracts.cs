using WotBTreader.Core;

namespace WotBTreader.Host.Web.Contracts;

/// <summary>
/// Wire shapes for the loopback read API. These exist so the HTTP contract does
/// not inherit the domain's identifier wrappers, which serialize as nested
/// objects, and so a field is exposed only when a client has a reason for it.
/// </summary>
public sealed record BattleSessionResponse(
    string BattleSessionId,
    string GameVersion,
    string? ArenaIdentity,
    string? MapId,
    string? MapName,
    DateTimeOffset? BattleTimeUtc,
    TimeSpan? Duration,
    string? ViewpointParticipantId)
{
    public static BattleSessionResponse From(BattleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new BattleSessionResponse(
            session.Id.Value.ToString("D"),
            session.GameVersion,
            session.ArenaIdentity,
            session.MapId,
            session.MapName,
            session.BattleTimeUtc,
            session.Duration,
            session.ViewpointParticipantId?.Value.ToString("D"));
    }
}

public sealed record DecodeRunResponse(
    string DecodeRunId,
    string SourceArtifactId,
    string DecoderId,
    string DecoderVersion,
    string SchemaVersion,
    string Status,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureSummary)
{
    public static DecodeRunResponse From(DecodeRun decodeRun)
    {
        ArgumentNullException.ThrowIfNull(decodeRun);
        return new DecodeRunResponse(
            decodeRun.Id.Value.ToString("D"),
            decodeRun.SourceArtifactId.Value.ToString("D"),
            decodeRun.DecoderId,
            decodeRun.DecoderVersion,
            decodeRun.SchemaVersion,
            decodeRun.Status.ToString(),
            DescribeCapabilities(decodeRun.Capabilities),
            decodeRun.StartedAtUtc,
            decodeRun.CompletedAtUtc,
            decodeRun.FailureCode,
            decodeRun.FailureSummary);
    }

    /// <summary>Expands the capability flags into names a client can display.</summary>
    private static string[] DescribeCapabilities(ReplayCapability capabilities) =>
        [.. Enum.GetValues<ReplayCapability>()
            .Where(candidate => candidate != ReplayCapability.None && capabilities.HasFlag(candidate))
            .Select(static candidate => candidate.ToString())];
}

public sealed record SessionSummaryResponse(
    DecodeRunResponse DecodeRun,
    BattleSessionResponse? Session,
    int ParticipantCount,
    int PositionCount,
    int EventCount,
    int RawRecordCount);

public sealed record ParticipantResponse(
    string ParticipantId,
    long? EntityId,
    int? TeamNumber,
    string? PlayerName,
    string? ClanTag,
    int? VehicleCompactDescriptor,
    string? TankId,
    string? TankName,
    string TankClass,
    string BotStatus,
    string BotStatusConfidence)
{
    public static ParticipantResponse From(Participant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        // AccountId is deliberately not exposed. It is a durable cross-battle
        // identifier with no display purpose, so the API has no reason to hand
        // it to a client.
        return new ParticipantResponse(
            participant.Id.Value.ToString("D"),
            participant.EntityId,
            participant.TeamNumber,
            participant.PlayerName,
            participant.ClanTag,
            participant.VehicleCompactDescriptor,
            participant.TankId,
            participant.TankName,
            participant.TankClass.ToString(),
            participant.BotStatus.ToString(),
            participant.BotStatusConfidence.ToString());
    }
}

public sealed record PositionSampleResponse(
    string? ParticipantId,
    long? EntityId,
    long Sequence,
    TimeSpan ReplayTime,
    double RawX,
    double RawY,
    double RawZ,
    double? NormalizedX,
    double? NormalizedY,
    string RawCoordinateSpace,
    string? NormalizedCoordinateSpace)
{
    public static PositionSampleResponse From(PositionSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new PositionSampleResponse(
            sample.ParticipantId?.Value.ToString("D"),
            sample.EntityId,
            sample.Sequence,
            sample.ReplayTime,
            sample.RawX,
            sample.RawY,
            sample.RawZ,
            sample.NormalizedX,
            sample.NormalizedY,
            sample.RawCoordinateSpace.ToString(),
            sample.NormalizedCoordinateSpace?.ToString());
    }
}

/// <summary>
/// One canonical event from a battle session, with a human-readable summary
/// computed from the structured values payload.
/// </summary>
public sealed record EventResponse(
    string Kind,
    TimeSpan ReplayTime,
    string? ParticipantId,
    string Summary)
{
    public static EventResponse From(CanonicalEvent canonicalEvent)
    {
        ArgumentNullException.ThrowIfNull(canonicalEvent);
        string summary = FormatSummary(canonicalEvent.Kind, canonicalEvent.ValuesJson);
        return new EventResponse(
            canonicalEvent.Kind.ToString(),
            canonicalEvent.ReplayTime,
            canonicalEvent.ParticipantId?.Value.ToString("D"),
            summary);
    }

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

/// <summary>
/// A session projection. Position samples are bounded because one battle can
/// produce far more samples than a single response should carry;
/// <paramref name="PositionsTruncated"/> tells the client the series is partial.
/// </summary>
public sealed record SessionDetailResponse(
    DecodeRunResponse DecodeRun,
    BattleSessionResponse? Session,
    IReadOnlyList<ParticipantResponse> Participants,
    IReadOnlyList<PositionSampleResponse> Positions,
    bool PositionsTruncated,
    int TotalPositionCount,
    int EventCount,
    int RawRecordCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<EventResponse> Events);

public sealed record SessionPageResponse(
    int Offset,
    int Limit,
    int Count,
    IReadOnlyList<SessionSummaryResponse> Items);

/// <summary>Computed map boundary from all observed position samples.</summary>
public sealed record MapBoundaryResponse(
    string MapId,
    double MinX,
    double MaxX,
    double MinZ,
    double MaxZ)
{
    public static MapBoundaryResponse From(MapBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        return new MapBoundaryResponse(
            boundary.MapId,
            boundary.MinX,
            boundary.MaxX,
            boundary.MinZ,
            boundary.MaxZ);
    }
}
