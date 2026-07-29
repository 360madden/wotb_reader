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
    IReadOnlyList<LifecycleSourceCursor> Sources);

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
    IReadOnlyList<LifecycleFeedEvent> Events);

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
