using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Host.Cli.Cli;

/// <summary>
/// Dispatches one CLI command to the appropriate handler. Every command
/// handler validates positional arguments, calls the relevant application
/// port, and returns a <see cref="CliExecution"/> with a machine-readable
/// JSON envelope and a human-readable message.
/// </summary>
/// <remarks>
/// <para>Output routing (stdout vs stderr) is decided by
/// <see cref="CliEntryPoint"/>, not by this class. Handlers only produce
/// the <see cref="CliExecution"/>; they never write to streams directly.</para>
/// <para>Progress for long-running commands (e.g. <c>watch</c>) is reported
/// through <see cref="ILogger{CliCommandRouter}"/>, which is configured to
/// emit to stderr so it never corrupts the stdout envelope.</para>
/// </remarks>
public sealed class CliCommandRouter
{
    private static readonly string[] CommandNames =
    [
        "doctor",
        "import",
        "inspect",
        "reprocess",
        "compare",
        "export",
        "sessions",
        "watch",
        "serve",
    ];

    private readonly IDoctorService _doctor;
    private readonly IReplayIngestionService _ingestion;
    private readonly IDecodeRunRepository _decodeRuns;
    private readonly ISessionQueryRepository _sessions;
    private readonly IComparisonRunRepository _comparisons;
    private readonly ITelemetryComparator _comparator;
    private readonly ILogger<CliCommandRouter> _logger;

    /// <summary>Creates a command router with all application ports resolved by DI.</summary>
    public CliCommandRouter(
        IDoctorService doctor,
        IReplayIngestionService ingestion,
        IDecodeRunRepository decodeRuns,
        ISessionQueryRepository sessions,
        IComparisonRunRepository comparisons,
        ITelemetryComparator comparator,
        ILogger<CliCommandRouter> logger)
    {
        _doctor = doctor;
        _ingestion = ingestion;
        _decodeRuns = decodeRuns;
        _sessions = sessions;
        _comparisons = comparisons;
        _comparator = comparator;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches the parsed invocation to the matching command handler.
    /// Unknown commands return <see cref="CliExitCode.InvalidArguments"/>;
    /// reserved-but-unavailable commands return
    /// <see cref="CliExitCode.UnsupportedCapability"/>.
    /// </summary>
    public async ValueTask<CliExecution> ExecuteAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return invocation.Command switch
        {
            "doctor" => await DoctorAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "import" => await ImportAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "inspect" => await InspectAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "reprocess" => await ReprocessAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "sessions" => await SessionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "compare" => await CompareAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "export" => await ExportAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "watch" => await WatchAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "serve" => Unsupported(invocation.Command, correlationId),
            _ => Invalid(
                "cli.command.unknown",
                $"Unknown command '{invocation.Command}'. Available commands: {string.Join(", ", CommandNames)}.",
                correlationId),
        };
    }

    /// <summary>Runs non-mutating health checks and returns the report.</summary>
    private async ValueTask<CliExecution> DoctorAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 0)
        {
            return Invalid("cli.doctor.arguments", "doctor accepts no positional arguments.", correlationId);
        }

        DoctorReport report = await _doctor.RunAsync(cancellationToken).ConfigureAwait(false);
        bool healthy = report.Checks.Where(static check => check.Required)
            .All(static check => string.Equals(check.Status, "pass", StringComparison.Ordinal));
        return healthy
            ? Success(report, "Doctor checks passed.", correlationId)
            : Failure(
                CliExitCode.InvalidInput,
                "doctor.required_check_failed",
                "One or more required doctor checks failed.",
                data: report,
                correlationId);
    }

    /// <summary>
    /// Imports one <c>.wotbreplay</c> file into content-addressed storage
    /// and decodes it. The same file imported twice produces two distinct
    /// decode runs sharing one artifact (evidence-first reprocessing rule).
    /// </summary>
    private async ValueTask<CliExecution> ImportAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid("cli.import.path_required", "import requires exactly one replay path.", correlationId);
        }

        string candidatePath = invocation.Positionals[0];
        if (!string.Equals(Path.GetExtension(candidatePath), ".wotbreplay", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(
                "cli.import.extension",
                "import accepts a .wotbreplay file.",
                correlationId,
                CliExitCode.InvalidInput);
        }

        OperationResult<ReplayIngestionOutcome> result = await _ingestion.ImportAsync(
            new ReplayIngestionRequest(
                candidatePath,
                "application/vnd.wotblitz.replay",
                ".wotbreplay",
                MaximumArtifactBytes: 128 * 1024 * 1024,
                DecoderLimits.Default),
            cancellationToken).ConfigureAwait(false);

        return FromResult(
            result,
            correlationId,
            result.Value is null
                ? "Replay import failed."
                : $"Imported artifact {result.Value.Artifact.Id}; decode run {result.Value.DecodeRun.DecodeRun.Id}.");
    }

    /// <summary>Looks up one decode run by its GUID and returns the summary.</summary>
    private async ValueTask<CliExecution> InspectAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1 ||
            !Guid.TryParse(invocation.Positionals[0], out Guid value))
        {
            return Invalid(
                "cli.inspect.decode_run_id",
                "inspect requires one decode-run GUID.",
                correlationId);
        }

        OperationResult<DecodeRunSummary> result = await _decodeRuns
            .GetAsync(new DecodeRunId(value), cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Decode run loaded.");
    }

    /// <summary>
    /// Re-decodes a source artifact that was previously imported, creating
    /// a new decode run. Useful after a decoder update to reprocess old
    /// replays with the latest logic.
    /// </summary>
    private async ValueTask<CliExecution> ReprocessAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1 ||
            !Guid.TryParse(invocation.Positionals[0], out Guid value))
        {
            return Invalid(
                "cli.reprocess.artifact_id",
                "reprocess requires one source-artifact GUID.",
                correlationId);
        }

        OperationResult<ReplayIngestionOutcome> result = await _ingestion
            .ReprocessAsync(new SourceArtifactId(value), DecoderLimits.Default, cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Replay reprocessing completed.");
    }

    /// <summary>
    /// Lists decoded battle sessions with offset/limit paging (default 50,
    /// max 200). Use <c>--offset</c> and <c>--limit</c> options.
    /// </summary>
    private async ValueTask<CliExecution> SessionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 0)
        {
            return Invalid("cli.sessions.arguments", "sessions accepts no positional arguments.", correlationId);
        }

        if (!TryGetInteger(invocation.Options, "offset", defaultValue: 0, minimum: 0, maximum: int.MaxValue, out int offset) ||
            !TryGetInteger(invocation.Options, "limit", defaultValue: 50, minimum: 1, maximum: 200, out int limit))
        {
            return Invalid(
                "cli.sessions.range",
                "sessions --offset must be non-negative and --limit must be between 1 and 200.",
                correlationId);
        }

        IReadOnlyList<DecodeRunSummary> sessions = await _sessions
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);
        return Success(sessions, $"Loaded {sessions.Count} session(s).", correlationId);
    }

    /// <summary>
    /// Dispatches comparison sub-commands. Supported: <c>list</c> (paged list
    /// of comparison runs), <c>inspect</c> (full result for one run),
    /// <c>create</c> (compare two battle sessions).
    /// </summary>
    private async ValueTask<CliExecution> CompareAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count == 0)
        {
            return Invalid(
                "cli.compare.subcommand_required",
                "Usage: compare list | compare inspect <comparison-run-id> | compare create <left-session-id> <right-session-id>.",
                correlationId);
        }

        string subCommand = invocation.Positionals[0];
        return subCommand switch
        {
            "list" => await CompareListAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "inspect" => await CompareInspectAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "create" => await CompareCreateAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            _ => Invalid(
                "cli.compare.unknown_subcommand",
                $"Unknown compare subcommand '{subCommand}'. Available: list, inspect, create.",
                correlationId),
        };
    }

    /// <summary>Lists comparison runs with paging (default 50, max 200).</summary>
    private async ValueTask<CliExecution> CompareListAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!TryGetInteger(invocation.Options, "offset", defaultValue: 0, minimum: 0, maximum: int.MaxValue, out int offset) ||
            !TryGetInteger(invocation.Options, "limit", defaultValue: 50, minimum: 1, maximum: 200, out int limit))
        {
            return Invalid(
                "cli.compare.list.range",
                "compare list --offset must be non-negative and --limit must be between 1 and 200.",
                correlationId);
        }

        IReadOnlyList<ComparisonRun> runs = await _comparisons
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);
        return Success(runs, $"Loaded {runs.Count} comparison run(s).", correlationId);
    }

    /// <summary>Returns the full comparison result (metadata, summary, items) for one run.</summary>
    private async ValueTask<CliExecution> CompareInspectAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.compare.inspect.id_required",
                "compare inspect requires one comparison-run GUID.",
                correlationId);
        }

        OperationResult<TelemetryComparison> result = await _comparisons
            .GetAsync(new ComparisonRunId(value), cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Comparison run loaded.");
    }

    /// <summary>
    /// Compares the telemetry events of two decoded battle sessions and
    /// persists the result as a new comparison run. Returns the new
    /// comparison run ID and summary on success.
    /// </summary>
    private async ValueTask<CliExecution> CompareCreateAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 3 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid leftGuid) ||
            !Guid.TryParse(invocation.Positionals[2], out Guid rightGuid))
        {
            return Invalid(
                "cli.compare.create.two_ids_required",
                "compare create requires two battle-session GUIDs (left and right).",
                correlationId);
        }

        BattleSessionId leftSessionId = new(leftGuid);
        BattleSessionId rightSessionId = new(rightGuid);

        OperationResult<ReplayDecodeProjection> leftResult = await _sessions
            .GetProjectionAsync(leftSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!leftResult.IsSuccess || leftResult.Value is null)
        {
            return FromResult(leftResult, correlationId, "Failed to load left session.");
        }

        OperationResult<ReplayDecodeProjection> rightResult = await _sessions
            .GetProjectionAsync(rightSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!rightResult.IsSuccess || rightResult.Value is null)
        {
            return FromResult(rightResult, correlationId, "Failed to load right session.");
        }

        ReplayDecodeProjection leftProjection = leftResult.Value;
        ReplayDecodeProjection rightProjection = rightResult.Value;

        SourceArtifactId leftArtifactId = leftProjection.DecodeRun.SourceArtifactId;
        SourceArtifactId rightArtifactId = rightProjection.DecodeRun.SourceArtifactId;

        IReadOnlyList<TelemetryEvent> leftEvents = ConvertToTelemetryEvents(
            leftProjection.Events,
            leftProjection.DecodeRun.DecoderId,
            leftArtifactId);
        IReadOnlyList<TelemetryEvent> rightEvents = ConvertToTelemetryEvents(
            rightProjection.Events,
            rightProjection.DecodeRun.DecoderId,
            rightArtifactId);

        OperationResult<TelemetryComparison> comparisonResult = await _comparator.CompareAsync(
            leftArtifactId,
            leftEvents,
            rightArtifactId,
            rightEvents,
            ComparisonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        if (!comparisonResult.IsSuccess || comparisonResult.Value is null)
        {
            return FromResult(comparisonResult, correlationId, "Comparison failed.");
        }

        OperationResult<TelemetryComparison> saved = await _comparisons
            .AddAsync(comparisonResult.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!saved.IsSuccess || saved.Value is null)
        {
            return FromResult(saved, correlationId, "Comparison created but could not be persisted.");
        }

        var data = new
        {
            comparisonRunId = saved.Value.Run.Id.ToString(),
            saved.Value.Run.ComparatorId,
            saved.Value.Run.ComparatorVersion,
            leftSessionId = leftGuid.ToString("D"),
            rightSessionId = rightGuid.ToString("D"),
            summary = saved.Value.Summary,
        };

        return Success(
            data,
            $"Created comparison run {saved.Value.Run.Id}.",
            correlationId);
    }

    /// <summary>
    /// Converts <see cref="CanonicalEvent"/> records decoded from a replay
    /// into <see cref="TelemetryEvent"/> records that the comparator can
    /// process. The <paramref name="decoderId"/> and
    /// <paramref name="sourceArtifactId"/> preserve provenance chain.
    /// </summary>
    private static TelemetryEvent[] ConvertToTelemetryEvents(
        IReadOnlyList<CanonicalEvent> canonicalEvents,
        string decoderId,
        SourceArtifactId sourceArtifactId)
    {
        TelemetryProvenance provenance = new(
            TelemetrySourceKind.Replay,
            decoderId,
            sourceArtifactId,
            null,
            null);

        TelemetryEvent[] result = new TelemetryEvent[canonicalEvents.Count];
        for (int i = 0; i < canonicalEvents.Count; i++)
        {
            CanonicalEvent ce = canonicalEvents[i];
            result[i] = new TelemetryEvent(
                ce.Sequence,
                null, // SourceTimeUtc — not available in canonical replay events
                ce.ReplayTime,
                ce.Kind.ToString(),
                ce.ParticipantId?.ToString(),
                ce.EntityId,
                ce.ValuesJson,
                provenance);
        }

        return result;
    }

    /// <summary>
    /// Dispatches export sub-commands. Supported: <c>sessions</c> (events as
    /// structured JSON), <c>positions</c> (position samples as structured JSON).
    /// </summary>
    private async ValueTask<CliExecution> ExportAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count == 0)
        {
            return Invalid(
                "cli.export.subcommand_required",
                "Usage: export sessions <battle-session-id> | export positions <battle-session-id>.",
                correlationId);
        }

        string subCommand = invocation.Positionals[0];
        return subCommand switch
        {
            "sessions" => await ExportSessionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "positions" => await ExportPositionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            _ => Invalid(
                "cli.export.unknown_subcommand",
                $"Unknown export subcommand '{subCommand}'. Available: sessions, positions.",
                correlationId),
        };
    }

    /// <summary>
    /// Exports all decoded events for a battle session as structured JSON
    /// with sequence, kind, replay time, participant/entity IDs, and values.
    /// </summary>
    private async ValueTask<CliExecution> ExportSessionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.export.sessions.id_required",
                "export sessions requires one battle-session GUID.",
                correlationId);
        }

        OperationResult<ReplayDecodeProjection> result = await _sessions
            .GetProjectionAsync(new BattleSessionId(value), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return FromResult(result, correlationId, "Export failed.");
        }

        var data = result.Value.Events
            .Select(static e => new
            {
                sequence = e.Sequence,
                kind = e.Kind,
                replayTimeMs = e.ReplayTime.TotalMilliseconds,
                participantId = e.ParticipantId,
                entityId = e.EntityId,
                values = e.ValuesJson,
            })
            .ToList();

        return Success(
            new { sessionId = value.ToString("D"), count = data.Count, events = data },
            $"Exported {data.Count} event(s).",
            correlationId,
            result.Warnings);
    }

    /// <summary>
    /// Exports all position samples for a battle session as structured JSON
    /// with sequence, replay time, participant/entity IDs, raw coordinates,
    /// and coordinate space.
    /// </summary>
    private async ValueTask<CliExecution> ExportPositionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.export.positions.id_required",
                "export positions requires one battle-session GUID.",
                correlationId);
        }

        OperationResult<ReplayDecodeProjection> result = await _sessions
            .GetProjectionAsync(new BattleSessionId(value), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return FromResult(result, correlationId, "Export failed.");
        }

        var data = result.Value.Positions
            .Select(static p => new
            {
                sequence = p.Sequence,
                replayTimeMs = p.ReplayTime.TotalMilliseconds,
                participantId = p.ParticipantId,
                entityId = p.EntityId,
                rawX = p.RawX,
                rawY = p.RawY,
                rawZ = p.RawZ,
                coordinateSpace = p.RawCoordinateSpace,
            })
            .ToList();

        return Success(
            new { sessionId = value.ToString("D"), count = data.Count, positions = data },
            $"Exported {data.Count} position(s).",
            correlationId,
            result.Warnings);
    }

    /// <summary>
    /// Monitors a directory for new <c>.wotbreplay</c> files and auto-imports
    /// each one. Uses <see cref="FileSystemWatcher"/> as a low-latency hint
    /// and periodic directory enumeration as source of truth, matching the
    /// pattern used by <c>BlitzReplayLogMonitor</c>.
    /// </summary>
    /// <remarks>
    /// <para>Existing files in the directory are imported on startup
    /// (idempotent). Each new file gets a 2-second stability delay before
    /// import to allow the writer to finish flushing.</para>
    /// <para>Press Ctrl+C to stop watching. The command returns a summary
    /// with the directory, elapsed time, and import/error counts.</para>
    /// </remarks>
    private async ValueTask<CliExecution> WatchAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid(
                "cli.watch.directory_required",
                "watch requires exactly one directory path.",
                correlationId);
        }

        string directory = Path.GetFullPath(invocation.Positionals[0]);
        if (!Directory.Exists(directory))
        {
            return Invalid(
                "cli.watch.directory_missing",
                $"Directory '{directory}' does not exist.",
                correlationId);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Watching {Directory} for new .wotbreplay files…", directory);
        }

        int importedCount = 0;
        int errorCount = 0;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        ConcurrentDictionary<string, bool> processed = new(StringComparer.OrdinalIgnoreCase);

        using FileSystemWatcher watcher = new(directory, "*.wotbreplay")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        TaskCompletionSource<bool> fileDetected = new();
        void OnCreated(object _, FileSystemEventArgs e)
        {
            fileDetected.TrySetResult(true);
        }

        void OnError(object _, ErrorEventArgs e)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4200, "WatchError"),
                    "File system watcher error: {ExceptionType}.",
                    e.GetException().GetType().Name);
            }

            fileDetected.TrySetResult(true);
        }

        watcher.Created += OnCreated;
        watcher.Error += OnError;

        // Enumerate files that already exist in the directory (idempotent).
        try
        {
            foreach (string existing in Directory.EnumerateFiles(
                         directory, "*.wotbreplay", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = Path.GetFullPath(existing);
                if (!processed.TryAdd(normalized, true))
                {
                    continue;
                }

                bool ok = await ImportFileAsync(normalized, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    importedCount++;
                    fileDetected.TrySetResult(true);
                }
                else
                {
                    errorCount++;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4203, "InitialEnumerationError"),
                    "Could not enumerate existing files: {ExceptionType}.",
                    exception.GetType().Name);
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task completed = await Task.WhenAny(
                        fileDetected.Task,
                        Task.Delay(Timeout.Infinite, linked.Token))
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Drain the completion source.
                fileDetected = new TaskCompletionSource<bool>();

                // Scan for new files.
                try
                {
                    foreach (string candidate in Directory.EnumerateFiles(
                                 directory, "*.wotbreplay", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string normalized = Path.GetFullPath(candidate);
                        if (!processed.TryAdd(normalized, true))
                        {
                            continue;
                        }

                        bool ok = await ImportFileAsync(normalized, cancellationToken)
                            .ConfigureAwait(false);
                        if (ok)
                        {
                            importedCount++;
                        }
                        else
                        {
                            errorCount++;
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    return Failure(
                        CliExitCode.InternalFailure,
                        "cli.watch.directory_removed",
                        $"Directory '{directory}' was removed while watching.",
                        data: null,
                        correlationId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher.Created -= OnCreated;
            watcher.Error -= OnError;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            string elapsedString = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            _logger.LogInformation(
                "Watched {Directory} for {Elapsed}. Imported {ImportedCount}, {ErrorCount} error(s).",
                directory,
                elapsedString,
                importedCount,
                errorCount);
        }

        return Success(
            new
            {
                directory,
                elapsed = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                importedCount,
                errorCount,
            },
            $"Imported {importedCount} replay(s) with {errorCount} error(s) in {elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}.",
            correlationId);
    }

    /// <summary>
    /// Imports one replay file after a 2-second stability delay. Returns
    /// <see langword="true"/> on success, <see langword="false"/> on failure
    /// (the caller is responsible for counting). Does not throw on import
    /// failures — those are logged and counted as errors.
    /// </summary>
    private async ValueTask<bool> ImportFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // Wait for the file to stabilise (the writer may still be flushing).
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        string fileName = Path.GetFileName(path);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Importing {FileName}…", fileName);
        }

        try
        {
            OperationResult<ReplayIngestionOutcome> result = await _ingestion.ImportAsync(
                new ReplayIngestionRequest(
                    path,
                    "application/vnd.wotblitz.replay",
                    ".wotbreplay",
                    MaximumArtifactBytes: 128 * 1024 * 1024,
                    DecoderLimits.Default),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    ReplayIngestionOutcome outcome = result.Value!;
                    _logger.LogInformation(
                        "Imported {FileName} → artifact {ArtifactId}, decode run {DecodeRunId}.",
                        fileName,
                        outcome.Artifact.Id,
                        outcome.DecodeRun.DecodeRun.Id);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    ApplicationError? error = result.Error;
                    _logger.LogWarning(
                        new EventId(4201, "ImportFailed"),
                        "Failed to import {FileName}: {ErrorCode} — {ErrorMessage}.",
                        fileName,
                        error?.Code,
                        error?.Message);
                }
            }

            return result.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4202, "ImportException"),
                    "Exception importing {FileName}: {ExceptionType} — {Message}.",
                    fileName,
                    exception.GetType().Name,
                    exception.Message);
            }

            return false;
        }
    }

    /// <summary>
    /// Parses an integer option with bounds checking. Returns
    /// <see langword="false"/> when the value is present but unparseable
    /// or out of range; the caller is responsible for producing the
    /// appropriate error envelope.
    /// </summary>
    private static bool TryGetInteger(
        IReadOnlyDictionary<string, string?> options,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        out int result)
    {
        if (!options.TryGetValue(key, out string? raw))
        {
            result = defaultValue;
            return true;
        }

        return int.TryParse(raw, out result) && result >= minimum && result <= maximum;
    }

    /// <summary>
    /// Maps a successful or failed <see cref="OperationResult{T}"/> into a
    /// <see cref="CliExecution"/>. Failures are routed through
    /// <see cref="MapExitCode"/> for stable exit-code classification.
    /// </summary>
    private static CliExecution FromResult<T>(
        OperationResult<T> result,
        Guid correlationId,
        string successMessage)
    {
        if (result.IsSuccess)
        {
            return Success(result.Value, successMessage, correlationId, result.Warnings);
        }

        ApplicationError error = result.Error ??
            new ApplicationError("internal.unknown", "An unknown application error occurred.");
        return Failure(
            MapExitCode(error.Code),
            error.Code,
            error.Message,
            data: null,
            correlationId,
            error.Retryable,
            result.Warnings);
    }

    /// <summary>Builds a successful CLI execution envelope.</summary>
    private static CliExecution Success(
        object? data,
        string message,
        Guid correlationId,
        IReadOnlyList<string>? warnings = null) =>
        new(
            CliExitCode.Success,
            new CliEnvelope(
                "1",
                Success: true,
                correlationId,
                data,
                warnings ?? [],
                Errors: []),
            message);

    /// <summary>Builds a failure envelope for argument-validation errors.</summary>
    private static CliExecution Invalid(
        string code,
        string message,
        Guid correlationId,
        CliExitCode exitCode = CliExitCode.InvalidArguments) =>
        Failure(exitCode, code, message, data: null, correlationId);

    /// <summary>
    /// Builds a failure envelope for commands that are reserved but not
    /// yet implemented in this milestone.
    /// </summary>
    private static CliExecution Unsupported(string command, Guid correlationId) =>
        Failure(
            CliExitCode.UnsupportedCapability,
            $"cli.{command}.not_available",
            $"The '{command}' command is reserved but not available in this milestone.",
            data: null,
            correlationId);

    /// <summary>Builds a general failure CLI execution envelope.</summary>
    private static CliExecution Failure(
        CliExitCode exitCode,
        string code,
        string message,
        object? data,
        Guid correlationId,
        bool retryable = false,
        IReadOnlyList<string>? warnings = null) =>
        new(
            exitCode,
            new CliEnvelope(
                "1",
                Success: false,
                correlationId,
                data,
                warnings ?? [],
                [new CliError(code, message, retryable)]),
            message);

    /// <summary>
    /// Maps stable application error codes to CLI exit codes by keyword
    /// matching, so the envelope stays deterministic even when a new
    /// error code is added.
    /// </summary>
    private static CliExitCode MapExitCode(string errorCode)
    {
        if (errorCode.Contains("cancelled", StringComparison.Ordinal))
        {
            return CliExitCode.Cancelled;
        }

        if (errorCode.Contains("unsupported", StringComparison.Ordinal))
        {
            return CliExitCode.UnsupportedCapability;
        }

        if (errorCode.Contains("busy", StringComparison.Ordinal) ||
            errorCode.Contains("conflict", StringComparison.Ordinal) ||
            errorCode.Contains("already_exists", StringComparison.Ordinal))
        {
            return CliExitCode.ConflictOrBusy;
        }

        if (errorCode.Contains("invalid", StringComparison.Ordinal) ||
            errorCode.Contains("not_found", StringComparison.Ordinal) ||
            errorCode.Contains("malformed", StringComparison.Ordinal))
        {
            return CliExitCode.InvalidInput;
        }

        return CliExitCode.InternalFailure;
    }
}
