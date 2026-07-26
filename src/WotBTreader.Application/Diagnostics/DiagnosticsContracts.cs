using WotBTreader.Application.Results;

namespace WotBTreader.Application.Diagnostics;

public sealed record DiagnosticCheck(
    string Id,
    string Status,
    string Summary,
    bool Required,
    IReadOnlyDictionary<string, string> Data);

public sealed record DoctorReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DiagnosticCheck> Checks);

public sealed record DiagnosticBundleOptions(
    bool IncludeDatabase,
    bool IncludeSourceArtifacts,
    bool IncludeScreenshots);

/// <summary>Runs non-mutating environment and application health checks.</summary>
public interface IDoctorService
{
    ValueTask<DoctorReport> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Exports redacted diagnostics; sensitive artifacts require explicit opt-in.</summary>
public interface IDiagnosticBundleService
{
    ValueTask<OperationResult<string>> CreateAsync(
        DiagnosticBundleOptions options,
        CancellationToken cancellationToken);
}
