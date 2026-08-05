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
    public void AmbiguityBandIsReportedAndCanMaskEdgeAlignment()
    {
        // Slow slope (2 units/s): the tie band is tolerance/slope = 3s wide.
        // The series was recorded 25s AFTER the anchor (observed = GT(wall - 25)),
        // so the true alignment is -25 and every shift in [-28, -22] matches
        // perfectly. The closest-to-zero REPORTED shift is -22, which looks
        // benign; only the band edges (-28) expose that the alignment rides
        // the sweep boundary. This is the masking the driver's band-based
        // edge audit is designed to catch.
        TrajectoryGroundTruth slowRamp = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 60,
                    "SlowRamp",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        // 200 units over 100s = 2 units/s.
                        new TrajectorySample(1_000_000_000, 200, 0, 0),
                    ]),
            ]);
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSample(Start.AddSeconds(second + 25), 2 * second));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            slowRamp,
            Start,
            [new ObservedAddressSeries("0xE000", samples)],
            maxTimeShiftSeconds: 30);

        Assert.HasCount(1, results);
        Assert.AreEqual(1.0, results[0].Score, 0.001);
        // Reported shift is the closest-to-zero point of the band [-28, -22].
        Assert.AreEqual(-22.0, results[0].ShiftSeconds, 0.001);
        Assert.AreEqual(-28.0, results[0].ShiftMinSeconds, 0.001);
        Assert.AreEqual(-22.0, results[0].ShiftMaxSeconds, 0.001);
    }

    [TestMethod]
    public void SeriesParkedAtEndPositionOutsideWindowDoesNotFabricateMatches()
    {
        // Regression for the endpoint-clamp fabrication: a series whose best
        // shift pushes its ticks PAST the last ground sample must NOT match
        // by clamping to the tank's final position (a parked address at the
        // end position would otherwise score as perfect evidence). Ground
        // truth ends at 100s (x = 200); the observed series is parked at
        // x ~ 200 from 110s onward -- outside the window at every sweep shift.
        TrajectoryGroundTruth groundTruth = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 70,
                    "EndsAt100s",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(0, 0, 0, 0),
                        new TrajectorySample(1_000_000_000, 200, 0, 0),
                    ]),
            ]);
        // A MOVING series (span 4, above the moving-span threshold so it is
        // not a constant decoy) parked around the end value 200 at wall times
        // 140s..160s. The ground window ends at 100s and the sweep is +-30s,
        // so the closest any sample can get to the window is 110s -- every
        // tick is past battle end at every shift. Before the fix, the tail
        // clamp returned the last sample (200) for all of these, so the
        // parked series scored perfect evidence (5/5).
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 140 + (index * 5);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), 200 + index));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundTruth,
            Start,
            [new ObservedAddressSeries("0xF000", samples)],
            maxTimeShiftSeconds: 30);

        // No clamp: ticks past battle end are no-match, so nothing aligns.
        Assert.HasCount(0, results);
    }

    [TestMethod]
    public void SeriesParkedAtSpawnBeforeWindowDoesNotFabricateMatches()
    {
        // Head-clamp regression: an entity whose trajectory starts at 100s
        // (late spawn) and a series parked at the spawn value from 20s on.
        // Ticks before the first ground sample must not clamp to the spawn
        // position -- that fabricated a "spawn-plateau coincidence" survivor.
        TrajectoryGroundTruth groundTruth = new(
            1_000_000_000,
            [
                new EntityTrajectory(
                    new ParticipantId(Guid.NewGuid()),
                    EntityId: 71,
                    "SpawnsAt100s",
                    IsViewpoint: true,
                    [
                        new TrajectorySample(1_000_000_000, 50, 0, 0),
                        new TrajectorySample(1_500_000_000, 150, 0, 0),
                    ]),
            ]);
        // A MOVING series (span 4, above the 0.5 moving-span threshold so it
        // is not a constant decoy) parked around the spawn value: every sample
        // is within tolerance of 50, and no sweep shift can move it inside the
        // [100s, 150s] window (it would need +60s, beyond the 30s sweep).
        // Before the fix, the head clamp matched all 5 samples against the
        // clamped spawn value 50 and fabricated a perfect score.
        List<CorrelationSample> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 20 + (index * 5);
            samples.Add(new CorrelationSample(Start.AddSeconds(second), 50 + index));
        }

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundTruth,
            Start,
            [new ObservedAddressSeries("0xF100", samples)],
            maxTimeShiftSeconds: 30);

        Assert.HasCount(0, results);
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
