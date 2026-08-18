using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

/// <summary>
/// Offline proofs for the facing (yaw) correlation core: a wrap-aware
/// float32 field whose value delta matches the packet-derived yaw delta
/// (radians) per replay-time window. Synthetic fixtures only — no process
/// access. The fixture is a 0x100-byte entity record with a float32 "yaw" at
/// +0x2C (and optionally a drifting decoy at +0x20).
/// </summary>
[TestClass]
public sealed class HeadingCorrelatorTests
{
    private const long TargetEntity = 7001;
    private const int YawOffset = 0x2C;
    private const int DecoyOffset = 0x20;
    // OD-RECOVERY-088 live-corrected yaw offset on the ring record.
    private const int LiveYawOffset = 0x30;

    private static byte[] Region(float yaw, float decoy = 0f)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(YawOffset), yaw);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(DecoyOffset), decoy);
        return bytes;
    }

    private static YawSample Yaw(TimeSpan time, double radians) =>
        new(time, TargetEntity, radians);

    [TestMethod]
    public void Correlate_RanksYawFieldFirst_WhenDeltasMatchTurnWindows()
    {
        // Two turn windows (yaw moves 0.9 and 0.1 rad) and one stationary
        // control window (yaw unchanged, the decoy drifts) — the yaw field
        // matches both turns and stays flat in the control.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 0.1f, decoy: 0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 1.0f, decoy: 0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(yaw: 1.1f, decoy: 0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(3), Region(yaw: 1.1f, decoy: 1f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
            Yaw(TimeSpan.FromSeconds(2), 1.1),
            Yaw(TimeSpan.FromSeconds(3), 1.1),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.AreEqual(2, candidates[0].MatchedWindows);
        Assert.AreEqual(2, candidates[0].TotalWindows);
    }

    [TestMethod]
    public void Correlate_HandlesWrapAcrossPi()
    {
        // A turn from +3.0 rad to -3.0 rad crosses the +/-pi seam: the raw
        // delta is -6.0, the wrapped delta is -6.0 + 2pi ≈ 0.283. Both the
        // ground truth and the field use the same wrap, so they match.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 3.0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: -3.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 3.0),
            Yaw(TimeSpan.FromSeconds(1), -3.0),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_FlatnessDemotesDecoyThatDriftsInControlWindows()
    {
        // A decoy at +0x20 reproduces the SAME turn deltas (so it matches the
        // turns) but keeps changing in the stationary control window — the
        // yaw field stays put there, so flatness separates them.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 0.1f, decoy: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 1.0f, decoy: 1.0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(yaw: 1.1f, decoy: 1.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(3), Region(yaw: 1.1f, decoy: 2.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
            Yaw(TimeSpan.FromSeconds(2), 1.1),
            Yaw(TimeSpan.FromSeconds(3), 1.1),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        HeadingCorrelationCandidate decoy = candidates.Single(
            candidate => candidate.Offset == DecoyOffset);
        Assert.AreEqual(1.0, decoy.Score, 1e-9);
        Assert.AreEqual(0.0, decoy.Flatness, 1e-9);
    }

    [TestMethod]
    public void Correlate_RejectsMagnitudeMismatchedField()
    {
        // A field that moves but by the WRONG amount in the turn windows is
        // not a candidate at all (changed but never matched).
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 0.1f, decoy: 0.0f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 0.1f, decoy: 9.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_IgnoresOtherEntitiesYaw()
    {
        // Only the target entity's yaw samples are summed into the ground
        // truth; another entity's rotation must not move the expected deltas.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 1.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
            new YawSample(TimeSpan.FromSeconds(0), 9999, 5.0),
            new YawSample(TimeSpan.FromSeconds(1), 9999, 5.5),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_UsesNearestSampleForMidWindowSnapshotTimes()
    {
        // Snapshot times fall BETWEEN yaw samples: the expected delta uses the
        // NEAREST decoded packet (the sample the dump's replay clock lands
        // on, ties resolve to the earlier), not an interpolated value. A
        // 0.5s snapshot resolves to the 0s sample (0.1) and a 1.5s snapshot
        // to the 1s sample (1.0), so the field must carry those nearest
        // values to match — an interpolated field would NOT.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(0.5), Region(yaw: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1.5), Region(yaw: 1.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
            Yaw(TimeSpan.FromSeconds(2), 1.1),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
    }

    [TestMethod]
    public void Correlate_FailsClosedOutsideYawSampleSpan()
    {
        // A snapshot before the first yaw sample has no ground truth: the
        // window is excluded entirely (no fabrication against an endpoint
        // constant), so nothing can match.
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.FromSeconds(-1), Region(yaw: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 1.0f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 1.0),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void Correlate_DeadBandWindow_IsControlNotUnmatchableTurn()
    {
        // A window whose expected |delta| sits between the old 0.02 rad
        // control threshold and the 0.05 rad match tolerance (e.g. a residual
        // rotation between two adjacent turns) can NEVER be matched: its
        // observed delta reads as "unchanged" (<= tolerance) and is skipped,
        // but counting it in the score denominator would cap a perfect field
        // below 1.0. Such windows must be CONTROL windows (flatness), so the
        // score denominator only holds provable turns.
        var snapshots = new[]
        {
            // Turn 1: 0.4 rad (clearly matchable).
            new RecordSnapshot(TimeSpan.FromSeconds(0), Region(yaw: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 0.5f)),
            // Dead-band gap: 0.03 rad residual rotation between the turns.
            new RecordSnapshot(TimeSpan.FromSeconds(2), Region(yaw: 0.53f)),
            // Turn 2: 0.4 rad (clearly matchable).
            new RecordSnapshot(TimeSpan.FromSeconds(3), Region(yaw: 0.93f)),
        };
        var yaw = new[]
        {
            Yaw(TimeSpan.FromSeconds(0), 0.1),
            Yaw(TimeSpan.FromSeconds(1), 0.5),
            Yaw(TimeSpan.FromSeconds(2), 0.53),
            Yaw(TimeSpan.FromSeconds(3), 0.93),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yaw,
                TargetEntity);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(YawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.AreEqual(2, candidates[0].MatchedWindows);
        Assert.AreEqual(2, candidates[0].TotalWindows);
    }

    [TestMethod]
    public void Correlate_NoSamplesForEntity_ReturnsEmpty()
    {
        var snapshots = new[]
        {
            new RecordSnapshot(TimeSpan.Zero, Region(yaw: 0.1f)),
            new RecordSnapshot(TimeSpan.FromSeconds(1), Region(yaw: 1.0f)),
        };

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                [Yaw(TimeSpan.Zero, 0.1), Yaw(TimeSpan.FromSeconds(1), 1.0)],
                9999);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void CorrelateWithLag_FindsYawAtSharedApplyLag_WhenDeltaPathMisses()
    {
        // OD-RECOVERY-087/088 finding: the ring record applies decoded packet
        // state with a variable ~1-5 s memory-apply lag, so the field at dump
        // time t holds the packet yaw from t - lag. The window-delta path
        // misses this (before/after dumps bracket a turn, but the memory is
        // still showing the PRE-turn value); the value-match lag path must
        // find the field at the true offset (+0x30, the live-corrected yaw)
        // with the shared lag.
        //
        // Fixture: the packet yaw is 0 rad until t=10s then 1.2 rad; the
        // memory applies it 5 s late. Dumps at 6..16 s carry the memory value
        // from t-5, i.e. [0,0,0,0,0,1.2] vs the packet [0,0,0,1.2,1.2,1.2].
        // The yaw sample series spans 0..16 s so lag lookups resolve.
        const double lag = 5.0;
        double[] dumpTimes = [6.0, 8.0, 10.0, 12.0, 14.0, 16.0];
        var snapshots = dumpTimes
            .Select(t => new RecordSnapshot(
                TimeSpan.FromSeconds(t),
                LaggedRegion(PacketYawAt(t - lag))))
            .ToArray();
        // Every non-target 4-byte offset carries a constant 0.7 rad (never
        // equal to the packet yaw 0/1.2), so no zero-filled decoy can match
        // the stationary stretches at some lag.
        foreach (RecordSnapshot snapshot in snapshots)
        {
            for (int offset = 0; offset <= snapshot.Bytes.Length - 4; offset += 4)
            {
                if (offset == LiveYawOffset)
                {
                    continue;
                }

                BinaryPrimitives.WriteSingleLittleEndian(
                    snapshot.Bytes.AsSpan(offset), 0.7f);
            }
        }
        var yawSamples = Enumerable.Range(0, 9)
            .Select(i => Yaw(TimeSpan.FromSeconds(2.0 * i), PacketYawAt(2.0 * i)))
            .ToArray();

        // The window-delta path must NOT hit (the lag breaks before/after
        // deltas at the turn boundary): no candidate reaches score 1.0.
        IReadOnlyList<HeadingCorrelationCandidate> deltaCandidates =
            HeadingCorrelator.Correlate(
                RecordChangeBucketer.Bucket(snapshots),
                yawSamples,
                TargetEntity);
        Assert.IsTrue(deltaCandidates.All(c => c.Score < 1.0));

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 8.0,
                lagStepSeconds: 0.5);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(LiveYawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.AreEqual(6, candidates[0].MatchedWindows);
        Assert.AreEqual(6, candidates[0].TotalWindows);
        Assert.IsNotNull(candidates[0].BestLagSeconds);
        Assert.AreEqual(5.0, candidates[0].BestLagSeconds!.Value, 0.55);
    }

    [TestMethod]
    public void CorrelateWithLag_ConstantField_FailsValueMatch()
    {
        // A constant field never matches the turning packet yaw at any lag:
        // the lag path must return no candidate with score 1.0 (the turning
        // dumps prove the yaw field is the changing one).
        double[] dumpTimes = [6.0, 8.0, 10.0, 12.0, 14.0, 16.0];
        var snapshots = dumpTimes
            .Select(t => new RecordSnapshot(
                TimeSpan.FromSeconds(t),
                ConstantRegion(0.5f)))  // constant everywhere
            .ToArray();
        var yawSamples = Enumerable.Range(0, 9)
            .Select(i => Yaw(TimeSpan.FromSeconds(2.0 * i), PacketYawAt(2.0 * i)))
            .ToArray();

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 8.0,
                lagStepSeconds: 0.5);

        // No candidate can track a turning series with a constant value.
        Assert.IsEmpty(candidates.Where(c => c.Score >= 1.0 - 1e-9));
    }

    [TestMethod]
    public void CorrelateWithLag_PerDumpLag_FindsYawWhenMemoryLeadsLabel()
    {
        // OD-RECOVERY-089 (medvedkovo): the G2 replay-clock LABEL skew makes
        // the memory value at label-time t equal the packet yaw at t + lead
        // (memory LEADS the label). The shared-lag path only searches
        // memory-behind-packet, so the per-dump path with a bounded memory
        // lead must find +0x30 with a NEGATIVE best lag.
        //
        // Fixture: a continuously-varying ramp yaw (unique lag minimum), the
        // memory leading by 2.4 s: memory(t) = yaw(t + 2.4). The per-dump
        // search in [-6, +6] s must land on lag -2.4 for every dump.
        double[] dumpTimes = [6.0, 8.0, 10.0, 12.0, 14.0, 16.0];
        var snapshots = dumpTimes
            .Select(t => new RecordSnapshot(
                TimeSpan.FromSeconds(t),
                LaggedRegion(RampYaw(t + 2.4))))
            .ToArray();
        foreach (RecordSnapshot snapshot in snapshots)
        {
            for (int offset = 0; offset <= snapshot.Bytes.Length - 4; offset += 4)
            {
                if (offset == LiveYawOffset)
                {
                    continue;
                }

                BinaryPrimitives.WriteSingleLittleEndian(
                    snapshot.Bytes.AsSpan(offset), 0.7f);
            }
        }
        var yawSamples = Enumerable.Range(0, 41)
            .Select(i => Yaw(TimeSpan.FromSeconds(0.5 * i), RampYaw(0.5 * i)))
            .ToArray();

        // The shared one-directional path cannot see the lead: a lag >= 0
        // never aligns the memory (it holds the FUTURE packet).
        IReadOnlyList<HeadingCorrelationCandidate> sharedLeadCandidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 6.0,
                lagStepSeconds: 0.1);
        Assert.IsEmpty(sharedLeadCandidates.Where(c => c.Score >= 1.0 - 1e-9));

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 6.0,
                lagStepSeconds: 0.1,
                maxMemoryLeadSeconds: 6.0,
                perDumpLag: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(LiveYawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.AreEqual(6, candidates[0].MatchedWindows);
        Assert.AreEqual(6, candidates[0].TotalWindows);
        Assert.IsNotNull(candidates[0].BestLagSeconds);
        // The memory-leading alignment is a NEGATIVE lag near -2.4 s (within
        // the nearest-sample quantization of the 0.5 s yaw grid).
        Assert.IsLessThan(0.0, candidates[0].BestLagSeconds!.Value);
        Assert.IsLessThanOrEqualTo(
            1.0,
            Math.Abs(candidates[0].BestLagSeconds!.Value + 2.4),
            $"best lag {candidates[0].BestLagSeconds!.Value} should be near -2.4 s");
        Assert.IsNotNull(candidates[0].LagSpreadSeconds);
        Assert.IsLessThanOrEqualTo(1.0, candidates[0].LagSpreadSeconds!.Value);
    }

    [TestMethod]
    public void CorrelateWithLag_PerDumpLag_MatchesPerDumpVariableSkew_WhenSharedCaps()
    {
        // OD-RECOVERY-089 (medvedkovo, refined): the label skew is PER-DUMP
        // variable (0..2.5 s, OPPOSITE sign per replay). A shared lag cannot
        // align a mix of +2 s and -2 s per-dump skews (caps at 0.5); the
        // per-dump path must still hit 1.0 with the spread reported.
        //
        // Fixture: ramp yaw; dumps alternate a memory LAG of +2 s and a
        // memory LEAD of -2 s.
        double[] dumpTimes = [6.0, 8.0, 10.0, 12.0, 14.0, 16.0];
        double[] perDumpSkew = [2.0, -2.0, 2.0, -2.0, 2.0, -2.0];
        var snapshots = dumpTimes
            .Select((t, index) => new RecordSnapshot(
                TimeSpan.FromSeconds(t),
                LaggedRegion(RampYaw(t - perDumpSkew[index]))))
            .ToArray();
        foreach (RecordSnapshot snapshot in snapshots)
        {
            for (int offset = 0; offset <= snapshot.Bytes.Length - 4; offset += 4)
            {
                if (offset == LiveYawOffset)
                {
                    continue;
                }

                BinaryPrimitives.WriteSingleLittleEndian(
                    snapshot.Bytes.AsSpan(offset), 0.7f);
            }
        }
        var yawSamples = Enumerable.Range(0, 41)
            .Select(i => Yaw(TimeSpan.FromSeconds(0.5 * i), RampYaw(0.5 * i)))
            .ToArray();

        // Shared lag (even bidirectional) cannot exceed half the dumps.
        IReadOnlyList<HeadingCorrelationCandidate> sharedCandidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 6.0,
                lagStepSeconds: 0.1,
                maxMemoryLeadSeconds: 6.0);
        Assert.IsEmpty(sharedCandidates.Where(c => c.Score >= 1.0 - 1e-9));
        Assert.IsLessThan(
            1.0,
            sharedCandidates.Count == 0 ? 0.0 : sharedCandidates.Max(c => c.Score),
            "a shared lag cannot align alternating per-dump skews");

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 6.0,
                lagStepSeconds: 0.1,
                maxMemoryLeadSeconds: 6.0,
                perDumpLag: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(LiveYawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        Assert.IsNotNull(candidates[0].LagSpreadSeconds);
        // Lags alternate +2 / -2: the spread is ~4 s (nearest-sample
        // quantization on the 0.5 s grid widens it slightly).
        Assert.IsGreaterThanOrEqualTo(3.5, candidates[0].LagSpreadSeconds!.Value);
    }

    [TestMethod]
    public void CorrelateWithLag_PerDumpLag_FlatnessDemotesDriftingDecoy()
    {
        // The per-dump lag must not become a license for silent per-dump
        // fitting: a decoy that tracks the yaw during turns but drifts in
        // the stationary (CONTROL) segments is still demoted by flatness,
        // because controls are stationary and no lag makes a drifted value
        // equal the constant packet yaw.
        //
        // Fixture: step yaw (0 rad until t=10s, 1.2 rad after); the memory
        // LAGS by 3 s. The yaw field at +0x30 carries packet(t-3); a decoy at
        // +0x20 carries 0.9 rad in BOTH stationary segments (before AND after
        // the turn — the post-turn dumps are controls too, since the packet
        // yaw is constant there), matching the packet only on the single
        // turn dump. Every OTHER 4-byte offset carries the constant 0.7 (a
        // value the packet timeline never contains): a zero-filled field
        // would otherwise track the stationary 0.0 yaw AND slide its lag
        // into the pre-turn window on the turning dumps, scoring a
        // degenerate 1.0/1.0 tie.
        double[] dumpTimes = [4.0, 6.0, 8.0, 12.0, 14.0, 16.0];
        var snapshots = dumpTimes
            .Select(t =>
            {
                byte[] bytes = new byte[0x100];
                for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(
                        bytes.AsSpan(offset), 0.7f);
                }

                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan(LiveYawOffset), (float)PacketYawAt(t - 3.0));
                bool stationary = t < 10.0 || t > 12.0;
                double decoy = stationary ? 0.9 : PacketYawAt(t - 3.0);
                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan(DecoyOffset), (float)decoy);
                return new RecordSnapshot(TimeSpan.FromSeconds(t), bytes);
            })
            .ToArray();
        var yawSamples = Enumerable.Range(0, 17)
            .Select(i => Yaw(TimeSpan.FromSeconds(i), PacketYawAt(i)))
            .ToArray();

        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.CorrelateWithLag(
                snapshots,
                yawSamples,
                TargetEntity,
                maxLagSeconds: 8.0,
                lagStepSeconds: 0.1,
                perDumpLag: true);

        Assert.IsNotEmpty(candidates);
        Assert.AreEqual(LiveYawOffset, candidates[0].Offset);
        Assert.AreEqual(1.0, candidates[0].Score, 1e-9);
        Assert.AreEqual(1.0, candidates[0].Flatness, 1e-9);
        HeadingCorrelationCandidate decoy = candidates.Single(
            candidate => candidate.Offset == DecoyOffset);
        // The decoy matches only the single turn dump (it drifts in both
        // stationary segments), so it scores well below 1.0 and its flatness
        // is exactly 0 — the demotion mechanism works under per-dump lag.
        Assert.IsLessThan(1.0, decoy.Score);
        Assert.AreEqual(0.0, decoy.Flatness, 1e-9);
    }

    /// <summary>Ramp yaw fixture: 0.5 + 0.1 * seconds (strictly varying, so
    /// the per-dump lag minimum is unique per dump).</summary>
    private static double RampYaw(double seconds) => 0.5 + 0.1 * seconds;

    /// <summary>Packet yaw fixture: 0 rad before t=10s, 1.2 rad after.</summary>
    private static double PacketYawAt(double seconds) => seconds < 10.0 ? 0.0 : 1.2;

    /// <summary>Builds a region whose +0x30 yaw field carries the given
    /// memory yaw (the value applied after the memory-apply lag).</summary>
    private static byte[] LaggedRegion(double memoryYaw)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(LiveYawOffset), (float)memoryYaw);
        return bytes;
    }

    /// <summary>Builds a region whose every 4-byte-aligned float32 is the
    /// given constant (a field that can never track a turning yaw).</summary>
    private static byte[] ConstantRegion(float value)
    {
        byte[] bytes = new byte[0x100];
        for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        }

        return bytes;
    }
}

