using WotBTreader.Core;

namespace WotBTreader.Application.Streaming;

/// <summary>
/// Classifies a <see cref="TelemetryStreamMessage"/> to let subscribers
/// distinguish live event data from catch-up snapshots, gaps, and heartbeats.
/// </summary>
public enum TelemetryStreamMessageKind
{
    /// <summary>Full projection snapshot for catch-up subscribers.</summary>
    Snapshot,
    /// <summary>A single decoded telemetry event.</summary>
    Event,
    /// <summary>Indicates one or more sequence numbers were skipped (subscriber is behind).</summary>
    Gap,
    /// <summary>Stream keep-alive with no payload.</summary>
    Heartbeat,
}

/// <summary>
/// One message in the sequenced telemetry stream. Carries either an
/// <see cref="Event"/> (for Event/Gap kinds) or a <see cref="Snapshot"/>
/// (for Snapshot kind). Heartbeat messages carry neither.
/// </summary>
/// <param name="Sequence">Monotonically increasing sequence number.</param>
/// <param name="Kind">Message classification.</param>
/// <param name="BattleSessionId">Associated battle session, if applicable.</param>
/// <param name="Event">Canonical event payload, if applicable.</param>
/// <param name="Snapshot">Full projection snapshot, if applicable.</param>
/// <param name="PublishedAtUtc">UTC timestamp when the message was published.</param>
public sealed record TelemetryStreamMessage(
    long Sequence,
    TelemetryStreamMessageKind Kind,
    BattleSessionId? BattleSessionId,
    CanonicalEvent? Event,
    ReplayDecodeProjection? Snapshot,
    DateTimeOffset PublishedAtUtc);

/// <summary>
/// Publishes sequenced telemetry events only after the owning transaction
/// commits. Subscribers receive events in order; late subscribers get
/// a bounded history catch-up followed by live stream events.
/// </summary>
public interface ITelemetryEventPublisher
{
    /// <summary>
    /// Publishes committed events, assigning each a monotonically increasing
    /// sequence number. All-or-nothing: on failure, no events are published.
    /// </summary>
    /// <param name="battleSessionId">The battle session these events belong to.</param>
    /// <param name="events">Canonical events to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last sequence number assigned.</returns>
    ValueTask<long> PublishCommittedAsync(
        BattleSessionId battleSessionId,
        IReadOnlyList<CanonicalEvent> events,
        CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to the event stream, receiving all messages after
    /// <paramref name="afterSequence"/> (history replay then live).
    /// If the requested sequence has already been evicted from the
    /// internal buffer, a <see cref="TelemetryStreamMessageKind.Gap"/>
    /// message is emitted first.
    /// </summary>
    /// <param name="afterSequence">Consume events after this sequence (0 = all).</param>
    /// <param name="cancellationToken">Cancellation token for unsubscription.</param>
    IAsyncEnumerable<TelemetryStreamMessage> SubscribeAsync(
        long afterSequence,
        CancellationToken cancellationToken);
}
