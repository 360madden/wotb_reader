using System.Text.Json;
using WotBTreader.Application.Results;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Host.Cli.Cli;

/// <summary>
/// Loads the HP-diffing trusted-reader dump file — the machine contract
/// between the (future, gated) live region reader and the offline correlator
/// (<c>hp-diff</c>). Each snapshot is a FULL region dump (bytesBase64) taken
/// at a replay-clock-labeled time; the region length is declared once and
/// every dump must match it exactly (fail-closed).
/// </summary>
/// <remarks>
/// File shape:
/// <code>
/// {
///   "schema": "wotbtreader.od.hp-diff.snapshots.v1",
///   "regionLength": 256,
///   "snapshots": [
///     { "replayTimeSeconds": 900.0, "bytesBase64": "..." },
///     ...
///   ]
/// }
/// </code>
/// Replay times must be strictly increasing (the bucketer rejects
/// non-increasing clocks); region length is bounded at
/// <see cref="MaxRegionLength"/> (the live plan's ≤ 4 KB dump bound).
/// </remarks>
public static class HpDiffSnapshotsFile
{
    public const int MaxRegionLength = 4096;

    private const string ExpectedSchema = "wotbtreader.od.hp-diff.snapshots.v1";

    /// <summary>Reads and parses the snapshots file at <paramref name="path"/>.</summary>
    public static OperationResult<IReadOnlyList<RecordSnapshot>> Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return OperationResult.Failure<IReadOnlyList<RecordSnapshot>>(
                new ApplicationError(
                    "cli.hp-diff.snapshots.read",
                    $"Cannot read the snapshots file '{path}': {exception.Message}"));
        }

        return Parse(json);
    }

    /// <summary>Parses and validates snapshots JSON. Fail-closed on any
    /// unknown schema, missing field, size mismatch, or clock regression.</summary>
    public static OperationResult<IReadOnlyList<RecordSnapshot>> Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return OperationResult.Failure<IReadOnlyList<RecordSnapshot>>(
                new ApplicationError(
                    "cli.hp-diff.snapshots.malformed",
                    $"The snapshots file is not valid JSON: {exception.Message}"));
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out JsonElement schema) ||
                schema.GetString() != ExpectedSchema)
            {
                return Fail("cli.hp-diff.snapshots.schema",
                    $"Unknown snapshots schema (expected '{ExpectedSchema}').");
            }

            if (!root.TryGetProperty("regionLength", out JsonElement regionLengthElement) ||
                !regionLengthElement.TryGetInt32(out int regionLength) ||
                regionLength < sizeof(int) ||
                regionLength > MaxRegionLength ||
                regionLength % sizeof(int) != 0)
            {
                return Fail("cli.hp-diff.snapshots.region",
                    $"regionLength must be a multiple of 4 between {sizeof(int)} and {MaxRegionLength}.");
            }

            if (!root.TryGetProperty("snapshots", out JsonElement snapshotsElement) ||
                snapshotsElement.ValueKind != JsonValueKind.Array ||
                snapshotsElement.GetArrayLength() == 0)
            {
                return Fail("cli.hp-diff.snapshots.empty",
                    "The snapshots array must contain at least one dump.");
            }

            List<RecordSnapshot> snapshots = new(snapshotsElement.GetArrayLength());
            TimeSpan previous = TimeSpan.MinValue;
            foreach (JsonElement snapshot in snapshotsElement.EnumerateArray())
            {
                if (snapshot.ValueKind != JsonValueKind.Object ||
                    !snapshot.TryGetProperty("replayTimeSeconds", out JsonElement timeElement) ||
                    !timeElement.TryGetDouble(out double replaySeconds) ||
                    double.IsNaN(replaySeconds) || double.IsInfinity(replaySeconds))
                {
                    return Fail("cli.hp-diff.snapshots.time",
                        "Each snapshot needs a finite replayTimeSeconds.");
                }

                if (!snapshot.TryGetProperty("bytesBase64", out JsonElement bytesElement) ||
                    bytesElement.ValueKind != JsonValueKind.String ||
                    bytesElement.GetString() is not { } base64)
                {
                    return Fail("cli.hp-diff.snapshots.bytes",
                        "Each snapshot needs a base64 bytesBase64 payload.");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return Fail("cli.hp-diff.snapshots.bytes",
                        "bytesBase64 is not valid base64.");
                }

                if (bytes.Length != regionLength)
                {
                    return Fail("cli.hp-diff.snapshots.length",
                        $"Snapshot at {replaySeconds:0.###}s has {bytes.Length} bytes; expected {regionLength}.");
                }

                TimeSpan replayTime = TimeSpan.FromSeconds(replaySeconds);
                if (replayTime <= previous)
                {
                    return Fail("cli.hp-diff.snapshots.clock",
                        "Replay times must be strictly increasing.");
                }

                previous = replayTime;
                snapshots.Add(new RecordSnapshot(replayTime, bytes));
            }

            return OperationResult.Success<IReadOnlyList<RecordSnapshot>>(snapshots);
        }
    }

    private static OperationResult<IReadOnlyList<RecordSnapshot>> Fail(
        string code, string message) =>
        OperationResult.Failure<IReadOnlyList<RecordSnapshot>>(new ApplicationError(code, message));
}
