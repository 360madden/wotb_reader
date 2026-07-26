using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Ndjson;

public sealed class NdjsonTelemetrySource : ITelemetrySource
{
    private const string SupportedSchemaVersion = "1";

    public string SourceId => "wotbtreader.capture.ndjson";

    public async IAsyncEnumerable<OperationResult<TelemetryEvent>> ReadAsync(
        Stream source,
        TelemetryReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        long eventCount = 0;
        long started = Stopwatch.GetTimestamp();
        long lineNumber = 0;
        IAsyncEnumerable<BoundedUtf8Line> lines = BoundedUtf8LineReader.ReadAsync(
            source,
            options.MaximumLineBytes,
            cancellationToken);

        await foreach (BoundedUtf8Line lineResult in lines
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (lineResult.LimitExceeded || lineResult.Bytes is null)
            {
                yield return Failure(
                    "capture.limit.line",
                    "A telemetry line exceeded the configured byte limit.");
                yield break;
            }

            byte[] line = lineResult.Bytes;
            lineNumber++;
            if (Stopwatch.GetElapsedTime(started) > options.MaximumDuration)
            {
                yield return Failure(
                    "capture.limit.duration",
                    "Telemetry parsing exceeded the configured duration.");
                yield break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            eventCount++;
            if (eventCount > options.MaximumEventCount)
            {
                yield return Failure(
                    "capture.limit.events",
                    "Telemetry event count exceeded the configured limit.");
                yield break;
            }

            ReadOnlyMemory<byte> json = lineNumber == 1 &&
                                        line.Length >= 3 &&
                                        line[0] == 0xEF &&
                                        line[1] == 0xBB &&
                                        line[2] == 0xBF
                ? line.AsMemory(3)
                : line;
            yield return Parse(json, lineNumber);
        }
    }

    private static OperationResult<TelemetryEvent> Parse(
        ReadOnlyMemory<byte> json,
        long lineNumber)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Malformed(lineNumber, "Telemetry lines must be JSON objects.");
            }

            if (!TryGetString(root, "schemaVersion", out string? schemaVersion) ||
                !string.Equals(schemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            {
                return OperationResult.Failure<TelemetryEvent>(
                    new ApplicationError(
                        "capture.schema.unsupported",
                        $"Telemetry line {lineNumber} uses an unsupported schema version."));
            }

            if (!root.TryGetProperty("sourceSequence", out JsonElement sequenceElement) ||
                !sequenceElement.TryGetInt64(out long sourceSequence) ||
                sourceSequence < 0)
            {
                return Malformed(lineNumber, "sourceSequence must be a non-negative integer.");
            }

            if (!TryGetString(root, "eventType", out string? eventType) ||
                string.IsNullOrWhiteSpace(eventType) ||
                eventType.Length > 128)
            {
                return Malformed(lineNumber, "eventType must be a non-empty string of at most 128 characters.");
            }

            DateTimeOffset? sourceTimeUtc = null;
            if (root.TryGetProperty("sourceTimeUtc", out JsonElement sourceTimeElement) &&
                sourceTimeElement.ValueKind != JsonValueKind.Null)
            {
                if (sourceTimeElement.ValueKind != JsonValueKind.String ||
                    !sourceTimeElement.TryGetDateTimeOffset(out DateTimeOffset parsedSourceTime))
                {
                    return Malformed(lineNumber, "sourceTimeUtc must be an ISO-8601 timestamp or null.");
                }

                sourceTimeUtc = parsedSourceTime.ToUniversalTime();
            }

            TimeSpan? replayTime = null;
            if (root.TryGetProperty("replayTimeMs", out JsonElement replayTimeElement) &&
                replayTimeElement.ValueKind != JsonValueKind.Null)
            {
                if (!replayTimeElement.TryGetDouble(out double replayMilliseconds) ||
                    !double.IsFinite(replayMilliseconds) ||
                    replayMilliseconds < 0)
                {
                    return Malformed(lineNumber, "replayTimeMs must be a finite non-negative number or null.");
                }

                replayTime = TimeSpan.FromMilliseconds(replayMilliseconds);
            }

            string? participantIdentity = GetOptionalString(root, "participantIdentity");
            long? entityId = null;
            if (root.TryGetProperty("entityId", out JsonElement entityElement) &&
                entityElement.ValueKind != JsonValueKind.Null)
            {
                if (!entityElement.TryGetInt64(out long parsedEntityId))
                {
                    return Malformed(lineNumber, "entityId must be an integer or null.");
                }

                entityId = parsedEntityId;
            }

            string valuesJson = root.TryGetProperty("values", out JsonElement values)
                ? values.GetRawText()
                : "{}";
            if (!IsObject(valuesJson))
            {
                return Malformed(lineNumber, "values must be a JSON object.");
            }

            string sourceVersion = SupportedSchemaVersion;
            string? detail = null;
            if (root.TryGetProperty("provenance", out JsonElement provenance) &&
                provenance.ValueKind != JsonValueKind.Null)
            {
                if (provenance.ValueKind != JsonValueKind.Object)
                {
                    return Malformed(lineNumber, "provenance must be an object or null.");
                }

                sourceVersion = GetOptionalString(provenance, "sourceVersion") ?? SupportedSchemaVersion;
                detail = GetOptionalString(provenance, "detail");
            }

            TelemetryEvent telemetryEvent = new(
                sourceSequence,
                sourceTimeUtc,
                replayTime,
                eventType,
                participantIdentity,
                entityId,
                valuesJson,
                new TelemetryProvenance(
                    TelemetrySourceKind.CaptureLog,
                    sourceVersion,
                    SourceArtifactId: null,
                    Evidence: null,
                    detail));
            return OperationResult.Success(telemetryEvent);
        }
        catch (JsonException)
        {
            return Malformed(lineNumber, "Telemetry line contains malformed JSON.");
        }
        catch (FormatException)
        {
            return Malformed(lineNumber, "Telemetry line contains an invalid value.");
        }
        catch (OverflowException)
        {
            return Malformed(lineNumber, "Telemetry line contains an out-of-range value.");
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        return element.TryGetProperty(name, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String &&
               (value = property.GetString()) is not null;
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }

    private static OperationResult<TelemetryEvent> Malformed(long lineNumber, string reason) =>
        OperationResult.Failure<TelemetryEvent>(
            new ApplicationError("capture.line.malformed", $"Telemetry line {lineNumber}: {reason}"));

    private static OperationResult<TelemetryEvent> Failure(string code, string message) =>
        OperationResult.Failure<TelemetryEvent>(new ApplicationError(code, message));
}
