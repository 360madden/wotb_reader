namespace WotBTreader.Core;

/// <summary>Identifies which subsystem produced a telemetry event.</summary>
public enum TelemetrySourceKind
{
    Replay,
    CaptureLog,
    NativeGameLog,
    Manual,
}

/// <summary>
/// Result of comparing one event or field between two telemetry sources.
/// <see cref="Exact"/> and <see cref="Tolerant"/> both count as matches;
/// <see cref="Mismatch"/>, <see cref="Missing"/>, and <see cref="Extra"/>
/// indicate discrepancies.
/// </summary>
public enum ComparisonClassification
{
    Exact,
    Tolerant,
    Mismatch,
    Missing,
    Extra,
    Uncomparable,
}

/// <summary>Confidence level of the current estimated replay-clock position.</summary>
public enum ReplayClockQuality
{
    Unknown,
    Estimated,
    Stale,
}

/// <summary>Immutable chain-of-custody for one telemetry event, linking it to its source artifact.</summary>
public sealed record TelemetryProvenance(
    TelemetrySourceKind SourceKind,
    string SourceVersion,
    SourceArtifactId? SourceArtifactId,
    EvidenceReference? Evidence,
    string? Detail);

/// <summary>
/// One decoded game event with its values serialized as a JSON string.
/// The <see cref="ValuesJson"/> blob is opaque to storage and comparison;
/// comparators deserialize it against the event type's known schema.
/// </summary>
public sealed record TelemetryEvent(
    long SourceSequence,
    DateTimeOffset? SourceTimeUtc,
    TimeSpan? ReplayTime,
    string EventType,
    string? ParticipantIdentity,
    long? EntityId,
    string ValuesJson,
    TelemetryProvenance Provenance);

/// <summary>
/// Immutable record of one comparison run: the two source artifacts,
/// the comparator that produced the result, and the creation timestamp.
/// </summary>
public sealed record ComparisonRun(
    ComparisonRunId Id,
    SourceArtifactId LeftSourceArtifactId,
    SourceArtifactId RightSourceArtifactId,
    string ComparatorId,
    string ComparatorVersion,
    string SchemaVersion,
    TimeSpan TimestampWindow,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// One classified row in a comparison result. Each item represents a single
/// event or field that was compared between the left and right sources.
/// </summary>
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

/// <summary>
/// Aggregated counts by classification for a comparison run.
/// <see cref="Exact"/> + <see cref="Tolerant"/> = total matches;
/// all six values sum to the total number of comparison items.
/// </summary>
public sealed record ComparisonSummary(
    int Exact,
    int Tolerant,
    int Mismatch,
    int Missing,
    int Extra,
    int Uncomparable);

/// <summary>Complete result of a comparison run: metadata, summary counts, and classified items.</summary>
public sealed record TelemetryComparison(
    ComparisonRun Run,
    ComparisonSummary Summary,
    IReadOnlyList<ComparisonItem> Items);

/// <summary>
/// One monotonic segment in the replay-clock synchronisation log.
/// Each segment maps a wall-clock anchor to a replay-time anchor with a
/// measured speed, allowing the system to estimate the current replay
/// position from the wall clock.
/// </summary>
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

/// <summary>
/// Best-effort estimate of the current replay-clock position for one battle
/// session, derived from the most recent synchronisation segment.
/// </summary>
public sealed record ReplayClockSnapshot(
    BattleSessionId BattleSessionId,
    TimeSpan EstimatedReplayTime,
    ReplayClockQuality Quality,
    TelemetrySourceKind? Source,
    TimeSpan? Offset,
    TimeSpan? Uncertainty,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastAnchorUtc);
