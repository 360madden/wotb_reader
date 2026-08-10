using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

/// <summary>
/// Offline proofs for the record-diffing discovery core: time bucketing of
/// trusted-reader dumps and HP correlation against damage ground truth. All
/// synthetic — no process access. The fixture is a 0x100-byte entity record
/// with an int32 "HP" at +0x48 (and optionally a changing counter at +0x20).
/// </summary>
[TestClass]
public sealed class RecordDiffingTests
{
    private const long TargetEntity = 7001;
    private const int HpOffset = 0x48;
    private const int CounterOffset = 0x20;

    private static byte[] Region(int hp, int counter = 0)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HpOffset), hp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(CounterOffset), counter);
        return bytes;
    }

    private static HpDamageEvent Damage(TimeSpan replayTime, int amount) =>
        new(
            ParticipantId: null,
            EntityId: TargetEntity,
            ReplayTime: replayTime,
            Kind: CanonicalEventKind.Damage,
            Damage: amount,
            AttackerEntityId: null,
            ValuesJson: "{}");

    [TestMethod]
    public void Bucket_ReturnsNoWindows_WithFewerThanTwoSnapshots()
    {
        Assert.IsEmpty(RecordChangeBucketer.Bucket([]));
        Assert.IsEmpty(
            RecordChangeBucketer.Bucket([new RecordSnapshot(TimeSpan.Zero, Region(500))]));
    }

    [TestMethod]
    public void Bucket_SkipsUnchangedPairs()
    {
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(275)),
        };

        IReadOnlyList<ByteChangeWindow> windows =
            RecordChangeBucketer.Bucket(snapshots);

        Assert.HasCount(1, windows);
        // The unchanged (0, 1000] pair produces no window; the window is the
        // pair that actually changed: (1000, 2000].
        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), windows[0].FromReplayTime);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2000), windows[0].ToReplayTime);
    }

    [TestMethod]
    public void Bucket_RejectsNonIncreasingReplayTime()
    {
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(350)),
        };

        Assert.ThrowsExactly<ArgumentException>(
            () => RecordChangeBucketer.Bucket(snapshots));
    }

    [TestMethod]
    public void Correlate_RanksHpFieldFirst_WhenDropsMatchDamage()
    {
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 275)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 25)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
            Damage(TimeSpan.FromMilliseconds(3000), 250),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(3, candidates[0].MatchedDamageWindows);
        Assert.AreEqual(3, candidates[0].TotalDamageWindows);
    }

    [TestMethod]
    public void Correlate_IgnoresUnrelatedChangingField()
    {
        // A counter at +0x20 changes every snapshot but never drops by the
        // damage amount — it must not rank above (or tie with) the HP field.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500, counter: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350, counter: 1)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 275, counter: 2)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 25, counter: 3)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
            Damage(TimeSpan.FromMilliseconds(3000), 250),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.IsFalse(
            candidates.Any(candidate => candidate.Offset == CounterOffset),
            "the unrelated counter must not be a candidate");
    }

    [TestMethod]
    public void Correlate_SumsMultipleDamageEventsInOneWindow()
    {
        // Sparse snapshots: only T=0 and T=3000. The events at 1000 (150) and
        // 2000 (75) both land in (0, 3000] — the cumulative drop must match
        // their sum.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 275)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1, candidates[0].MatchedDamageWindows);
    }

    [TestMethod]
    public void Correlate_NoCandidates_WhenDamageWindowHasNoHpDrop()
    {
        // A damage event lands in a window where only the unrelated counter
        // changed — no field drops by the damage amount → no candidates.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500, counter: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 500, counter: 1)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_StrictMatch_DoesNotMatchOverkill()
    {
        // Documented v1 limitation: HP 500 -> 0 with only 150 damage (overkill)
        // is NOT a strict match (delta -500 != -150), so HP is not a candidate.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 0)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_IgnoresOtherEntitiesDamage()
    {
        // Damage to a DIFFERENT entity must not be summed into the target's
        // windows — the target's own drop still matches its own events only.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            new HpDamageEvent(
                ParticipantId: null,
                EntityId: 9999,
                ReplayTime: TimeSpan.FromMilliseconds(1000),
                Kind: CanonicalEventKind.Damage,
                Damage: 999,
                AttackerEntityId: null,
                ValuesJson: "{}"),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }
}
