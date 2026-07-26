using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Host.Cli.Cli;

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

    public CliCommandRouter(
        IDoctorService doctor,
        IReplayIngestionService ingestion,
        IDecodeRunRepository decodeRuns,
        ISessionQueryRepository sessions)
    {
        _doctor = doctor;
        _ingestion = ingestion;
        _decodeRuns = decodeRuns;
        _sessions = sessions;
    }

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
            "compare" or "export" or "watch" or "serve" => Unsupported(invocation.Command, correlationId),
            _ => Invalid(
                "cli.command.unknown",
                $"Unknown command '{invocation.Command}'. Available commands: {string.Join(", ", CommandNames)}.",
                correlationId),
        };
    }

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

    private static CliExecution Invalid(
        string code,
        string message,
        Guid correlationId,
        CliExitCode exitCode = CliExitCode.InvalidArguments) =>
        Failure(exitCode, code, message, data: null, correlationId);

    private static CliExecution Unsupported(string command, Guid correlationId) =>
        Failure(
            CliExitCode.UnsupportedCapability,
            $"cli.{command}.not_available",
            $"The '{command}' command is reserved but not available in this milestone.",
            data: null,
            correlationId);

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
