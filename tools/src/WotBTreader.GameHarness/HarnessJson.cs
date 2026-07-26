using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotBTreader.GameHarness;

internal static class HarnessJson
{
    public const string SchemaVersion = "wotb-treader.game-harness/v1";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };
}

/// <summary>
/// Stable error payload returned by every failed harness command.
/// </summary>
public sealed record HarnessError(string Code, string Message, bool Retryable);

/// <summary>
/// Stable JSON response envelope for local automation.
/// </summary>
public sealed record HarnessEnvelope(
    string SchemaVersion,
    bool Success,
    Guid CorrelationId,
    object? Data,
    IReadOnlyList<string> Warnings,
    HarnessError? Error)
{
    public static HarnessEnvelope Ok(
        Guid correlationId,
        object? data = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            HarnessJson.SchemaVersion,
            true,
            correlationId,
            data,
            warnings ?? [],
            null);

    public static HarnessEnvelope Fail(
        Guid correlationId,
        string code,
        string message,
        bool retryable = false,
        object? data = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            HarnessJson.SchemaVersion,
            false,
            correlationId,
            data,
            warnings ?? [],
            new HarnessError(code, message, retryable));
}

/// <summary>
/// A harness response paired with its stable process exit code.
/// </summary>
public sealed record HarnessCommandResult(HarnessExitCode ExitCode, HarnessEnvelope Envelope);
