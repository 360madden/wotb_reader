namespace WotBTreader.Core;

public enum TelemetrySourceKind
{
    Replay,
    CaptureLog,
    NativeGameLog,
    Manual,
}

public enum ComparisonClassification
{
    Exact,
    Tolerant,
    Mismatch,
    Missing,
    Extra,
    Uncomparable,
}

public enum ReplayClockQuality
{
    Unknown,
    Estimated,
    Stale,
}

public sealed record TelemetryProvenance(
    TelemetrySourceKind SourceKind,
    string SourceVersion,
    SourceArtifactId? SourceArtifactId,
    EvidenceReference? Evidence,
    string? Detail);

public sealed record TelemetryEvent(
    long SourceSequence,
    DateTimeOffset? SourceTimeUtc,
    TimeSpan? ReplayTime,
    string EventType,
    string? ParticipantIdentity,
    long? EntityId,
    string ValuesJson,
    TelemetryProvenance Provenance);

public sealed record ComparisonRun(
    ComparisonRunId Id,
    SourceArtifactId LeftSourceArtifactId,
    SourceArtifactId RightSourceArtifactId,
    string ComparatorId,
    string ComparatorVersion,
    string SchemaVersion,
    TimeSpan TimestampWindow,
    DateTimeOffset CreatedAtUtc);

public sealed record ComparisonItem(
    ComparisonItemId Id,
    ComparisonRunId ComparisonRunId,
    long Sequence,
    ComparisonClassification Classification,
    string EventType,
    TimeSpan? LeftReplayTime,
    TimeSpan? RightReplayTime,
    string? ParticipantIdentity,
    string? Field,
    string? LeftValue,
    string? RightValue,
    string Explanation);

public sealed record ComparisonSummary(
    int Exact,
    int Tolerant,
    int Mismatch,
    int Missing,
    int Extra,
    int Uncomparable);

public sealed record TelemetryComparison(
    ComparisonRun Run,
    ComparisonSummary Summary,
    IReadOnlyList<ComparisonItem> Items);

public sealed record ReplayClockSegment(
    ReplayClockSegmentId Id,
    BattleSessionId BattleSessionId,
    long Sequence,
    DateTimeOffset SourceAnchorUtc,
    TimeSpan ReplayAnchor,
    double Speed,
    TelemetrySourceKind Source,
    TimeSpan Uncertainty,
    DateTimeOffset CreatedAtUtc);

public sealed record ReplayClockSnapshot(
    BattleSessionId BattleSessionId,
    TimeSpan EstimatedReplayTime,
    ReplayClockQuality Quality,
    TelemetrySourceKind? Source,
    TimeSpan? Offset,
    TimeSpan? Uncertainty,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastAnchorUtc);
