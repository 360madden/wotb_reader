using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using WotBTreader.Application.Streaming;

namespace WotBTreader.Host.Web.Hubs;

internal sealed class TelemetryHub(ITelemetryEventPublisher publisher) : Hub
{
    [HubMethodName("subscribe")]
    public async IAsyncEnumerable<TelemetryStreamEnvelope> SubscribeAsync(
        string? sessionId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (afterSequence < 0)
        {
            throw new HubException("stream.invalid_sequence");
        }

        if (sessionId is not null && !Guid.TryParse(sessionId, out _))
        {
            throw new HubException("stream.invalid_session_id");
        }

        await foreach (var message in publisher
            .SubscribeAsync(afterSequence, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            var messageSessionId = message.BattleSessionId?.Value.ToString("D");
            if (sessionId is not null &&
                messageSessionId is not null &&
                !string.Equals(
                    sessionId,
                    messageSessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new TelemetryStreamEnvelope(
                SchemaVersion: "1.0",
                Sequence: message.Sequence,
                Kind: message.Kind.ToString().ToLowerInvariant(),
                SessionId: messageSessionId,
                Event: message.Event,
                Snapshot: message.Snapshot,
                PublishedAtUtc: message.PublishedAtUtc);
        }
    }
}

internal sealed record TelemetryStreamEnvelope(
    string SchemaVersion,
    long Sequence,
    string Kind,
    string? SessionId,
    object? Event,
    object? Snapshot,
    DateTimeOffset PublishedAtUtc);
