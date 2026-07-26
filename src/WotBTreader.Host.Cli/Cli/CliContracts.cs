namespace WotBTreader.Host.Cli.Cli;

public enum CliExitCode
{
    Success = 0,
    InternalFailure = 1,
    InvalidArguments = 2,
    UnsupportedCapability = 3,
    InvalidInput = 4,
    ConflictOrBusy = 5,
}

public sealed record CliError(
    string Code,
    string Message,
    bool Retryable);

public sealed record CliEnvelope(
    string SchemaVersion,
    bool Success,
    Guid CorrelationId,
    object? Data,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CliError> Errors);

public sealed record CliExecution(
    CliExitCode ExitCode,
    CliEnvelope Envelope,
    string HumanMessage);
