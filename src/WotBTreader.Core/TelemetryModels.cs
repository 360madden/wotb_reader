namespace WotBTreader.Core;

[Flags]
public enum ReplayCapability
{
    None = 0,
    Metadata = 1 << 0,
    BattleResults = 1 << 1,
    Participants = 1 << 2,
    Teams = 1 << 3,
    EntityMapping = 1 << 4,
    Positions = 1 << 5,
    Damage = 1 << 6,
    Lifecycle = 1 << 7,
    InstalledGameMetadata = 1 << 8,
    UnknownRecordsPreserved = 1 << 9,
    /// <summary>
    /// The decode produced per-shot impact outcomes (type-32 damage mirror
    /// <c>01 11</c>/<c>01 12</c> → <see cref="CanonicalEventKind.ShotImpact"/>).
    /// </summary>
    ShotImpact = 1 << 10,
}

public enum DecodeRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Unsupported,
    Cancelled,
}

public enum BotStatus
{
    Unknown,
    Human,
    Bot,
}

public enum Affiliation
{
    Unknown,
    Friendly,
    Enemy,
}

public enum TankClass
{
    Unknown,
    Light,
    Medium,
    Heavy,
    TankDestroyer,
}

public enum EvidenceConfidence
{
    Unknown,
    Exact,
    Derived,
    Estimated,
}

public enum CanonicalEventKind
{
    Unknown,
    ParticipantObserved,
    Position,
    Damage,
    Destroyed,
    BattleStarted,
    BattleEnded,
    /// <summary>
    /// The tank's maximum health, read from the type-5 spawn full-state
    /// broadcast (u16 current HP at payload +0x33). The first broadcast per
    /// entity fires before any damage and equals max HP (verified 2026-08-11
    /// on both 11.19 replays: author 700 == battle_results hitpoints_left,
    /// 28/28 tanks monotonic non-increasing, first-broadcast-before-damage
    /// 28/28, total damage_dealt &lt;= total pool on both replays). Values
    /// json: {"maxHealth": n}.
    /// </summary>
    MaxHealthObserved,
    /// <summary>
    /// One shell impact's outcome, read from the type-32 damage/impact event
    /// mirror (the <c>01 11</c>/<c>01 12</c> damage-with-payload variants).
    /// The payload's hit-result byte (offset 19 for <c>01 12</c>, 18 for
    /// <c>01 11</c>) is <c>0x03</c> for a PENETRATING hit and
    /// <c>0x00/0x01/0x02/0x04</c> for a NON-penetrating hit (bounce/absorb),
    /// pinned 2026-08-13 on three distinct replays (~98% agreement with the
    /// type-8 damage ledger; the ~1% outliers are post-destruction hits and
    /// same-tick pen+bounce pairs). Values json:
    /// {"victimEntityId": n, "hitResult": n, "penetrated": bool}.
    /// </summary>
    ShotImpact,
}

public enum CoordinateSpace
{
    Unknown,
    ReplayRaw,
    MapNormalized,
}

public sealed record SourceArtifact(
    SourceArtifactId Id,
    ContentHash Sha256,
    long ByteLength,
    string MediaType,
    string StoredExtension,
    DateTimeOffset ImportedAtUtc,
    string SchemaVersion);

public sealed record DecodeRun(
    DecodeRunId Id,
    SourceArtifactId SourceArtifactId,
    string DecoderId,
    string DecoderVersion,
    string SchemaVersion,
    DecodeRunStatus Status,
    ReplayCapability Capabilities,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureSummary);

public sealed record EvidenceReference(
    SourceArtifactId SourceArtifactId,
    string? ArchiveEntry,
    long Offset,
    int Length,
    ContentHash Sha256);

public sealed record RawRecord(
    RawRecordId Id,
    DecodeRunId DecodeRunId,
    long Ordinal,
    string RecordKind,
    TimeSpan? ReplayTime,
    EvidenceReference Evidence,
    string? PropertiesJson);

public sealed record CanonicalEvent(
    CanonicalEventId Id,
    DecodeRunId DecodeRunId,
    BattleSessionId BattleSessionId,
    long Sequence,
    CanonicalEventKind Kind,
    TimeSpan ReplayTime,
    ParticipantId? ParticipantId,
    long? EntityId,
    string ValuesJson,
    EvidenceConfidence Confidence,
    EvidenceReference Evidence);

public sealed record BattleSession(
    BattleSessionId Id,
    DecodeRunId DecodeRunId,
    string GameVersion,
    string? ArenaIdentity,
    string? MapId,
    string? MapName,
    DateTimeOffset? BattleTimeUtc,
    TimeSpan? Duration,
    ParticipantId? ViewpointParticipantId,
    string SchemaVersion);

/// <summary>
/// Per-player battle results statistics decoded from battle_results.dat
/// (player-results info message), cross-referenced against the parser schema.
/// Null means the value was absent or out of the documented range — unknown
/// stays unknown; a missing stat is never guessed.
/// </summary>
public sealed record BattleStats(
    int? CreditsEarned,
    int? BaseXp,
    int? Shots,
    int? HitsDealt,
    int? PenetrationsDealt,
    int? DamageDealt,
    int? DamageAssisted1,
    int? DamageAssisted2,
    int? HitsReceived,
    int? NonPenetratingHitsReceived,
    int? PenetrationsReceived,
    int? EnemiesDamaged,
    int? EnemiesDestroyed,
    int? VictoryPointsEarned,
    int? VictoryPointsSeized,
    float? MmRating,
    int? DamageBlocked);

public sealed record Participant(
    ParticipantId Id,
    BattleSessionId BattleSessionId,
    long? AccountId,
    long? EntityId,
    int? TeamNumber,
    string? PlayerName,
    string? ClanTag,
    int? VehicleCompactDescriptor,
    string? TankId,
    string? TankName,
    TankClass TankClass,
    BotStatus BotStatus,
    EvidenceConfidence BotStatusConfidence,
    BattleStats? BattleStats,
    EvidenceReference Evidence);

public sealed record PositionSample(
    PositionSampleId Id,
    BattleSessionId BattleSessionId,
    ParticipantId? ParticipantId,
    long? EntityId,
    long Sequence,
    TimeSpan ReplayTime,
    double RawX,
    double RawY,
    double RawZ,
    double? NormalizedX,
    double? NormalizedY,
    CoordinateSpace RawCoordinateSpace,
    CoordinateSpace? NormalizedCoordinateSpace,
    EvidenceReference Evidence,
    // The entity's rotation in radians from the type-10 packet tail
    // (yaw at payload +36, pitch +40, roll +44); null for samples decoded
    // before migration 5 (2026-08-10) persisted the fields.
    double? Yaw = null,
    double? Pitch = null,
    double? Roll = null);

public sealed record ReplayDecodeProjection(
    DecodeRun DecodeRun,
    BattleSession? Session,
    IReadOnlyList<Participant> Participants,
    IReadOnlyList<PositionSample> Positions,
    IReadOnlyList<CanonicalEvent> Events,
    IReadOnlyList<RawRecord> RawRecords,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Computed map boundary from all observed position samples across every
/// imported replay. Used to normalise position plots so they overlay the
/// game's minimap accurately regardless of which area of the map a
/// particular battle covered.
/// </summary>
public sealed record MapBoundary(
    string MapId,
    double MinX,
    double MaxX,
    double MinZ,
    double MaxZ);
