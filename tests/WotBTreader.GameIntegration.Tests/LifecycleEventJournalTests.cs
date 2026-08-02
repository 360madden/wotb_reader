using WotBTreader.Core;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class LifecycleEventJournalTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 29, 16, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void CaptureBaseline_NewJournalIsUninitialized()
    {
        LifecycleFeedBaseline baseline = NewJournal().CaptureBaseline();

        Assert.AreEqual(0L, baseline.Sequence);
        Assert.AreEqual(0L, baseline.HealthEpoch);
        Assert.AreEqual(LifecycleFeedHealth.Uninitialized, baseline.Health);
        Assert.HasCount(0, baseline.Sources);
    }

    [TestMethod]
    public void CommitReconciliationBatch_PublishesDraftsAndEofAtomically()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
            [new(source, 1, 20)],
            LifecycleFeedReason.InitialReconciliationCompleted);

        LifecycleFeedBaseline baseline = journal.CaptureBaseline();
        LifecycleFeedReadResult events = journal.ReadAfter(0);
        Assert.AreEqual(LifecycleFeedHealth.Healthy, baseline.Health);
        Assert.AreEqual(LifecycleFeedHealth.Healthy, events.Health);
        Assert.AreEqual(1L, baseline.HealthEpoch);
        Assert.AreEqual(20L, baseline.Sources.Single().LastByteOffset);
        Assert.HasCount(2, events.Events);
    }

    [TestMethod]
    public void CommitReconciliationBatch_InvalidSecondDraftDoesNotPartiallyPublish()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Assert.ThrowsExactly<InvalidOperationException>(() => Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Live), Marker(source, 1, 10, LifecycleMarkerProvenance.Live)],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted));

        LifecycleFeedBaseline baseline = journal.CaptureBaseline();
        Assert.AreEqual(0L, baseline.Sequence);
        Assert.AreEqual(LifecycleFeedHealth.Uninitialized, baseline.Health);
        Assert.HasCount(0, baseline.Sources);
    }

    [TestMethod]
    public void CommitReconciliationBatch_HealthyPassUpdatesEofWithoutEpoch()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(journal, [], [new(source, 1, 10)], LifecycleFeedReason.InitialReconciliationCompleted);
        Commit(journal, [], [new(source, 1, 20)], LifecycleFeedReason.ReconciliationCompleted);
        LifecycleFeedBaseline baseline = journal.CaptureBaseline();
        Assert.AreEqual(1L, baseline.HealthEpoch);
        Assert.AreEqual(20L, baseline.Sources.Single().LastByteOffset);
    }

    [TestMethod]
    public void CommitReconciliationBatch_OrdersSourcesDeterministically()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash sourceB = Hash('b');
        ContentHash sourceA = Hash('a');

        Commit(journal,
            [],
            [new(sourceB, 1, 20), new(sourceA, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);

        LifecycleFeedBaseline baseline = journal.CaptureBaseline();
        Assert.AreEqual(sourceA, baseline.Sources[0].SourceId);
        Assert.AreEqual(sourceB, baseline.Sources[1].SourceId);
    }

    [TestMethod]
    public void CommitReconciliationBatch_PreservesMarkerProvenance()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');

        Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);
        Commit(journal,
            [Marker(source, 1, 20, LifecycleMarkerProvenance.Live)],
            [new(source, 1, 20)],
            LifecycleFeedReason.ReconciliationCompleted);

        LifecycleFeedEvent[] markers =
            [.. journal.ReadAfter(0).Events.Where(static item =>
                item.Kind == LifecycleFeedEventKind.Marker)];
        Assert.AreEqual(LifecycleMarkerProvenance.Historical, markers[0].Provenance);
        Assert.AreEqual(LifecycleMarkerProvenance.Live, markers[1].Provenance);
    }

    [TestMethod]
    public void ReadAfter_EvictedHistoryReturnsExplicitGapWithoutPartialEvents()
    {
        var journal = new LifecycleEventJournal(2, new FixedTimeProvider(Now));
        ContentHash source = Hash('a');
        Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);
        Commit(journal,
            [Marker(source, 1, 20, LifecycleMarkerProvenance.Live)],
            [new(source, 1, 20)],
            LifecycleFeedReason.ReconciliationCompleted);

        LifecycleFeedReadResult result = journal.ReadAfter(0);
        Assert.IsTrue(result.HistoryGap);
        Assert.HasCount(0, result.Events);
        Assert.AreEqual(3L, result.LatestSequence);
    }

    [TestMethod]
    public void ReadAfter_ReturnsOnlyStrictlyNewerEvents()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);

        LifecycleFeedReadResult result = journal.ReadAfter(1);
        Assert.IsFalse(result.HistoryGap);
        Assert.HasCount(1, result.Events);
        Assert.AreEqual(LifecycleFeedEventKind.ReconciliationCompleted, result.Events[0].Kind);
    }

    [TestMethod]
    public void ReadAfter_RejectsFutureSequence()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => NewJournal().ReadAfter(1));
    }

    [TestMethod]
    public void RecordGap_DegradesUntilSuccessfulReconciliation()
    {
        LifecycleEventJournal journal = NewJournal();
        Commit(journal,
            [],
            [],
            LifecycleFeedReason.InitialReconciliationCompleted);
        journal.RecordGap(LifecycleFeedReason.WatcherOverflow);

        LifecycleFeedBaseline degraded = journal.CaptureBaseline();
        LifecycleFeedReadResult degradedRead = journal.ReadAfter(degraded.Sequence);
        Assert.AreEqual(LifecycleFeedHealth.Degraded, degraded.Health);
        Assert.AreEqual(LifecycleFeedHealth.Degraded, degradedRead.Health);
        Commit(journal,
            [],
            [],
            LifecycleFeedReason.ReconciliationCompleted);
        LifecycleFeedBaseline recovered = journal.CaptureBaseline();
        Assert.AreEqual(LifecycleFeedHealth.Healthy, recovered.Health);
        Assert.IsGreaterThan(degraded.HealthEpoch, recovered.HealthEpoch);
    }

    [TestMethod]
    public void CommitReconciliationBatch_DuplicateAndGenerationJumpAreRejected()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(journal,
            [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Commit(journal,
                [Marker(source, 1, 10, LifecycleMarkerProvenance.Live)],
                [new(source, 1, 10)],
                LifecycleFeedReason.ReconciliationCompleted));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Commit(journal,
                [Marker(source, 2, 20, LifecycleMarkerProvenance.Live)],
                [new(source, 2, 20)],
                LifecycleFeedReason.ReconciliationCompleted));
    }

    [TestMethod]
    public void CommitReconciliationBatch_PreservesGenerationTombstones()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(journal,
            [],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);
        Commit(journal,
            [Reset(source, 2, 10, LifecycleFeedReason.SourceDeleted)],
            [],
            LifecycleFeedReason.ReconciliationCompleted);

        Assert.HasCount(0, journal.CaptureBaseline().Sources);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Commit(journal,
                [Marker(source, 1, 20, LifecycleMarkerProvenance.Live)],
                [new(source, 1, 20)],
                LifecycleFeedReason.ReconciliationCompleted));

        Commit(journal,
            [Reset(source, 3, 0, LifecycleFeedReason.SourceReappeared)],
            [new(source, 3, 0)],
            LifecycleFeedReason.ReconciliationCompleted);
        Assert.AreEqual(3L, journal.CaptureBaseline().Sources.Single().Generation);
    }

    [TestMethod]
    public void CommitReconciliationBatch_RejectsArbitraryReasonForMarker()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        LifecycleFeedDraft invalid = Marker(
            source,
            1,
            10,
            LifecycleMarkerProvenance.Live) with
        {
            Reason = LifecycleFeedReason.ReadFailed,
        };

        Assert.ThrowsExactly<ArgumentException>(() =>
            Commit(journal,
                [invalid],
                [new(source, 1, 10)],
                LifecycleFeedReason.InitialReconciliationCompleted));
    }

    [TestMethod]
    public void TryCommitReconciliationBatch_RejectsStaleJournalRevision()
    {
        LifecycleEventJournal journal = NewJournal();
        ContentHash source = Hash('a');
        Commit(
            journal,
            [],
            [new(source, 1, 10)],
            LifecycleFeedReason.InitialReconciliationCompleted);
        long staleSequence = journal.CaptureBaseline().Sequence;
        journal.RecordGap(LifecycleFeedReason.WatcherOverflow);

        bool committed = journal.TryCommitReconciliationBatch(
            [Marker(source, 1, 20, LifecycleMarkerProvenance.Live)],
            [new(source, 1, 20)],
            LifecycleFeedReason.ReconciliationCompleted,
            staleSequence);

        Assert.IsFalse(committed);
        Assert.AreEqual(
            LifecycleFeedHealth.Degraded,
            journal.CaptureBaseline().Health);
        Assert.IsFalse(journal.ReadAfter(0).Events.Any(static item =>
            item.Kind == LifecycleFeedEventKind.Marker));
    }

    [TestMethod]
    public void TryCommitReconciliationBatch_ThrowingClockLeavesStateUnchanged()
    {
        var journal = new LifecycleEventJournal(
            16,
            new ThrowingTimeProvider());
        ContentHash source = Hash('a');

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            journal.TryCommitReconciliationBatch(
                [Marker(source, 1, 10, LifecycleMarkerProvenance.Historical)],
                [new(source, 1, 10)],
                LifecycleFeedReason.InitialReconciliationCompleted,
                expectedSequence: 0));

        LifecycleFeedBaseline baseline = journal.CaptureBaseline();
        Assert.AreEqual(0L, baseline.Sequence);
        Assert.AreEqual(0L, baseline.HealthEpoch);
        Assert.AreEqual(LifecycleFeedHealth.Uninitialized, baseline.Health);
        Assert.HasCount(0, baseline.Sources);
        Assert.HasCount(0, journal.ReadAfter(0).Events);
    }

    private static LifecycleFeedDraft Marker(
        ContentHash source,
        long generation,
        long offset,
        LifecycleMarkerProvenance provenance) =>
        new(
            LifecycleFeedEventKind.Marker,
            source,
            generation,
            offset,
            ReplayLogMarkerKind.OfflineReplayStarted,
            null,
            provenance,
            LifecycleFeedReason.Marker);

    private static LifecycleFeedDraft Reset(
        ContentHash source,
        long generation,
        long offset,
        LifecycleFeedReason reason) =>
        new(
            LifecycleFeedEventKind.SourceReset,
            source,
            generation,
            offset,
            null,
            null,
            null,
            reason);

    private static LifecycleEventJournal NewJournal() =>
        new(16, new FixedTimeProvider(Now));

    private static void Commit(
        LifecycleEventJournal journal,
        IReadOnlyList<LifecycleFeedDraft> drafts,
        IReadOnlyList<LifecycleSourceCursor> activeSnapshot,
        LifecycleFeedReason completionReason)
    {
        bool committed = journal.TryCommitReconciliationBatch(
            drafts,
            activeSnapshot,
            completionReason,
            journal.CaptureBaseline().Sequence);
        Assert.IsTrue(committed);
    }

    private static ContentHash Hash(char value) => new(new string(value, 64));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("Synthetic clock failure.");
    }
}
