using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Application.Streaming;
using WotBTreader.Core;

namespace WotBTreader.Application.Replay;

public sealed record ReplayIngestionRequest(
    string CandidatePath,
    string MediaType,
    string StoredExtension,
    long MaximumArtifactBytes,
    DecoderLimits DecoderLimits);

public sealed record ReplayIngestionOutcome(
    SourceArtifact Artifact,
    bool ArtifactAlreadyExisted,
    DecodeRunSummary DecodeRun);

/// <summary>Coordinates immutable import, probing, decoding, persistence, and post-commit publication.</summary>
public interface IReplayIngestionService
{
    ValueTask<OperationResult<ReplayIngestionOutcome>> ImportAsync(
        ReplayIngestionRequest request,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<ReplayIngestionOutcome>> ReprocessAsync(
        SourceArtifactId sourceArtifactId,
        DecoderLimits limits,
        CancellationToken cancellationToken);
}

public sealed class ReplayIngestionService : IReplayIngestionService
{
    private readonly ISourceArtifactStore _artifactStore;
    private readonly IReplayProbe _probe;
    private readonly ReplayDecoderRegistry _decoderRegistry;
    private readonly IDecodeRunRepository _decodeRuns;
    private readonly ITelemetryEventPublisher _publisher;
    private readonly IProjectionCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReplayIngestionService> _logger;

    public ReplayIngestionService(
        ISourceArtifactStore artifactStore,
        IReplayProbe probe,
        ReplayDecoderRegistry decoderRegistry,
        IDecodeRunRepository decodeRuns,
        ITelemetryEventPublisher publisher,
        IProjectionCache cache,
        TimeProvider timeProvider,
        ILogger<ReplayIngestionService> logger)
    {
        _artifactStore = artifactStore;
        _probe = probe;
        _decoderRegistry = decoderRegistry;
        _decodeRuns = decodeRuns;
        _publisher = publisher;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<OperationResult<ReplayIngestionOutcome>> ImportAsync(
        ReplayIngestionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using Activity? activity = TreaderDiagnostics.ActivitySource.StartActivity("replay.import");
        ReplayIngestionLog.ImportStarted(_logger);

        OperationResult<SourceImportOutcome> imported = await _artifactStore.ImportAsync(
            new SourceImportRequest(
                request.CandidatePath,
                request.MediaType,
                request.StoredExtension,
                request.MaximumArtifactBytes),
            cancellationToken).ConfigureAwait(false);

        if (!imported.IsSuccess || imported.Value is null)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            return OperationResult.Failure<ReplayIngestionOutcome>(
                imported.Error ?? new ApplicationError("artifact.import.failed", "Artifact import failed."),
                [.. imported.Warnings]);
        }

        OperationResult<ReplayIngestionOutcome> decoded = await DecodeArtifactAsync(
            imported.Value.Artifact,
            imported.Value.AlreadyExisted,
            request.DecoderLimits,
            cancellationToken).ConfigureAwait(false);

        if (decoded.IsSuccess)
        {
            ReplayIngestionLog.ImportCompleted(_logger, imported.Value.Artifact.Id.Value);
        }

        return decoded;
    }

    public async ValueTask<OperationResult<ReplayIngestionOutcome>> ReprocessAsync(
        SourceArtifactId sourceArtifactId,
        DecoderLimits limits,
        CancellationToken cancellationToken)
    {
        OperationResult<SourceArtifact> artifact = await _artifactStore
            .GetAsync(sourceArtifactId, cancellationToken)
            .ConfigureAwait(false);

        if (!artifact.IsSuccess || artifact.Value is null)
        {
            return OperationResult.Failure<ReplayIngestionOutcome>(
                artifact.Error ?? new ApplicationError("artifact.not_found", "Source artifact was not found."),
                [.. artifact.Warnings]);
        }

        return await DecodeArtifactAsync(
            artifact.Value,
            artifactAlreadyExisted: true,
            limits,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OperationResult<ReplayIngestionOutcome>> DecodeArtifactAsync(
        SourceArtifact artifact,
        bool artifactAlreadyExisted,
        DecoderLimits limits,
        CancellationToken cancellationToken)
    {
        ReplayInput input = new(artifact, OpenArtifactAsync);
        OperationResult<ReplayProbeResult> probeResult;
        try
        {
            probeResult = await _probe.ProbeAsync(input, limits, cancellationToken).ConfigureAwait(false);
        }
        catch (ArtifactOpenException exception)
        {
            ReplayIngestionLog.ProbeOpenFailed(_logger, exception);
            return OperationResult.Failure<ReplayIngestionOutcome>(exception.Error);
        }

        if (!probeResult.IsSuccess || probeResult.Value is null)
        {
            return OperationResult.Failure<ReplayIngestionOutcome>(
                probeResult.Error ?? new ApplicationError("replay.probe.failed", "Replay probe failed."),
                [.. probeResult.Warnings]);
        }

        OperationResult<IReplayDecoder> selection = _decoderRegistry.Select(probeResult.Value);
        if (!selection.IsSuccess || selection.Value is null)
        {
            return OperationResult.Failure<ReplayIngestionOutcome>(
                selection.Error ?? new ApplicationError("replay.decoder.unsupported", "Replay is unsupported."),
                [.. probeResult.Warnings, .. selection.Warnings]);
        }

        IReplayDecoder decoder = selection.Value;
        DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();
        DecodeRunId decodeRunId = DecodeRunId.New();
        DecodeRun running = new(
            decodeRunId,
            artifact.Id,
            decoder.Descriptor.Id,
            decoder.Descriptor.Version,
            decoder.Descriptor.SchemaVersion,
            DecodeRunStatus.Running,
            ReplayCapability.None,
            startedAtUtc,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureSummary: null);

        OperationResult<DecodeRun> started = await _decodeRuns
            .StartAsync(running, cancellationToken)
            .ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return OperationResult.Failure<ReplayIngestionOutcome>(
                started.Error ?? new ApplicationError("decode.start.failed", "Decode run could not be started."),
                [.. started.Warnings]);
        }

        using Activity? activity = TreaderDiagnostics.ActivitySource.StartActivity("replay.decode");
        activity?.SetTag("decode.run_id", decodeRunId.ToString());
        activity?.SetTag("decoder.id", decoder.Descriptor.Id);
        long startedTimestamp = Stopwatch.GetTimestamp();
        ReplayIngestionLog.DecodeStarted(_logger, decodeRunId.Value, decoder.Descriptor.Id);

        try
        {
            OperationResult<ReplayDecodeProjection> decoded = await decoder.DecodeAsync(
                new ReplayDecodeRequest(input, decodeRunId, probeResult.Value, limits),
                cancellationToken).ConfigureAwait(false);

            if (!decoded.IsSuccess || decoded.Value is null)
            {
                return await FailRunAsync(
                    artifact,
                    artifactAlreadyExisted,
                    decodeRunId,
                    decoded.Error ?? new ApplicationError("decode.failed", "Replay decode failed."),
                    decoded.Warnings,
                    DecodeRunStatus.Failed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            DateTimeOffset completedAtUtc = _timeProvider.GetUtcNow();
            ReplayDecodeProjection projection = decoded.Value with
            {
                DecodeRun = decoded.Value.DecodeRun with
                {
                    Id = decodeRunId,
                    SourceArtifactId = artifact.Id,
                    DecoderId = decoder.Descriptor.Id,
                    DecoderVersion = decoder.Descriptor.Version,
                    SchemaVersion = decoder.Descriptor.SchemaVersion,
                    Status = DecodeRunStatus.Succeeded,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    FailureCode = null,
                    FailureSummary = null,
                },
            };

            OperationResult<DecodeRunSummary> persisted = await _decodeRuns
                .CommitAsync(projection, cancellationToken)
                .ConfigureAwait(false);
            if (!persisted.IsSuccess || persisted.Value is null)
            {
                return OperationResult.Failure<ReplayIngestionOutcome>(
                    persisted.Error ?? new ApplicationError("decode.persist.failed", "Decode results were not committed."),
                    [.. persisted.Warnings]);
            }

            // Warm the projection cache so the first frame request for this
            // session never pays the full storage re-read. The projection is
            // immutable after commit; the cache is a pure performance seam.
            if (projection.Session is not null)
            {
                _cache.Store(projection.Session.Id, projection);
            }

            // Publication is a separate delivery concern. A publication failure
            // must not fail or rewrite an already-successful immutable decode run.
            // Fire-and-forget with best-effort logging; the run is already durable.
            if (projection.Session is not null && projection.Events.Count > 0)
            {
                _ = PublishTelemetryAsync(
                    projection.Session.Id,
                    projection.Events,
                    persisted.Value.DecodeRun.Id.Value);
            }

            double elapsedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            TreaderDiagnostics.DecodeDurationMilliseconds.Record(elapsedMilliseconds);
            ReplayIngestionLog.DecodeCompleted(
                _logger,
                decodeRunId.Value,
                persisted.Value.ParticipantCount,
                persisted.Value.PositionCount);

            return OperationResult.Success(
                new ReplayIngestionOutcome(artifact, artifactAlreadyExisted, persisted.Value),
                [.. probeResult.Warnings, .. decoded.Warnings, .. persisted.Warnings]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await FailRunAsync(
                artifact,
                artifactAlreadyExisted,
                decodeRunId,
                new ApplicationError("operation.cancelled", "Replay decoding was cancelled."),
                [],
                DecodeRunStatus.Cancelled,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ArtifactOpenException exception)
        {
            ReplayIngestionLog.DecodeOpenFailed(_logger, exception);
            return await FailRunAsync(
                artifact,
                artifactAlreadyExisted,
                decodeRunId,
                exception.Error,
                [],
                DecodeRunStatus.Failed,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReplayIngestionLog.UnexpectedDecodeFailure(_logger, decodeRunId.Value, exception);
            return await FailRunAsync(
                artifact,
                artifactAlreadyExisted,
                decodeRunId,
                new ApplicationError("decode.internal", "An internal decode error occurred."),
                [],
                DecodeRunStatus.Failed,
                CancellationToken.None).ConfigureAwait(false);
        }

        async ValueTask<Stream> OpenArtifactAsync(CancellationToken openCancellationToken)
        {
            OperationResult<Stream> opened = await _artifactStore
                .OpenReadAsync(artifact.Id, openCancellationToken)
                .ConfigureAwait(false);
            if (!opened.IsSuccess || opened.Value is null)
            {
                throw new ArtifactOpenException(
                    opened.Error ?? new ApplicationError(
                        "artifact.open.failed",
                        "Managed source artifact could not be opened."));
            }

            return opened.Value;
        }
    }

    private async Task PublishTelemetryAsync(
        BattleSessionId sessionId,
        IReadOnlyList<CanonicalEvent> events,
        Guid decodeRunId)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            await _publisher.PublishCommittedAsync(
                sessionId, events, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReplayIngestionLog.PublicationFailed(_logger, decodeRunId, ex.GetType().Name);
        }
    }

    private async ValueTask<OperationResult<ReplayIngestionOutcome>> FailRunAsync(
        SourceArtifact artifact,
        bool artifactAlreadyExisted,
        DecodeRunId decodeRunId,
        ApplicationError error,
        IReadOnlyList<string> warnings,
        DecodeRunStatus status,
        CancellationToken cancellationToken)
    {
        await _decodeRuns.FailAsync(
            decodeRunId,
            status,
            error.Code,
            error.Message,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        ReplayIngestionLog.DecodeRunFailed(_logger, decodeRunId.Value, error.Code);
        return OperationResult.Failure<ReplayIngestionOutcome>(
            error,
            [.. warnings, $"Artifact retained: {artifact.Id}; duplicate: {artifactAlreadyExisted}."]);
    }

    private sealed class ArtifactOpenException : Exception
    {
        public ArtifactOpenException(ApplicationError error)
            : base(error.Message)
        {
            Error = error;
        }

        public ApplicationError Error { get; }
    }
}
