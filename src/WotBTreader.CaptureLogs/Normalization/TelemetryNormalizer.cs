using System.Buffers;
using System.Text.Json;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Normalization;

public sealed class TelemetryNormalizer : ITelemetryNormalizer
{
    public OperationResult<TelemetryEvent> Normalize(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        try
        {
            string eventType = telemetryEvent.EventType.Trim().ToLowerInvariant();
            if (eventType.Length == 0)
            {
                return OperationResult.Failure<TelemetryEvent>(
                    new ApplicationError("telemetry.event_type.invalid", "Event type is empty."));
            }

            string canonicalValues = CanonicalizeObject(telemetryEvent.ValuesJson);
            return OperationResult.Success(telemetryEvent with
            {
                EventType = eventType,
                ValuesJson = canonicalValues,
            });
        }
        catch (JsonException)
        {
            return OperationResult.Failure<TelemetryEvent>(
                new ApplicationError("telemetry.values.malformed", "Telemetry values contain malformed JSON."));
        }
    }

    private static string CanonicalizeObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Telemetry values must be an object.");
        }

        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        WriteCanonical(document.RootElement, writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
