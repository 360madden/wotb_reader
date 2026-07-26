using System.Buffers;
using System.Text.Json;
using WotBTreader.Application.Capture;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Ndjson;

public sealed class NdjsonTelemetryWriter : ITelemetryCaptureWriter
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    public async ValueTask WriteAsync(
        Stream destination,
        IAsyncEnumerable<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(events);

        await foreach (TelemetryEvent telemetryEvent in events
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("schemaVersion", "1");
                writer.WriteNumber("sourceSequence", telemetryEvent.SourceSequence);
                if (telemetryEvent.SourceTimeUtc is { } sourceTime)
                {
                    writer.WriteString("sourceTimeUtc", sourceTime.ToUniversalTime());
                }
                else
                {
                    writer.WriteNull("sourceTimeUtc");
                }

                if (telemetryEvent.ReplayTime is { } replayTime)
                {
                    writer.WriteNumber("replayTimeMs", replayTime.TotalMilliseconds);
                }
                else
                {
                    writer.WriteNull("replayTimeMs");
                }

                writer.WriteString("eventType", telemetryEvent.EventType);
                WriteOptionalString(writer, "participantIdentity", telemetryEvent.ParticipantIdentity);
                if (telemetryEvent.EntityId is { } entityId)
                {
                    writer.WriteNumber("entityId", entityId);
                }
                else
                {
                    writer.WriteNull("entityId");
                }

                writer.WritePropertyName("values");
                using (JsonDocument values = JsonDocument.Parse(telemetryEvent.ValuesJson))
                {
                    values.RootElement.WriteTo(writer);
                }

                writer.WriteStartObject("provenance");
                writer.WriteString("sourceVersion", telemetryEvent.Provenance.SourceVersion);
                WriteOptionalString(writer, "detail", telemetryEvent.Provenance.Detail);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
