namespace WotBTreader.Overlay.Contracts;

/// <summary>DTOs for the loopback read API. Wire format is camelCase JSON; deserialize with System.Text.Json.JsonSerializerOptions.Web.</summary>

/// <summary>Metadata about one decode run (a single pass of the replay decoder over a source artifact).</summary>
public sealed record DecodeRunResponse
{
    /// <summary>Immutable identifier for this decode run.</summary>
    public string DecodeRunId { get; init; } = string.Empty;

    /// <summary>Identifier of the source artifact (replay file) that was decoded.</summary>
    public string SourceArtifactId { get; init; } = string.Empty;

    /// <summary>Decoder implementation identifier (e.g. "wotb-v1").</summary>
    public string DecoderId { get; init; } = string.Empty;

    /// <summary>Version string of the decoder used.</summary>
    public string DecoderVersion { get; init; } = string.Empty;

    /// <summary>Schema version of the projection produced by this run.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>Outcome of the decode: Succeeded, Failed, or Partial.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Capabilities discovered during decoding (e.g. positions, events).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>UTC timestamp when decoding started.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC timestamp when decoding completed, or null if still running.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Machine-readable error code when Status is Failed.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable failure summary when Status is Failed.</summary>
    public string? FailureSummary { get; init; }
}

/// <summary>Metadata about a single World of Tanks Blitz battle session extracted from a replay.</summary>
public sealed record BattleSessionResponse
{
    /// <summary>Immutable identifier for this battle session.</summary>
    public string BattleSessionId { get; init; } = string.Empty;

    /// <summary>Game client version recorded in the replay (e.g. "10.6.0").</summary>
    public string? GameVersion { get; init; }

    /// <summary>Internal arena identity string from the game engine.</summary>
    public string? ArenaIdentity { get; init; }

    /// <summary>Map identifier used for boundary lookups (e.g. "02_malinovka").</summary>
    public string? MapId { get; init; }

    /// <summary>Human-readable map name (e.g. "Malinovka").</summary>
    public string? MapName { get; init; }

    /// <summary>UTC timestamp marking the start of the battle.</summary>
    public DateTimeOffset BattleTimeUtc { get; init; }

    /// <summary>Total duration of the battle from start to end.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Participant ID of the replay's recording viewpoint, if known.</summary>
    public string? ViewpointParticipantId { get; init; }
}

/// <summary>Summary row for one battle session, used in the session list and overlay sidebar.</summary>
public sealed record SessionSummaryResponse
{
    /// <summary>Decode run that produced this session.</summary>
    public DecodeRunResponse DecodeRun { get; init; } = new();

    /// <summary>Battle session metadata.</summary>
    public BattleSessionResponse Session { get; init; } = new();

    /// <summary>Number of participants (players) in this battle.</summary>
    public int ParticipantCount { get; init; }

    /// <summary>Number of position samples decoded.</summary>
    public int PositionCount { get; init; }

    /// <summary>Number of canonical events decoded.</summary>
    public int EventCount { get; init; }

    /// <summary>Number of raw telemetry records before canonical mapping.</summary>
    public int RawRecordCount { get; init; }
}

/// <summary>Paginated list of session summaries.</summary>
public sealed record SessionPageResponse
{
    /// <summary>Zero-based offset of the first item in this page.</summary>
    public int Offset { get; init; }

    /// <summary>Maximum number of items requested for this page.</summary>
    public int Limit { get; init; }

    /// <summary>Actual number of items returned in this page.</summary>
    public int Count { get; init; }

    /// <summary>Session summary items for this page.</summary>
    public IReadOnlyList<SessionSummaryResponse> Items { get; init; } = [];
}

/// <summary>One participant (player or bot) in a battle session.</summary>
public sealed record ParticipantResponse
{
    /// <summary>Unique identifier for this participant within the session.</summary>
    public string ParticipantId { get; init; } = string.Empty;

    /// <summary>Game entity identifier, if discoverable.</summary>
    public long? EntityId { get; init; }

    /// <summary>Team number: 1 or 2. Null if unknown.</summary>
    public int? TeamNumber { get; init; }

    /// <summary>Player name from the replay evidence. Null if not present.</summary>
    public string? PlayerName { get; init; }

    /// <summary>Clan tag abbreviation, if present.</summary>
    public string? ClanTag { get; init; }

    /// <summary>Internal tank identifier (e.g. "R01_T-34").</summary>
    public string? TankId { get; init; }

    /// <summary>Human-readable tank name (e.g. "T-34").</summary>
    public string? TankName { get; init; }

    /// <summary>Tank class (e.g. "mediumTank", "heavyTank"). Null if unknown.</summary>
    public string? TankClass { get; init; }

    /// <summary>Bot classification: human, bot, or unknown.</summary>
    public string BotStatus { get; init; } = string.Empty;

    /// <summary>Confidence level for the bot status classification.</summary>
    public string? BotStatusConfidence { get; init; }
}

/// <summary>One position sample captured during a battle.</summary>
public sealed record PositionSampleResponse
{
    /// <summary>Participant this position belongs to. Null if the sample is unattributed.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Game entity identifier for the vehicle at this position.</summary>
    public long? EntityId { get; init; }

    /// <summary>Monotonically increasing sample sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>Replay time when this position was recorded.</summary>
    public TimeSpan ReplayTime { get; init; }

    /// <summary>Raw X coordinate in the game's coordinate space.</summary>
    public double RawX { get; init; }

    /// <summary>Raw Y (height) coordinate in the game's coordinate space.</summary>
    public double RawY { get; init; }

    /// <summary>Raw Z coordinate in the game's coordinate space.</summary>
    public double RawZ { get; init; }

    /// <summary>Map-normalised X coordinate, if the coordinate space supports it.</summary>
    public double? NormalizedX { get; init; }

    /// <summary>Map-normalised Y coordinate, if the coordinate space supports it.</summary>
    public double? NormalizedY { get; init; }

    /// <summary>Identifier for the raw coordinate space (e.g. "world").</summary>
    public string RawCoordinateSpace { get; init; } = string.Empty;

    /// <summary>Identifier for the normalised coordinate space, if available.</summary>
    public string? NormalizedCoordinateSpace { get; init; }
}

/// <summary>Complete detail projection for one battle session, including positions and events.</summary>
public sealed record SessionDetailResponse
{
    /// <summary>Decode run that produced this projection.</summary>
    public DecodeRunResponse DecodeRun { get; init; } = new();

    /// <summary>Battle session metadata.</summary>
    public BattleSessionResponse Session { get; init; } = new();

    /// <summary>All participants in this battle.</summary>
    public IReadOnlyList<ParticipantResponse> Participants { get; init; } = [];

    /// <summary>Position samples, capped to the API's maximum-sample limit.</summary>
    public IReadOnlyList<PositionSampleResponse> Positions { get; init; } = [];

    /// <summary>True when the position list was truncated to fit the response cap.</summary>
    public bool PositionsTruncated { get; init; }

    /// <summary>Total number of position samples before truncation.</summary>
    public int TotalPositionCount { get; init; }

    /// <summary>Number of canonical events in this session.</summary>
    public int EventCount { get; init; }

    /// <summary>Number of raw telemetry records before canonical mapping.</summary>
    public int RawRecordCount { get; init; }

    /// <summary>Decode warnings for this session (e.g. skipped records).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Filtered canonical events (positions excluded), capped to the API limit.</summary>
    public IReadOnlyList<EventResponse> Events { get; init; } = [];
}

/// <summary>One canonical event from a battle session.</summary>
public sealed record EventResponse
{
    /// <summary>Event kind: Damage, Destroyed, BattleEnded, or ParticipantObserved.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Replay time when this event occurred.</summary>
    public TimeSpan ReplayTime { get; init; }

    /// <summary>Participant this event is attributed to (e.g. damage dealer, destroyed vehicle).</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Human-readable summary (e.g. "Damage: 350 HP" or "Destroyed").</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>World-space boundary of one map, used to stabilise the minimap projection.</summary>
public sealed record MapBoundaryResponse
{
    /// <summary>Map identifier matching BattleSessionResponse.MapId.</summary>
    public string MapId { get; init; } = string.Empty;

    /// <summary>Minimum X coordinate of the playable area.</summary>
    public double MinX { get; init; }

    /// <summary>Maximum X coordinate of the playable area.</summary>
    public double MaxX { get; init; }

    /// <summary>Minimum Z coordinate of the playable area.</summary>
    public double MinZ { get; init; }

    /// <summary>Maximum Z coordinate of the playable area.</summary>
    public double MaxZ { get; init; }
}
