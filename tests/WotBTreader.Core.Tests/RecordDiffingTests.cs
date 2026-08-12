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
    private const int DrainOffset = 0x30;

    private static byte[] Region(int hp, int counter = 0, int drain = 0)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HpOffset), hp);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(CounterOffset), counter);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DrainOffset), drain);
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

    /// <summary>An event the TARGET dealt to some victim (attacker-side — the
    /// increment/damage-dealt direction keys on this id).</summary>
    private static HpDamageEvent DealtDamage(TimeSpan replayTime, int amount) =>
        new(
            ParticipantId: null,
            EntityId: 9999,
            ReplayTime: replayTime,
            Kind: CanonicalEventKind.Damage,
            Damage: amount,
            AttackerEntityId: TargetEntity,
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

    [TestMethod]
    public void Correlate_Lenient_ModeMatchesOverkillKillingBlow()
    {
        // The destroying hit overkills: HP 500 -> 0 with only 150 recorded
        // damage. Strict misses (existing test); Lenient accepts any drop >=
        // the window's damage, so HP ranks first.
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
                TargetEntity,
                DamageMatchMode.Lenient);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_Lenient_ModeRejectsSmallCoincidentalDrop()
    {
        // A drop SMALLER than the window's damage is not a match even in
        // Lenient mode (-100 > -150).
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 400)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_Lenient_DrainingDecoy_RanksBelowHp_OnFlatness()
    {
        // A monotonic drain (e.g. ammo/fuel/energy) drops MORE than every
        // window's damage, so under Lenient it matches every damage window
        // (score 1.0, precision 1.0 — precision counts damage windows only).
        // Without a discriminator it would TIE with HP and win on offset. The
        // flatness rank breaks the tie: HP is unchanged in the control (no-
        // damage) window, the drain keeps dropping through it.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500, drain: 1000)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350, drain: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 275, drain: -900)),
            // Control window (2000, 3000]: no damage; only the drain changes.
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 275, drain: -1900)),
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
                TargetEntity,
                DamageMatchMode.Lenient);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        // The drain IS a Lenient candidate (drop >= sum in both damage
        // windows) but its flatness is 0 — it ranks below HP.
        DamageCorrelationCandidate drain = candidates.Single(
            candidate => candidate.Offset == DrainOffset);
        Assert.AreEqual(1.0, drain.Score, 1e-9);
        Assert.AreEqual(0.0, drain.Flatness, 1e-9);
        Assert.AreNotEqual(DrainOffset, candidates[0].Offset);
    }

    [TestMethod]
    public void Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp()
    {
        // The residual Lenient risk: a decoy that drops by LARGE amounts (e.g.
        // another victim's HP, or a heavy drain) in the damage windows is flat
        // in control windows too, so flatness does NOT separate it — score and
        // flatness both 1.0, offset decides. The load-bearing confirmation is
        // STRICT: HP's drops EQUAL the exact sums (non-overkill windows); the
        // decoy's drops never do. The verdict contract requires >= 2 strict
        // matches before a HIT.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500, drain: 5000, counter: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350, drain: 4001, counter: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 275, drain: 3002, counter: 0)),
            // Control window (2000, 3000]: only the counter changes; the drain
            // is FLAT here (unlike the previous test's drain) — so flatness
            // cannot separate it from HP; both are 1.0.
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 275, drain: 3002, counter: 1)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
        };

        // Lenient: the decoy ties HP (score 1.0, flatness 1.0) and wins on
        // offset — the risk this contract documents.
        IReadOnlyList<DamageCorrelationCandidate> lenient =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient);
        Assert.AreEqual(DrainOffset, lenient[0].Offset);
        Assert.AreEqual(1.0, lenient[0].Flatness, 1e-9);

        // Strict: the decoy never drops by an exact sum -> excluded; HP drops
        // exactly -> confirmed with 2 strict matches.
        IReadOnlyList<DamageCorrelationCandidate> strict =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict);
        Assert.IsFalse(strict.Any(candidate => candidate.Offset == DrainOffset));
        Assert.IsNotEmpty(strict);
        Assert.AreEqual(HpOffset, strict[0].Offset);
        Assert.AreEqual(2, strict[0].MatchedDamageWindows);
    }

    [TestMethod]
    public void Correlate_Lenient_ModeStillMatchesExactDrops()
    {
        // Lenient subsumes strict: exact drops still match, across windows.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 275)),
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
                TargetEntity,
                DamageMatchMode.Lenient);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(2, candidates[0].MatchedDamageWindows);
    }

    [TestMethod]
    public void Correlate_RealisticEventMix_FindsHp()
    {
        // A realistic timeline: two Damage events with amounts, a Destroyed
        // event (no damage — must not break the correlation), and damage to an
        // unrelated entity — the HP field still ranks first across the two
        // windows that actually carry target damage.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 350)),
            // (1000, 2000] has only the Destroyed event — HP unchanged, no window.
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 350)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 275)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            new HpDamageEvent(
                ParticipantId: null,
                EntityId: TargetEntity,
                ReplayTime: TimeSpan.FromMilliseconds(2000),
                Kind: CanonicalEventKind.Destroyed,
                Damage: null,
                AttackerEntityId: 123,
                ValuesJson: "{}"),
            Damage(TimeSpan.FromMilliseconds(3000), 75),
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
        Assert.AreEqual(2, candidates[0].MatchedDamageWindows);
        Assert.AreEqual(2, candidates[0].TotalDamageWindows);
    }

    // ---- Increment direction (damage-dealt / scoreboard counter) ---------

    [TestMethod]
    public void Correlate_Increment_RanksDamageDealtFieldFirst_WhenRisesMatchDamage()
    {
        // The mirror of HP: the target's scoreboard damage-dealt counter at
        // +0x48 RISES by the exact damage of each event the target dealt.
        // Direction Increment keys the events on AttackerEntityId.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 150)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 225)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 475)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
            DealtDamage(TimeSpan.FromMilliseconds(2000), 75),
            DealtDamage(TimeSpan.FromMilliseconds(3000), 250),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Increment);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.AreEqual(3, candidates[0].MatchedDamageWindows);
    }

    [TestMethod]
    public void Correlate_Increment_DefaultDirection_StillMatchesDropsOnly()
    {
        // The default direction stays Decrement: an increment-only field must
        // NOT become a candidate just because the direction enum exists —
        // existing callers are unchanged.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 150)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
        };

        // Decrement direction: the event keys on EntityId (9999, not the
        // target) AND the field rises — no candidates either way.
        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_Increment_IgnoresVictimSideEvents()
    {
        // In Increment mode only the target's ATTACKER-side events are summed:
        // an event where the target is the victim must not move the counter.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 100)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 100)),
        };
        var events = new[]
        {
            // Target is the victim; attacker is someone else.
            new HpDamageEvent(
                ParticipantId: null,
                EntityId: TargetEntity,
                ReplayTime: TimeSpan.FromMilliseconds(1000),
                Kind: CanonicalEventKind.Damage,
                Damage: 150,
                AttackerEntityId: 1234,
                ValuesJson: "{}"),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Increment);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_Increment_StrictExcludesMagnitudeMismatchedRiser()
    {
        // The Increment analog of the HP Strict confirmation: a decoy at
        // +0x30 that rises by LARGE amounts in the damage windows (and is flat
        // in controls) ties Lenient on score and flatness, but Strict requires
        // delta == exact sum — the decoy never satisfies it; the real counter
        // at +0x48 does (2 strict matches).
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0, drain: 1000)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 150, drain: 2000)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 225, drain: 3000)),
            // Control window (2000, 3000]: only the counter changes; the
            // decoy is FLAT — flatness cannot separate it.
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 225, drain: 3000, counter: 1)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
            DealtDamage(TimeSpan.FromMilliseconds(2000), 75),
        };

        IReadOnlyList<DamageCorrelationCandidate> lenient =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Increment);
        Assert.AreEqual(DrainOffset, lenient[0].Offset);
        Assert.AreEqual(1.0, lenient[0].Flatness, 1e-9);

        IReadOnlyList<DamageCorrelationCandidate> strict =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Increment);
        Assert.IsFalse(strict.Any(candidate => candidate.Offset == DrainOffset));
        Assert.IsNotEmpty(strict);
        Assert.AreEqual(HpOffset, strict[0].Offset);
        Assert.AreEqual(2, strict[0].MatchedDamageWindows);
    }

    [TestMethod]
    public void Correlate_Increment_FlatnessDemotesMonotonicRiser()
    {
        // A monotonic riser (e.g. a tick/ammo counter) rises MORE than every
        // damage window's sum: under Lenient it matches every damage window
        // (score 1.0) but keeps rising through the no-damage control window —
        // flatness 0 demotes it below the real counter, which is flat there.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0, drain: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 150, drain: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 225, drain: 1000)),
            // Control window (2000, 3000]: no damage; the riser keeps climbing.
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Region(hp: 225, drain: 1500)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
            DealtDamage(TimeSpan.FromMilliseconds(2000), 75),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Increment);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        DamageCorrelationCandidate riser = candidates.Single(
            candidate => candidate.Offset == DrainOffset);
        Assert.AreEqual(1.0, riser.Score, 1e-9);
        Assert.AreEqual(0.0, riser.Flatness, 1e-9);
        Assert.AreNotEqual(DrainOffset, candidates[0].Offset);
    }

    [TestMethod]
    public void Correlate_Increment_LenientMatchesOvercapRise()
    {
        // Lenient also admits a rise LARGER than the window's sum (the counter
        // absorbed a sub-event the timeline missed) — the mirror of HP's
        // overkill admission; Strict still requires the exact sum.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 400)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> lenient =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Increment);
        Assert.IsNotEmpty(lenient);
        Assert.AreEqual(HpOffset, lenient[0].Offset);

        IReadOnlyList<DamageCorrelationCandidate> strict =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Increment);
        Assert.IsEmpty(strict);
    }

    // ---- int16 candidate mode (static playerHP evidence, 2026-08-11) ----
    // VerifyPlayerHpChain pins the 11.19.0.10 current-health field as a
    // SIGNED int16 at [entity+0xB8] on the entity base record (alive byte
    // at +0xBA, healing int16 at +0x11E). The correlator's int32-only
    // default folds that field into garbage (health + alive byte + padding)
    // or misses it entirely, so the int16 pass is the HP path.

    private const int Int16HpOffset = 0xB8;
    private const int Int16AliveOffset = 0xBA;

    private static byte[] Int16Region(short hp, byte alive = 1)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(Int16HpOffset), hp);
        bytes[Int16AliveOffset] = alive;
        return bytes;
    }

    [TestMethod]
    public void Correlate_Int16ModeOff_DoesNotEmitInt16Candidates()
    {
        // The int32-only default must never report an int16-sized candidate.
        // The destroy-window fixture (HP drops to 0 and the alive byte at
        // +0xBA flips 0) is the discriminator: the coincidental int32 read
        // at +0xB8 (health + alive<<16) matches only the non-destroy
        // windows under Strict, while the int16 field matches all — but with
        // the flag OFF the scan cannot even see the int16 field.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Int16Region(500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Int16Region(350)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Int16Region(275)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Int16Region(0, alive: 0)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
            Damage(TimeSpan.FromMilliseconds(3000), 275),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity);

        Assert.IsTrue(candidates.All(c => c.Length == sizeof(int)),
            "int32-only scan must never emit an int16 candidate");
    }

    [TestMethod]
    public void Correlate_Int16ModeOn_RanksInt16HpFieldFirst_UnderStrict()
    {
        // With the int16 pass enabled, the true int16 HP at +0xB8 matches
        // all three windows exactly (150/75/275) and ranks first with
        // length 2; the coincidental int32 read at the same offset (health +
        // alive<<16) drops 2^16 extra in the destroy window, so it cannot
        // confirm under Strict (the verdict contract's confirmation pass).
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Int16Region(500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Int16Region(350)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Int16Region(275)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(3000), Int16Region(0, alive: 0)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
            Damage(TimeSpan.FromMilliseconds(2000), 75),
            Damage(TimeSpan.FromMilliseconds(3000), 275),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(Int16HpOffset, candidates[0].Offset);
        Assert.AreEqual(sizeof(short), candidates[0].Length);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(3, candidates[0].MatchedDamageWindows);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
    }

    [TestMethod]
    public void Correlate_Int16Mode_StrictConfirmsExactDrops()
    {
        // The verdict contract's Strict confirmation also works on int16:
        // exact-sum drops rank, and a magnitude-mismatched int16 decoy
        // (drops 90 vs 150 damage in the same window) is excluded.
        byte[] RegionWithDecoy(short hp, short decoy)
        {
            byte[] bytes = Int16Region(hp);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(0x40), decoy);
            return bytes;
        }

        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, RegionWithDecoy(500, 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), RegionWithDecoy(350, 410)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(Int16HpOffset, candidates[0].Offset);
        Assert.IsFalse(candidates.Any(c => c.Offset == 0x40),
            "magnitude-mismatched int16 decoy must be excluded under Strict");
    }

    [TestMethod]
    public void Correlate_Int16Mode_IncrementCounterStillRanksFirst()
    {
        // Damage-dealt (Increment, int32 counter) with the int16 pass also
        // enabled: the int32 counter still ranks first — the int16 pass
        // only ADDS candidates, never demotes the true counter.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 0, counter: 100)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 0, counter: 250)),
        };
        var events = new[]
        {
            DealtDamage(TimeSpan.FromMilliseconds(1000), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Increment,
                includeInt16Candidates: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(CounterOffset, candidates[0].Offset);
        Assert.AreEqual(4, candidates[0].Length);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_LagToleranceZero_DoesNotMatchLaggedEvent()
    {
        // The memory write lands AFTER the decoded event (live evidence,
        // OD-RECOVERY-087: the health field drops ~1-10 s after the decoded
        // damage time). The window (1000, 2000] contains the drop but the
        // event at t=0 is OUTSIDE (From - 0, To] — with the default tolerance
        // 0 the drop is unexplained, exactly like the live session's mis-
        // attributed windows.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 350)),
        };
        var events = new[]
        {
            Damage(TimeSpan.Zero, 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient);

        // The HP drop 500 -> 350 in (1000, 2000] cannot be attributed to the
        // t=0 event — no window carries a matching event sum, so the drop is
        // unexplained and NO candidate is reported (the correlator omits
        // zero-match offsets). Exactly the live session's mis-attribution.
        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_LagTolerance_MatchesLaggedEvent()
    {
        // The same lagged fixture with a bounded tolerance: the event at t=0
        // now attributes to the (1000, 2000] change window that contains its
        // memory write ((From - 2s, To] contains 0), the 150 drop matches the
        // 150 damage, and the candidate scores 1.0 — the live-session fix.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 350)),
        };
        var events = new[]
        {
            Damage(TimeSpan.Zero, 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 2.0);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_LagTolerance_DoesNotMatchUnrelatedDrop()
    {
        // A tolerance must not fabricate matches: the window's drop (100) is
        // smaller than the only event's damage (150), so even with the
        // tolerance the strict Lenient test (drop >= sum) rejects it.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(1000), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromMilliseconds(2000), Region(hp: 400)),
        };
        var events = new[]
        {
            Damage(TimeSpan.Zero, 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 2.0);

        // The drop is smaller than the only event's damage: no candidate is
        // reported (the Lenient gate rejects it before ranking).
        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_LagTolerance_MatchesMultiEventClusterSum()
    {
        // The dense-span live case (OD-RECOVERY-087): two events ~1 s apart
        // (41 + 157 damage) apply in ONE change window whose drop equals
        // their combined sum (198). The subset match consumes both; a later
        // window with an unexplained drop has no affordable subset left, so
        // the denominator stays at the one real window and the true field
        // scores 1.0.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(100), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(200), Region(hp: 302)),
            new RecordSnapshot(TimeSpan.FromSeconds(300), Region(hp: 187)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromSeconds(155), 41),
            Damage(TimeSpan.FromSeconds(156), 157),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 50.0);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.IsNotNull(candidates[0].MatchedWindows);
        Assert.HasCount(1, candidates[0].MatchedWindows!);
        Assert.AreEqual(198, candidates[0].MatchedWindows![0].DamageSum);
    }

    [TestMethod]
    public void Correlate_LagLeadToleranceDefaultZero_DoesNotMatchLeadEvent()
    {
        // Dead Rail memory LEADS the decoded clock by ~2.5 s (OD-RECOVERY-089
        // measured it for yaw): the health write lands BEFORE the decoded
        // damage time, so the event's decoded time postdates the window that
        // contains its write. The one-directional attribution window
        // (From - lag, To] cannot see it — the drop in (1s, 2s] is
        // unexplained and the Strict pass reports no candidate. Exactly the
        // Phase-4 at-session honest negative (OD-RECOVERY-091).
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(hp: 350)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromSeconds(3), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 1.0,
                eventLagLeadSeconds: 0.0);

        // The default lead 0 keeps the one-directional window: the t=3s event
        // is outside (From - 1, To] = (0, 2], so the 150 drop is unexplained
        // and no candidate is reported — additive, unchanged behavior.
        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_LagLeadTolerance_MatchesLeadEventExactlyInStrictMode()
    {
        // The same memory-lead fixture with a bounded LEAD window: the event
        // at t=3s now attributes to the (1s, 2s] change window that contains
        // its write ((From - 1, To + 2] contains 3), the 150 drop equals the
        // 150 damage EXACTLY, and the Strict pass confirms the field with
        // score 1.0 — the fix that re-verdicts the OD-091 dumps to HIT.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(hp: 350)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromSeconds(3), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Strict,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 1.0,
                eventLagLeadSeconds: 2.0);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(HpOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.IsNotNull(candidates[0].MatchedWindows);
        Assert.HasCount(1, candidates[0].MatchedWindows!);
        Assert.AreEqual(150, candidates[0].MatchedWindows![0].DamageSum);
    }

    [TestMethod]
    public void Correlate_LagLeadTolerance_DoesNotFabricateMatchForLargerEvent()
    {
        // A lead must not fabricate matches: the window's drop (100) is
        // smaller than the only candidate event's damage (150), so even with
        // the bounded lead the Lenient gate (drop >= sum) rejects it — the
        // boundary is respected, not papered over.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(hp: 500)),
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(hp: 400)),
        };
        var events = new[]
        {
            Damage(TimeSpan.FromSeconds(3), 150),
        };

        IReadOnlyList<DamageCorrelationCandidate> candidates =
            HpDamageCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                events,
                TargetEntity,
                DamageMatchMode.Lenient,
                DamageCorrelationDirection.Decrement,
                includeInt16Candidates: false,
                eventLagToleranceSeconds: 1.0,
                eventLagLeadSeconds: 2.0);

        // The drop is smaller than the only event's damage: no candidate is
        // reported (the Lenient gate rejects it before ranking).
        Assert.IsEmpty(candidates);
    }
}
