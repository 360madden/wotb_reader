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
    EvidenceReference Evidence);

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
