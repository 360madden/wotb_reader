using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Logs;

internal enum LifecycleFeedEventKind
{
    Marker,
    SourceReset,
    Gap,
    Fault,
    ReconciliationCompleted,
}

internal enum LifecycleMarkerProvenance
{
    Historical,
    Live,
}

internal enum LifecycleFeedHealth
{
    Uninitialized,
    Healthy,
    Degraded,
}

internal enum LifecycleFeedReason
{
    Marker,
    InitialReconciliationCompleted,
    ReconciliationCompleted,
    SourceTruncated,
    SourceDeleted,
    SourceReappeared,
    SourceReplaced,
    SourceRewritten,
    WatcherOverflow,
    EnumerationFailed,
    EnumerationIncomplete,
    ReadFailed,
    ProducerFault,
}

internal sealed record LifecycleSourceCursor(
    ContentHash SourceId,
    long Generation,
    long LastByteOffset);

/// <summary>Unpublished evidence prepared during one reconciliation pass.</summary>
internal sealed record LifecycleFeedDraft(
    LifecycleFeedEventKind Kind,
    ContentHash SourceId,
    long Generation,
    long ByteOffset,
    ReplayLogMarkerKind? MarkerKind,
    DateTimeOffset? SourceTimestampUtc,
    LifecycleMarkerProvenance? Provenance,
    LifecycleFeedReason Reason);

internal sealed record LifecycleFeedBaseline(
    long Sequence,
    long HealthEpoch,
    LifecycleFeedHealth Health,
    IReadOnlyList<LifecycleSourceCursor> Sources)
{
    /// <summary>
    /// UTC instant at which the reconciled cursor snapshot was captured.
    /// New lifecycle sources must be created after this anchor before their
    /// initial bytes can be considered live launch evidence.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.MinValue;
}

internal sealed record LifecycleFeedEvent(
    long Sequence,
    long HealthEpoch,
    LifecycleFeedEventKind Kind,
    DateTimeOffset ObservedAtUtc,
    LifecycleSourceCursor? Cursor,
    ReplayLogMarkerKind? MarkerKind,
    DateTimeOffset? SourceTimestampUtc,
    LifecycleMarkerProvenance? Provenance,
    LifecycleFeedReason Reason);

internal sealed record LifecycleFeedReadResult(
    long RequestedAfterSequence,
    long LatestSequence,
    bool HistoryGap,
    IReadOnlyList<LifecycleFeedEvent> Events)
{
    /// <summary>
    /// Health captured with the read. A degraded feed cannot continue to
    /// authorize memory discovery even when it has no new events.
    /// </summary>
    public LifecycleFeedHealth Health { get; init; } = LifecycleFeedHealth.Healthy;
}

internal interface IBlitzReplayLifecycleFeed
{
    ValueTask<LifecycleFeedBaseline> CaptureBaselineAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles all configured sources through their current ends before
    /// capturing a baseline. This is the preparation barrier for a future
    /// managed launch; it does not authorize a session.
    /// </summary>
    ValueTask<LifecycleFeedBaseline> CaptureReconciledBaselineAsync(
        CancellationToken cancellationToken);

    ValueTask<LifecycleFeedReadResult> ReadAfterAsync(
        long afterSequence,
        CancellationToken cancellationToken);
}
