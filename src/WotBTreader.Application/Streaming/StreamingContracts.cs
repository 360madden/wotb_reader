using WotBTreader.Core;

namespace WotBTreader.Application.Streaming;

public enum TelemetryStreamMessageKind
{
    Snapshot,
    Event,
    Gap,
    Heartbeat,
}

public sealed record TelemetryStreamMessage(
    long Sequence,
    TelemetryStreamMessageKind Kind,
    BattleSessionId? BattleSessionId,
    CanonicalEvent? Event,
    ReplayDecodeProjection? Snapshot,
    DateTimeOffset PublishedAtUtc);

/// <summary>Publishes sequenced telemetry only after the owning transaction commits.</summary>
public interface ITelemetryEventPublisher
{
    ValueTask<long> PublishCommittedAsync(
        BattleSessionId battleSessionId,
        IReadOnlyList<CanonicalEvent> events,
        CancellationToken cancellationToken);

    IAsyncEnumerable<TelemetryStreamMessage> SubscribeAsync(
        long afterSequence,
        CancellationToken cancellationToken);
}
