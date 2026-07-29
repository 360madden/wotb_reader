using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Logs;

/// <summary>Thread-safe bounded evidence journal; reconciliation publishes atomically.</summary>
internal sealed class LifecycleEventJournal(int capacity, TimeProvider timeProvider)
{
    private readonly Lock _gate = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly Queue<LifecycleFeedEvent> _events = new(capacity);
    private readonly Dictionary<ContentHash, LifecycleSourceCursor> _sources = [];
    private readonly HashSet<ContentHash> _active = [];
    private long _sequence;
    private long _epoch;
    private LifecycleFeedHealth _health = LifecycleFeedHealth.Uninitialized;

    public LifecycleFeedBaseline CaptureBaseline()
    {
        lock (_gate)
        {
            return new(_sequence, _epoch, _health, [.. _sources.Values.OrderBy(static x => x.SourceId.Value, StringComparer.Ordinal)]);
        }
    }

    public bool TryCommitReconciliationBatch(
        IReadOnlyList<LifecycleFeedDraft> drafts,
        IReadOnlyList<LifecycleSourceCursor> activeSnapshot,
        LifecycleFeedReason completionReason,
        long expectedSequence)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(activeSnapshot);
        if (completionReason is not LifecycleFeedReason.InitialReconciliationCompleted and not LifecycleFeedReason.ReconciliationCompleted)
        {
            throw new ArgumentOutOfRangeException(nameof(completionReason));
        }

        lock (_gate)
        {
            if (_sequence != expectedSequence)
            {
                return false;
            }

            Dictionary<ContentHash, LifecycleSourceCursor> sources = new(_sources);
            HashSet<ContentHash> active = new(_active);
            bool degraded = _health != LifecycleFeedHealth.Healthy;
            foreach (LifecycleFeedDraft draft in drafts)
            {
                ValidateDraft(draft, sources, active, ref degraded);
            }

            HashSet<ContentHash> snapshotIds = [];
            foreach (LifecycleSourceCursor cursor in activeSnapshot)
            {
                ArgumentNullException.ThrowIfNull(cursor);
                ArgumentNullException.ThrowIfNull(cursor.SourceId);
                ValidateCursor(cursor.Generation, cursor.LastByteOffset);
                if (!snapshotIds.Add(cursor.SourceId)) throw new ArgumentException("Duplicate source.", nameof(activeSnapshot));
                if (sources.ContainsKey(cursor.SourceId) && !active.Contains(cursor.SourceId)) throw new InvalidOperationException("Tombstone must re-enter through reset.");
                if (sources.TryGetValue(cursor.SourceId, out LifecycleSourceCursor? current)
                    && (cursor.Generation != current.Generation || cursor.LastByteOffset < current.LastByteOffset))
                {
                    throw new InvalidOperationException("Snapshot cursor regression.");
                }
                sources[cursor.SourceId] = cursor;
            }
            active.Clear();
            active.UnionWith(snapshotIds);

            bool needsEpoch = degraded;
            int eventCount = checked(drafts.Count + (needsEpoch ? 1 : 0));
            if (_sequence > long.MaxValue - eventCount)
            {
                throw new InvalidOperationException(
                    "The lifecycle journal sequence is exhausted.");
            }

            long nextSequence = _sequence;
            long nextEpoch = needsEpoch
                ? checked(_epoch + 1)
                : _epoch;
            List<LifecycleFeedEvent> prepared = new(eventCount);
            foreach (LifecycleFeedDraft draft in drafts)
            {
                prepared.Add(new LifecycleFeedEvent(
                    checked(++nextSequence),
                    _epoch,
                    draft.Kind,
                    _time.GetUtcNow(),
                    new LifecycleSourceCursor(
                        draft.SourceId,
                        draft.Generation,
                        draft.ByteOffset),
                    draft.MarkerKind,
                    draft.SourceTimestampUtc,
                    draft.Provenance,
                    draft.Reason));
            }

            if (needsEpoch)
            {
                prepared.Add(new LifecycleFeedEvent(
                    checked(++nextSequence),
                    nextEpoch,
                    LifecycleFeedEventKind.ReconciliationCompleted,
                    _time.GetUtcNow(),
                    Cursor: null,
                    MarkerKind: null,
                    SourceTimestampUtc: null,
                    Provenance: null,
                    completionReason));
            }

            _sources.Clear();
            foreach ((ContentHash id, LifecycleSourceCursor cursor) in sources)
            {
                _sources.Add(id, cursor);
            }

            _active.Clear();
            _active.UnionWith(active);
            _sequence = nextSequence;
            _epoch = nextEpoch;
            if (needsEpoch)
            {
                _health = LifecycleFeedHealth.Healthy;
            }

            foreach (LifecycleFeedEvent feedEvent in prepared)
            {
                Append(feedEvent);
            }

            return true;
        }
    }

    public LifecycleFeedEvent RecordGap(LifecycleFeedReason reason, LifecycleSourceCursor? cursor = null)
    {
        if (reason is not LifecycleFeedReason.WatcherOverflow and not LifecycleFeedReason.EnumerationFailed and not LifecycleFeedReason.EnumerationIncomplete and not LifecycleFeedReason.ReadFailed) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            DateTimeOffset observedAtUtc = _time.GetUtcNow();
            long sequence = checked(_sequence + 1);
            var feedEvent = new LifecycleFeedEvent(
                sequence,
                _epoch,
                LifecycleFeedEventKind.Gap,
                observedAtUtc,
                cursor,
                MarkerKind: null,
                SourceTimestampUtc: null,
                Provenance: null,
                reason);
            _sequence = sequence;
            _health = LifecycleFeedHealth.Degraded;
            return Append(feedEvent);
        }
    }

    public LifecycleFeedEvent RecordFault(LifecycleFeedReason reason)
    {
        if (reason != LifecycleFeedReason.ProducerFault) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            DateTimeOffset observedAtUtc = _time.GetUtcNow();
            long sequence = checked(_sequence + 1);
            var feedEvent = new LifecycleFeedEvent(
                sequence,
                _epoch,
                LifecycleFeedEventKind.Fault,
                observedAtUtc,
                Cursor: null,
                MarkerKind: null,
                SourceTimestampUtc: null,
                Provenance: null,
                reason);
            _sequence = sequence;
            _health = LifecycleFeedHealth.Degraded;
            return Append(feedEvent);
        }
    }
    public LifecycleFeedReadResult ReadAfter(long after)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(after, _sequence);
            long earliest = _events.Count == 0 ? _sequence + 1 : _events.Peek().Sequence;
            bool gap = after < earliest - 1;
            return new(after, _sequence, gap, gap ? [] : [.. _events.Where(x => x.Sequence > after)]);
        }
    }

    private static void ValidateDraft(LifecycleFeedDraft d, Dictionary<ContentHash, LifecycleSourceCursor> sources, HashSet<ContentHash> active, ref bool degraded)
    {
        ArgumentNullException.ThrowIfNull(d); ArgumentNullException.ThrowIfNull(d.SourceId); ValidateCursor(d.Generation, d.ByteOffset);
        if (d.Kind == LifecycleFeedEventKind.Marker)
        {
            if (d.MarkerKind is null || d.Provenance is null || d.Reason != LifecycleFeedReason.Marker) throw new ArgumentException("Invalid marker draft.");
            if (sources.TryGetValue(d.SourceId, out LifecycleSourceCursor? old))
            {
                if (!active.Contains(d.SourceId) || old.Generation != d.Generation || d.ByteOffset <= old.LastByteOffset) throw new InvalidOperationException("Marker cursor regression.");
            }
            else if (d.Generation != 1) throw new InvalidOperationException("New marker must begin at generation one.");
            sources[d.SourceId] = new(d.SourceId, d.Generation, d.ByteOffset); active.Add(d.SourceId); return;
        }
        if (d.Kind != LifecycleFeedEventKind.SourceReset || d.MarkerKind is not null || d.Provenance is not null || d.Reason is not (LifecycleFeedReason.SourceTruncated or LifecycleFeedReason.SourceDeleted or LifecycleFeedReason.SourceReappeared or LifecycleFeedReason.SourceReplaced or LifecycleFeedReason.SourceRewritten)) throw new ArgumentException("Invalid reset draft.");
        if (sources.TryGetValue(d.SourceId, out LifecycleSourceCursor? current))
        {
            if (d.Generation <= current.Generation) throw new InvalidOperationException("Reset generation must advance.");
        }
        else if (d.Generation != 1) throw new InvalidOperationException("New reset must begin at generation one.");
        sources[d.SourceId] = new(d.SourceId, d.Generation, d.ByteOffset);
        if (d.Reason == LifecycleFeedReason.SourceDeleted) active.Remove(d.SourceId); else active.Add(d.SourceId);
        degraded = true;
    }
    private LifecycleFeedEvent Append(LifecycleFeedEvent e) { while (_events.Count >= _capacity) _events.Dequeue(); _events.Enqueue(e); return e; }
    private static void ValidateCursor(long generation, long offset) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation); ArgumentOutOfRangeException.ThrowIfNegative(offset); }
}
