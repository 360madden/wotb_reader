namespace WotBTreader.ApiContracts;

/// <summary>
/// Wire shapes for the loopback read API, serialized as camelCase JSON. These
/// exist so the HTTP contract does not inherit the domain's identifier
/// wrappers, which serialize as nested objects, and so a field is exposed only
/// when a client has a reason for it. This assembly carries serialization
/// shapes only: no domain behaviour and no project references.
/// </summary>
/// <remarks>
/// Every member is init-only with a default so that a client tolerates a
/// response produced by a different host version. Nullability here is the
/// contract: a nullable member means the host really can send null.
/// </remarks>
public sealed record DecodeRunResponse
{
    /// <summary>Immutable identifier for this decode run.</summary>
    public string DecodeRunId { get; init; } = string.Empty;

    /// <summary>Identifier of the source artifact that was decoded.</summary>
    public string SourceArtifactId { get; init; } = string.Empty;

    /// <summary>Decoder implementation identifier.</summary>
    public string DecoderId { get; init; } = string.Empty;

    /// <summary>Version string of the decoder used.</summary>
    public string DecoderVersion { get; init; } = string.Empty;

    /// <summary>Schema version of the projection produced by this run.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>Outcome of the decode run.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Capability names discovered during decoding.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>UTC timestamp when decoding started.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC timestamp when decoding completed, or null while running.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Machine-readable error code when the run failed.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable failure summary when the run failed.</summary>
    public string? FailureSummary { get; init; }
}

/// <summary>Metadata about a single battle session extracted from a replay.</summary>
public sealed record BattleSessionResponse
{
    /// <summary>Immutable identifier for this battle session.</summary>
    public string BattleSessionId { get; init; } = string.Empty;

    /// <summary>Game client version recorded in the replay.</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>Internal arena identity string from the game engine.</summary>
    public string? ArenaIdentity { get; init; }

    /// <summary>Map identifier used for boundary lookups.</summary>
    public string? MapId { get; init; }

    /// <summary>Human-readable map name.</summary>
    public string? MapName { get; init; }

    /// <summary>Start of the battle, or null when the replay did not record one.</summary>
    public DateTimeOffset? BattleTimeUtc { get; init; }

    /// <summary>Battle duration, or null when it could not be determined.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Participant identifier of the recording viewpoint, if known.</summary>
    public string? ViewpointParticipantId { get; init; }
}

/// <summary>Summary row for one battle session.</summary>
public sealed record SessionSummaryResponse
{
    /// <summary>Decode run that produced this session.</summary>
    public DecodeRunResponse DecodeRun { get; init; } = new();

    /// <summary>Battle session metadata, or null when the run produced none.</summary>
    public BattleSessionResponse? Session { get; init; }

    /// <summary>Number of participants in this battle.</summary>
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

/// <summary>One participant in a battle session.</summary>
public sealed record ParticipantResponse
{
    /// <summary>Unique identifier for this participant within the session.</summary>
    public string ParticipantId { get; init; } = string.Empty;

    /// <summary>Game entity identifier, if discoverable.</summary>
    public long? EntityId { get; init; }

    /// <summary>Team number, or null when unknown.</summary>
    public int? TeamNumber { get; init; }

    /// <summary>Player name from the replay evidence, if present.</summary>
    public string? PlayerName { get; init; }

    /// <summary>Clan tag abbreviation, if present.</summary>
    public string? ClanTag { get; init; }

    /// <summary>Compact descriptor of the vehicle, if resolved.</summary>
    public int? VehicleCompactDescriptor { get; init; }

    /// <summary>Internal tank identifier.</summary>
    public string? TankId { get; init; }

    /// <summary>Human-readable tank name.</summary>
    public string? TankName { get; init; }

    /// <summary>Tank class name.</summary>
    public string TankClass { get; init; } = string.Empty;

    /// <summary>Bot classification; player names are public Wargaming statistics.</summary>
    public string BotStatus { get; init; } = string.Empty;

    /// <summary>Confidence level backing the bot classification.</summary>
    public string BotStatusConfidence { get; init; } = string.Empty;
}

/// <summary>One position sample captured during a battle.</summary>
public sealed record PositionSampleResponse
{
    /// <summary>Participant this sample belongs to, or null when unattributed.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Game entity identifier for the vehicle at this position.</summary>
    public long? EntityId { get; init; }

    /// <summary>Monotonically increasing sample sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>Replay time when this position was recorded.</summary>
    public TimeSpan ReplayTime { get; init; }

    /// <summary>Raw X coordinate in the game's coordinate space.</summary>
    public double RawX { get; init; }

    /// <summary>Raw Y coordinate in the game's coordinate space.</summary>
    public double RawY { get; init; }

    /// <summary>Raw Z coordinate in the game's coordinate space.</summary>
    public double RawZ { get; init; }

    /// <summary>Map-normalised X coordinate, when the space supports it.</summary>
    public double? NormalizedX { get; init; }

    /// <summary>Map-normalised Y coordinate, when the space supports it.</summary>
    public double? NormalizedY { get; init; }

    /// <summary>Identifier for the raw coordinate space.</summary>
    public string RawCoordinateSpace { get; init; } = string.Empty;

    /// <summary>Identifier for the normalised coordinate space, if available.</summary>
    public string? NormalizedCoordinateSpace { get; init; }
}

/// <summary>One canonical event from a battle session.</summary>
public sealed record EventResponse
{
    /// <summary>Event kind name.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Replay time when this event occurred.</summary>
    public TimeSpan ReplayTime { get; init; }

    /// <summary>Participant this event is attributed to, if any.</summary>
    public string? ParticipantId { get; init; }

    /// <summary>Human-readable summary of the event.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>
/// A session projection. Position samples are bounded because one battle can
/// produce far more samples than a single response should carry;
/// <see cref="PositionsTruncated"/> tells the client the series is partial.
/// </summary>
public sealed record SessionDetailResponse
{
    /// <summary>Decode run that produced this projection.</summary>
    public DecodeRunResponse DecodeRun { get; init; } = new();

    /// <summary>Battle session metadata, or null when the run produced none.</summary>
    public BattleSessionResponse? Session { get; init; }

    /// <summary>All participants in this battle.</summary>
    public IReadOnlyList<ParticipantResponse> Participants { get; init; } = [];

    /// <summary>Position samples, capped to the API's maximum-sample limit.</summary>
    public IReadOnlyList<PositionSampleResponse> Positions { get; init; } = [];

    /// <summary>True when the position list was truncated to fit the cap.</summary>
    public bool PositionsTruncated { get; init; }

    /// <summary>Total number of position samples before truncation.</summary>
    public int TotalPositionCount { get; init; }

    /// <summary>Number of canonical events in this session.</summary>
    public int EventCount { get; init; }

    /// <summary>Number of raw telemetry records before canonical mapping.</summary>
    public int RawRecordCount { get; init; }

    /// <summary>Decode warnings for this session.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Canonical events, capped to the API limit.</summary>
    public IReadOnlyList<EventResponse> Events { get; init; } = [];
}

/// <summary>Computed map boundary from all observed position samples.</summary>
public sealed record MapBoundaryResponse
{
    /// <summary>Map identifier matching <see cref="BattleSessionResponse.MapId"/>.</summary>
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
