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

/// <summary>
/// Per-player battle results statistics. Every member is nullable because the
/// replay evidence may simply not record it; null means unknown, never zero.
/// </summary>
public sealed record BattleStatsResponse
{
    /// <summary>Credits earned, without special awards, medals, or premium.</summary>
    public int? CreditsEarned { get; init; }

    /// <summary>Base XP, the total without multipliers.</summary>
    public int? BaseXp { get; init; }

    /// <summary>Number of shots fired.</summary>
    public int? Shots { get; init; }

    /// <summary>Number of hits dealt.</summary>
    public int? HitsDealt { get; init; }

    /// <summary>Number of penetrations dealt.</summary>
    public int? PenetrationsDealt { get; init; }

    /// <summary>Damage dealt.</summary>
    public int? DamageDealt { get; init; }

    /// <summary>Assisted damage, first kind.</summary>
    public int? DamageAssisted1 { get; init; }

    /// <summary>Assisted damage, second kind.</summary>
    public int? DamageAssisted2 { get; init; }

    /// <summary>Number of hits received.</summary>
    public int? HitsReceived { get; init; }

    /// <summary>Number of non-penetrating hits received.</summary>
    public int? NonPenetratingHitsReceived { get; init; }

    /// <summary>Number of penetrations received.</summary>
    public int? PenetrationsReceived { get; init; }

    /// <summary>Number of enemies damaged.</summary>
    public int? EnemiesDamaged { get; init; }

    /// <summary>Number of enemies destroyed.</summary>
    public int? EnemiesDestroyed { get; init; }

    /// <summary>Victory points earned.</summary>
    public int? VictoryPointsEarned { get; init; }

    /// <summary>Victory points seized.</summary>
    public int? VictoryPointsSeized { get; init; }

    /// <summary>
    /// Rating-battles rating; matches the Wargaming.net API mm_rating.
    /// Display rating is calculated as 3000.0 + mm_rating * 10.0.
    /// </summary>
    public float? MmRating { get; init; }

    /// <summary>Damage blocked by armor.</summary>
    public int? DamageBlocked { get; init; }
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

    /// <summary>Battle results statistics, or null when the replay recorded none.</summary>
    public BattleStatsResponse? BattleStats { get; init; }
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

/// <summary>
/// One overlay tank projected onto the viewport. Screen coordinates are
/// null when the tank is at/behind the camera or the camera carries no
/// rotation evidence — the HUD must not draw it.
/// </summary>
public sealed record OverlayTankResponse
{
    /// <summary>Game entity identifier for the vehicle.</summary>
    public long EntityId { get; init; }

    /// <summary>Player name, when decoded.</summary>
    public string? PlayerName { get; init; }

    /// <summary>Vehicle name, when decoded.</summary>
    public string? TankName { get; init; }

    /// <summary>Clan tag, when decoded.</summary>
    public string? ClanTag { get; init; }

    /// <summary>Team number (1 or 2); null when unknown.</summary>
    public int? TeamNumber { get; init; }

    /// <summary>HP fraction 0..1: exact health (1 − taken/maxHealth) when the
    /// type-5 max-HP broadcast decoded for this tank, otherwise the observed
    /// damage arc.</summary>
    public double HpFraction { get; init; }

    /// <summary>Exact max HP from the type-5 spawn broadcast; 0 when unknown.</summary>
    public long MaxHealth { get; init; }

    /// <summary>Current HP = maxHealth − damage received (clamped ≥ 0); 0 when
    /// max health is unknown.</summary>
    public long CurrentHealth { get; init; }

    /// <summary>True while the tank is not destroyed at this replay time.</summary>
    public bool Alive { get; init; }

    /// <summary>Distance from the camera in world units.</summary>
    public double DistanceMeters { get; init; }

    /// <summary>World X of the tank's nearest position sample (replay-raw
    /// space). Independent of the camera — used by the minimap, which draws
    /// god-view positions regardless of what the camera can see.</summary>
    public double WorldX { get; init; }

    /// <summary>World Z of the tank's nearest position sample (replay-raw
    /// space). Independent of the camera — used by the minimap.</summary>
    public double WorldZ { get; init; }

    /// <summary>Projected viewport X (pixels from the left); null = behind camera.</summary>
    public double? ScreenX { get; init; }

    /// <summary>Projected viewport Y (pixels from the top); null = behind camera.</summary>
    public double? ScreenY { get; init; }

    /// <summary>Camera-space depth for painter's-algorithm sorting.</summary>
    public double? Depth { get; init; }

    /// <summary>True when the projection lies inside the requested viewport.</summary>
    public bool InViewport { get; init; }

    /// <summary>Screen-space hull heading in degrees, clockwise from screen-up
    /// (0 = facing away from the viewer); null when the tank has no packet
    /// rotation evidence or its facing projects to a single pixel. Drives the
    /// nameplate facing arrow.</summary>
    public double? ScreenHeadingDegrees { get; init; }

    /// <summary>Cumulative damage this tank has dealt up to the frame time
    /// (sum of damage events attributed to it as attacker; 0 when no
    /// evidence). Scoreboard column.</summary>
    public long DamageDealt { get; init; }

    /// <summary>Cumulative damage this tank has received up to the frame
    /// time. Scoreboard column.</summary>
    public long DamageTaken { get; init; }

    /// <summary>Destroy kills this tank has scored up to the frame time.
    /// Scoreboard column.</summary>
    public long Kills { get; init; }
}

/// <summary>
/// One beacon projected onto the viewport. Screen coordinates are null when
/// the beacon is at/behind the camera or the camera carries no rotation
/// evidence — the HUD must not draw it.
/// </summary>
public sealed record OverlayBeaconResponse
{
    /// <summary>Beacon label.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Marker color as an HTML-style hex string.</summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>Distance from the camera in world units.</summary>
    public double DistanceMeters { get; init; }

    /// <summary>World X of the beacon (replay-raw space). Camera-independent;
    /// used by the minimap so POIs appear regardless of what the camera sees.</summary>
    public double WorldX { get; init; }

    /// <summary>World Z of the beacon (replay-raw space). Camera-independent;
    /// used by the minimap.</summary>
    public double WorldZ { get; init; }

    /// <summary>Projected viewport X (pixels from the left); null = behind camera.</summary>
    public double? ScreenX { get; init; }

    /// <summary>Projected viewport Y (pixels from the top); null = behind camera.</summary>
    public double? ScreenY { get; init; }

    /// <summary>Camera-space depth for painter's-algorithm sorting.</summary>
    public double? Depth { get; init; }

    /// <summary>True when the projection lies inside the requested viewport.</summary>
    public bool InViewport { get; init; }
}

/// <summary>
/// One renderable instant of the replay overlay at a replay time: the
/// viewpoint camera, every roster tank, and every visible beacon projected
/// to viewport pixels. The HUD draws these directly over the game window.
/// </summary>
public sealed record OverlayFrameResponse
{
    /// <summary>Replay time the frame was built at, in seconds.</summary>
    public double ReplayTimeSeconds { get; init; }

    /// <summary>Camera world X; null when the viewpoint has no evidence.</summary>
    public double? CameraX { get; init; }

    /// <summary>Camera world Y; null when the viewpoint has no evidence.</summary>
    public double? CameraY { get; init; }

    /// <summary>Camera world Z; null when the viewpoint has no evidence.</summary>
    public double? CameraZ { get; init; }

    /// <summary>Camera facing (radians); null without packet rotation evidence.</summary>
    public double? CameraYawRadians { get; init; }

    /// <summary>Camera pitch (radians); null without packet rotation evidence.</summary>
    public double? CameraPitchRadians { get; init; }

    /// <summary>Projected tanks, sorted by distance (nearest first).</summary>
    public IReadOnlyList<OverlayTankResponse> Tanks { get; init; } = [];

    /// <summary>Projected beacons visible at this replay time.</summary>
    public IReadOnlyList<OverlayBeaconResponse> Beacons { get; init; } = [];

    /// <summary>Event-feed pips (damage/death) from the recent replay window,
    /// anchored at the affected tank's viewport pixel.</summary>
    public IReadOnlyList<OverlayPipResponse> Pips { get; init; } = [];

    /// <summary>Kill feed: every destroy landed at or before this frame's
    /// replay time, ordered oldest first (the HUD renders newest first).
    /// Killer is null when attribution is impossible (environmental kill).</summary>
    public IReadOnlyList<OverlayKillResponse> Kills { get; init; } = [];
}

/// <summary>One kill-feed entry: the destroyed tank and, when attributable,
/// the killer (attacker of the victim's last damage event before the destroy
/// marker).</summary>
public sealed record OverlayKillResponse
{
    /// <summary>Destroyed tank entity id.</summary>
    public long VictimEntityId { get; init; }

    /// <summary>Killer entity id; null for environmental kills.</summary>
    public long? KillerEntityId { get; init; }

    /// <summary>Replay time of the destroy.</summary>
    public double ReplayTimeSeconds { get; init; }
}

/// <summary>One event-feed pip (damage hit or destruction) rendered over the
/// affected tank's nameplate.</summary>
public sealed record OverlayPipResponse
{
    /// <summary>Affected tank entity id.</summary>
    public long EntityId { get; init; }

    /// <summary>Pip kind: <c>Damage</c> (with <see cref="Damage"/>) or
    /// <c>Destroyed</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Damage amount for <c>Damage</c> pips; 0 otherwise.</summary>
    public int Damage { get; init; }

    /// <summary>Viewport X of the affected tank (always in viewport).</summary>
    public double ScreenX { get; init; }

    /// <summary>Viewport Y of the affected tank (always in viewport).</summary>
    public double ScreenY { get; init; }
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
