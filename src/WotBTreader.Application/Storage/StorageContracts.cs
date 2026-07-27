using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Storage;

public sealed record SourceImportRequest(
    string CandidatePath,
    string MediaType,
    string StoredExtension,
    long MaximumBytes);

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

/// <summary>Owns atomic, content-addressed copies of immutable source artifacts.</summary>
public interface ISourceArtifactStore
{
    ValueTask<OperationResult<SourceImportOutcome>> ImportAsync(
        SourceImportRequest request,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<Stream>> OpenReadAsync(
        SourceArtifactId artifactId,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<SourceArtifact>> GetAsync(
        SourceArtifactId artifactId,
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
