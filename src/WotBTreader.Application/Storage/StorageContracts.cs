using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Parameters for importing a source artifact into the content-addressed store.
/// </summary>
/// <param name="CandidatePath">File system path to the source file to import.</param>
/// <param name="MediaType">MIME type or application-specific media type identifier.</param>
/// <param name="StoredExtension">File extension to use in the content store.</param>
/// <param name="MaximumBytes">Maximum allowed file size in bytes.</param>
public sealed record SourceImportRequest(
    string CandidatePath,
    string MediaType,
    string StoredExtension,
    long MaximumBytes);

/// <summary>
/// Result of a source artifact import operation.
/// </summary>
/// <param name="Artifact">The imported (or pre-existing) artifact record.</param>
/// <param name="AlreadyExisted">True if the content was already in storage (deduplicated).</param>
public sealed record SourceImportOutcome(
    SourceArtifact Artifact,
    bool AlreadyExisted);

public sealed record DecodeRunSummary(
    DecodeRun DecodeRun,
    BattleSession? Session,
    int ParticipantCount,
    int PositionCount,
    int EventCount,
    int RawRecordCount);

/// <summary>
/// Owns atomic, content-addressed copies of immutable source artifacts.
/// Every artifact is stored by its SHA-256 hash and never modified after import.
/// </summary>
public interface ISourceArtifactStore
{
    /// <summary>
    /// Imports a candidate file into the content-addressed store.
    /// Returns the existing artifact if the content hash already exists (deduplication).
    /// </summary>
    ValueTask<OperationResult<SourceImportOutcome>> ImportAsync(
        SourceImportRequest request,
        CancellationToken cancellationToken);

    /// <summary>Opens a read-only stream for the artifact content.</summary>
    ValueTask<OperationResult<Stream>> OpenReadAsync(
        SourceArtifactId artifactId,
        CancellationToken cancellationToken);

    /// <summary>Retrieves the artifact metadata record by ID.</summary>
    ValueTask<OperationResult<SourceArtifact>> GetAsync(
        SourceArtifactId artifactId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Identifies content-addressed objects on disk that have no corresponding
    /// source_artifacts row so a reconciler can safely delete or re-index them.
    /// Never deletes referenced or recently in-flight content.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListUnreferencedContentHashesAsync(
        CancellationToken cancellationToken);
}

/// <summary>Persists decode evidence atomically and never overwrites an existing run.</summary>
public interface IDecodeRunRepository
{
    ValueTask<OperationResult<DecodeRun>> StartAsync(
        DecodeRun decodeRun,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<DecodeRunSummary>> CommitAsync(
        ReplayDecodeProjection projection,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<DecodeRun>> FailAsync(
        DecodeRunId decodeRunId,
        DecodeRunStatus finalStatus,
        string failureCode,
        string failureSummary,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<DecodeRunSummary>> GetAsync(
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken);
}

/// <summary>Provides read models used by local hosts without exposing storage internals.</summary>
public interface ISessionQueryRepository
{
    ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns map boundaries computed from all position samples across every
    /// imported replay, grouped by map ID. An empty list means no position data
    /// has been imported yet.
    /// </summary>
    ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
        CancellationToken cancellationToken);
}

/// <summary>Initializes and migrates application storage before dependent services start.</summary>
public interface IStorageInitializer
{
    ValueTask<OperationResult<int>> InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>Persists immutable comparison runs and their classified items.</summary>
public interface IComparisonRunRepository
{
    ValueTask<IReadOnlyList<ComparisonRun>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<TelemetryComparison>> AddAsync(
        TelemetryComparison comparison,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<TelemetryComparison>> GetAsync(
        ComparisonRunId comparisonRunId,
        CancellationToken cancellationToken);
}

/// <summary>Appends and reads immutable monotonic replay-clock synchronization segments.</summary>
public interface IReplayClockSegmentRepository
{
    ValueTask<OperationResult<ReplayClockSegment>> AppendAsync(
        ReplayClockSegment segment,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<IReadOnlyList<ReplayClockSegment>>> ListAsync(
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken);
}
