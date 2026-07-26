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
