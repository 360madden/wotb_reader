namespace WotBTreader.Overlay.Contracts;

/// <summary>DTOs for the loopback read API. Wire format is camelCase JSON; deserialize with System.Text.Json.JsonSerializerOptions.Web.</summary>
public sealed record DecodeRunResponse
{
    public string DecodeRunId { get; init; } = string.Empty;

    public string SourceArtifactId { get; init; } = string.Empty;

    public string DecoderId { get; init; } = string.Empty;

    public string DecoderVersion { get; init; } = string.Empty;

    public string SchemaVersion { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public string? FailureCode { get; init; }

    public string? FailureSummary { get; init; }
}

public sealed record BattleSessionResponse
{
    public string BattleSessionId { get; init; } = string.Empty;

    public string? GameVersion { get; init; }

    public string? ArenaIdentity { get; init; }

    public string? MapId { get; init; }

    public string? MapName { get; init; }

    public DateTimeOffset BattleTimeUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public string? ViewpointParticipantId { get; init; }
}

public sealed record SessionSummaryResponse
{
    public DecodeRunResponse DecodeRun { get; init; } = new();

    public BattleSessionResponse Session { get; init; } = new();

    public int ParticipantCount { get; init; }

    public int PositionCount { get; init; }

    public int EventCount { get; init; }

    public int RawRecordCount { get; init; }
}

public sealed record SessionPageResponse
{
    public int Offset { get; init; }

    public int Limit { get; init; }

    public int Count { get; init; }

    public IReadOnlyList<SessionSummaryResponse> Items { get; init; } = [];
}

public sealed record ParticipantResponse
{
    public string ParticipantId { get; init; } = string.Empty;

    public long? EntityId { get; init; }

    public int? TeamNumber { get; init; }

    public string? PlayerName { get; init; }

    public string? ClanTag { get; init; }

    public string? TankId { get; init; }

    public string? TankName { get; init; }

    public string? TankClass { get; init; }

    public string BotStatus { get; init; } = string.Empty;

    public string? BotStatusConfidence { get; init; }
}

public sealed record PositionSampleResponse
{
    public string? ParticipantId { get; init; }

    public long? EntityId { get; init; }

    public long Sequence { get; init; }

    public TimeSpan ReplayTime { get; init; }

    public double RawX { get; init; }

    public double RawY { get; init; }

    public double RawZ { get; init; }

    public double? NormalizedX { get; init; }

    public double? NormalizedY { get; init; }

    public string RawCoordinateSpace { get; init; } = string.Empty;

    public string? NormalizedCoordinateSpace { get; init; }
}

public sealed record SessionDetailResponse
{
    public DecodeRunResponse DecodeRun { get; init; } = new();

    public BattleSessionResponse Session { get; init; } = new();

    public IReadOnlyList<ParticipantResponse> Participants { get; init; } = [];

    public IReadOnlyList<PositionSampleResponse> Positions { get; init; } = [];

    public bool PositionsTruncated { get; init; }

    public int TotalPositionCount { get; init; }

    public int EventCount { get; init; }

    public int RawRecordCount { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record MapBoundaryResponse
{
    public string MapId { get; init; } = string.Empty;

    public double MinX { get; init; }

    public double MaxX { get; init; }

    public double MinZ { get; init; }

    public double MaxZ { get; init; }
}
