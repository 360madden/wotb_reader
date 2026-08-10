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
}
