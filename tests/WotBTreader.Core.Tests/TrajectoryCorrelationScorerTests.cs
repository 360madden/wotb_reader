using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class TrajectoryCorrelationScorerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Viewpoint entity: x follows a V shape (0 -> 100 at 50s -> 0 at 100s)
    /// so a phase-shifted linear copy cannot reproduce it; y climbs linearly
    /// 0 -> 40. A second entity is stationary and must contribute nothing.
    /// </summary>
    private static TrajectoryGroundTruth MovingGroundTruth() =>
        new(
            100_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 1,
                    "Viewpoint",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 100),
                        new TrajectorySample(250_000_000, 50, 10, 100),
                        new TrajectorySample(500_000_000, 100, 20, 100),
                        new TrajectorySample(750_000_000, 50, 30, 100),
                        new TrajectorySample(1_000_000_000, 0, 40, 100),
                    ]),
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 2,
                    "Stationary",
                    IsViewpoint: false,
                    [
                        new TrajectorySample(0, 7, 7, 7),
                        new TrajectorySample(100_000_000, 7, 7, 7),
                    ]),
            ]);

    private static List<CorrelationSample> XSeries(int startSecond, int count = 5, int stepSeconds = 10, double multiplier = 2)
    {
        List<CorrelationSample> samples = [];
        for (int index = 0; index < count; index++)
        {
            int second = startSecond + (index * stepSeconds);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), second * multiplier));
        }

        return samples;
    }

    [TestMethod]
    public void ReproducingSeriesScoresPerfectly()
    {
        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0x1000", XSeries(10))]);

        Assert.HasCount(1, results);
        TrajectoryCorrelationResult result = results[0];
        Assert.AreEqual("0x1000", result.Address);
        Assert.AreEqual("x", result.Axis);
        Assert.AreEqual(1, result.Sign);
        Assert.AreEqual(5, result.MatchCount);
        Assert.AreEqual(5, result.TotalSamples);
        Assert.AreEqual(1.0, result.Score, 0.001);
        Assert.AreEqual(1L, result.EntityId);
    }

    [TestMethod]
    public void SignFlippedSeriesIsDetected()
    {
        List<CorrelationSample> samples = XSeries(10).Select(sample =>
            sample with { Value = -sample.Value }).ToList();

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0x4000", samples)]);

        Assert.HasCount(1, results);
        Assert.AreEqual("x", results[0].Axis);
        Assert.AreEqual(-1, results[0].Sign);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
    }

    [TestMethod]
    public void OtherAxisIsSelectedIndependently()
    {
        // y(t) = 0.4 * second over the same wall window.
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), second * 0.4));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0x5000", samples)]);

        Assert.HasCount(1, results);
        Assert.AreEqual("y", results[0].Axis);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
    }

    [TestMethod]
    public void ConstantDecoyIsExcluded()
    {
        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [
                new ObservedAddressSeries("0x1000", XSeries(10)),
                new ObservedAddressSeries("0x2000",
                [
                    new CorrelationSample(Start.AddSeconds(10), 7),
                    new CorrelationSample(Start.AddSeconds(20), 7),
                    new CorrelationSample(Start.AddSeconds(30), 7),
                    new CorrelationSample(Start.AddSeconds(40), 7),
                ]),
            ]);

        Assert.HasCount(1, results);
        Assert.AreEqual("0x1000", results[0].Address);
    }

    [TestMethod]
    public void StationaryGroundAxisIsSkipped()
    {
        // Entity whose x is stationary (100) but y moves. A moving series that
        // happens to hover near 100 must NOT be attributed to x: a stationary
        // axis reproduces nothing.
        TrajectoryGroundTruth groundTruth = new(
            100_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 3,
                    "LateralMover",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 100, 0, 0),
                        new TrajectorySample(1_000_000_000, 100, 100, 0),
                    ]),
            ]);

        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), 100 + index));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundTruth,
            Start,
            [new ObservedAddressSeries("0x3000", samples)]);

        Assert.HasCount(0, results);
    }

    [TestMethod]
    public void TimeShiftSweepAbsorbsStartMarkerSkew()
    {
        // The wall anchor is 3 seconds late; the sweep still aligns the
        // series to the ground truth within tolerance.
        List<CorrelationSample> samples = XSeries(10).Select(sample =>
            sample with { WallTimeUtc = sample.WallTimeUtc.AddSeconds(3) }).ToList();

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0x1000", samples)],
            maxTimeShiftSeconds: 8);

        Assert.HasCount(1, results);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
    }

    [TestMethod]
    public void SeriesBeyondShiftWindowDoesNotMatch()
    {
        // A monotonic ramp (x = 2t over the whole battle) cannot coincide with
        // a series recorded 40s later: the ±8s sweep cannot bridge the gap.
        TrajectoryGroundTruth monotonic = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 9,
                    "Ramp",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        new TrajectorySample(1_000_000_000, 200, 0, 0),
                    ]),
            ]);
        List<CorrelationSample> samples = XSeries(10).Select(sample =>
            sample with { WallTimeUtc = sample.WallTimeUtc.AddSeconds(40) }).ToList();

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            monotonic,
            Start,
            [new ObservedAddressSeries("0x6000", samples)]);

        Assert.HasCount(0, results);
    }

    [TestMethod]
    public void MultipleEntitiesSelectTheMatchingOne()
    {
        TrajectoryGroundTruth groundTruth = new(
            100_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 10,
                    "A",
                    IsViewpoint: false,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        new TrajectorySample(1_000_000_000, 1000, 1000, 1000),
                    ]),
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 20,
                    "B",
                    IsViewpoint: false,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        new TrajectorySample(100_000_000, 0, 0, 0),
                    ]),
            ]);

        // This series tracks entity 10's x (slope 10/s): at 10s..50s it is 100..500.
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), second * 10));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundTruth,
            Start,
            [new ObservedAddressSeries("0x7000", samples)]);

        Assert.HasCount(1, results);
        Assert.AreEqual(10L, results[0].EntityId);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
    }

    [TestMethod]
    public void InterpolatedLookupMatchesBetweenSparseSamples()
    {
        // Ground truth has only the two endpoints; the scorer must interpolate.
        TrajectoryGroundTruth groundTruth = new(
            100_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 30,
                    "Sparse",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        new TrajectorySample(1_000_000_000, 200, 0, 0),
                    ]),
            ]);

        // x = 2 * second: at 10s..50s the interpolated ground truth is 20..100.
        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundTruth,
            Start,
            [new ObservedAddressSeries("0x8000", XSeries(10, multiplier: 2))]);

        Assert.HasCount(1, results);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
    }

    [TestMethod]
    public void NonFiniteObservedSamplesAreDropped()
    {
        List<CorrelationSample> samples = XSeries(10);
        samples.Insert(2, new CorrelationSample(Start.AddSeconds(30), double.NaN));

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0x9000", samples)]);

        Assert.HasCount(1, results);
        Assert.AreEqual(5, results[0].MatchCount);
        Assert.AreEqual(5, results[0].TotalSamples);
    }

    [TestMethod]
    public void PerSampleInconsistentShiftsDoNotScore()
    {
        // Values follow the trajectory, but each sample uses a different time
        // alignment (k*3s drift). The consistent-shift model must NOT treat
        // this as a reproduction: the anchor error is one constant offset.
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int wallSecond = 10 + (index * 10);
            int valueSecond = wallSecond + (index * 3);
            double value = valueSecond <= 50
                ? 2 * valueSecond
                : 200 - (2 * valueSecond);
            samples.Add(new CorrelationSample(Start.AddSeconds(wallSecond), value));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            MovingGroundTruth(),
            Start,
            [new ObservedAddressSeries("0xA000", samples)]);

        Assert.HasCount(1, results);
        Assert.IsLessThan(results[0].TotalSamples, results[0].MatchCount);
        Assert.IsLessThan(1.0, results[0].Score);
    }

    [TestMethod]
    public void FastMoverNeedsSubSecondShiftStep()
    {
        // A fast ramp (20 units per second) recorded with a 0.5s anchor error:
        // the whole-second sweep leaves a 0.5s residual = 10 units of constant
        // offset, permanently outside the 6-unit tolerance, so a whole-second
        // sweep would reject a TRUE field. The default 0.5s step must align it.
        TrajectoryGroundTruth ramp = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 40,
                    "FastRamp",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        // 2000 units over 100s = 20 units/s.
                        new TrajectorySample(1_000_000_000, 2000, 0, 0),
                    ]),
            ]);

        // Observed = GT(wall + 0.5s): value 20*second + 10.
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), (20 * second) + 10));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            ramp,
            Start,
            [new ObservedAddressSeries("0xB000", samples)]);

        Assert.HasCount(1, results);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
        Assert.AreEqual(0.5, results[0].ShiftSeconds, 0.001);
    }

    [TestMethod]
    public void WholeSecondStepRejectsTheSameFastMover()
    {
        TrajectoryGroundTruth ramp = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 41,
                    "FastRamp",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        // 2000 units over 100s = 20 units/s.
                        new TrajectorySample(1_000_000_000, 2000, 0, 0),
                    ]),
            ]);

        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), (20 * second) + 10));
        }

        // With a whole-second step the best integer shift leaves 0.5s residual
        // (10 units at 20 units/s) — the field cannot score.
        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            ramp,
            Start,
            [new ObservedAddressSeries("0xB100", samples)],
            shiftStepSeconds: 1.0);

        Assert.HasCount(0, results);
    }

    [TestMethod]
    public void WinningShiftIsReported()
    {
        // Steep ramp (30 units/s) so the shift is uniquely determined: the
        // tolerance 6 / slope 30 ambiguity band is 0.2s, narrower than a 0.5s
        // step, so only shift -3.0 aligns all samples. Wall anchor 3s late =>
        // observed = GT(wall - 3); the winning shift must be -3s so operators
        // can audit the anchor error.
        TrajectoryGroundTruth steepRamp = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 50,
                    "SteepRamp",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        // 3000 units over 100s = 30 units/s.
                        new TrajectorySample(1_000_000_000, 3000, 0, 0),
                    ]),
            ]);
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(
                Start.AddSeconds(second + 3),
                30 * second));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            steepRamp,
            Start,
            [new ObservedAddressSeries("0xC000", samples)],
            maxTimeShiftSeconds: 8);

        Assert.HasCount(1, results);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
        Assert.AreEqual(-3.0, results[0].ShiftSeconds, 0.001);
    }

    [TestMethod]
    public void InvalidArgumentsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TrajectoryCorrelationScorer.Score(null!, Start, []));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            TrajectoryCorrelationScorer.Score(MovingGroundTruth(), Start, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryCorrelationScorer.Score(MovingGroundTruth(), Start, [], tolerancePerAxis: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryCorrelationScorer.Score(MovingGroundTruth(), Start, [], maxTimeShiftSeconds: 121));
        // A default anchor produces silent, meaningless evidence (every tick
        // clamps to the last sample) — reject it instead.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryCorrelationScorer.Score(
                MovingGroundTruth(),
                DateTimeOffset.MinValue,
                [new ObservedAddressSeries("0xD000", XSeries(10))]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryCorrelationScorer.Score(
                MovingGroundTruth(),
                Start,
                [new ObservedAddressSeries("0xD000", XSeries(10))],
                shiftStepSeconds: 0));
    }
}
