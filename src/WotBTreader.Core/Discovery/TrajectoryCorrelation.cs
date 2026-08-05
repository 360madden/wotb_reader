namespace WotBTreader.Core.Discovery;

/// <summary>
/// One ground-truth position sample decoded from a replay.
/// </summary>
public sealed record TrajectorySample(
    long ReplayTimeTicks,
    double X,
    double Y,
    double Z);

/// <summary>
/// Ground-truth position time-series for one tracked entity. Samples are
/// monotone in <see cref="TrajectorySample.ReplayTimeTicks"/>.
/// </summary>
public sealed record EntityTrajectory(
    ParticipantId? ParticipantId,
    long? EntityId,
    string? TankName,
    bool IsViewpoint,
    IReadOnlyList<TrajectorySample> Samples);

/// <summary>All ground-truth trajectories for one decoded battle session.</summary>
public sealed record TrajectoryGroundTruth(
    long DurationTicks,
    IReadOnlyList<EntityTrajectory> Entities);

/// <summary>One observed read of a monitored memory address.</summary>
public sealed record CorrelationSample(
    DateTimeOffset WallTimeUtc,
    double Value);

/// <summary>Monitored value series for one staged memory address.</summary>
public sealed record ObservedAddressSeries(
    string Address,
    IReadOnlyList<CorrelationSample> Samples);

/// <summary>Correlated evidence for one monitored address.</summary>
public sealed record TrajectoryCorrelationResult(
    string Address,
    ParticipantId? ParticipantId,
    long? EntityId,
    string Axis,
    int Sign,
    int MatchCount,
    int TotalSamples,
    double Span,
    double Score);

/// <summary>
/// Scores monitored address series against the decoded replay trajectory
/// (strategy v4 — replay-guided correlation). This is the layer that makes
/// the exact-pause requirement disappear: the replay simply plays at 1x, the
/// staged address set is re-read repeatedly, and each address's value series
/// is scored against the known replay time-series.
///
/// Design notes:
/// <list type="bullet">
/// <item>The game replay clock runs at
/// <see cref="ReplayClockTicksPerSecond"/> ticks per real second at 1x speed
/// (verified against decoded sessions: the synthetic 120s fixture is exactly
/// 1,200,000,000 ticks and the real decode puts the HUD 1:00 frame at
/// 599,839,248 ticks ≈ 59.98s). The driver anchors wall time at the replay
/// Start marker; residual anchor error is absorbed by the time-shift sweep.</item>
/// <item>Each staged address is read as a single numeric value, so the
/// correlation is per-axis: an address holds one coordinate component (x, y,
/// or z, with possible sign flip) of one entity. Once one component is found,
/// the sibling components live at ±4-byte neighbors (the "candidate family
/// maps fast" step of the strategy).</item>
/// <item>Stationary ground-truth axes are non-discriminating and skipped;
/// constant observed addresses are excluded by the minimum-moving-span
/// threshold. The remaining evidence — few unrelated addresses reproduce a
/// movement sequence with direction/speed changes — is exactly the strategy's
/// proof.</item>
/// </list>
/// </summary>
public static class TrajectoryCorrelationScorer
{
    /// <summary>Replay clock resolution: 10,000,000 ticks per real second.</summary>
    public const double ReplayClockTicksPerSecond = 10_000_000.0;

    public const double DefaultTolerancePerAxis = 6.0;
    public const int DefaultMaxTimeShiftSeconds = 8;
    public const double DefaultMinMovingSpan = 0.5;
    public const int MaximumTimeShiftSeconds = 120;

    private static readonly string[] Axes = ["x", "y", "z"];

    /// <summary>
    /// Scores every observation against the ground truth. Returns one result
    /// per scored address (addresses with a moving series that matched at least
    /// one entity axis within tolerance), ordered by match count descending,
    /// then observed span descending.
    /// </summary>
    public static IReadOnlyList<TrajectoryCorrelationResult> Score(
        TrajectoryGroundTruth groundTruth,
        DateTimeOffset replayStartWallTimeUtc,
        IReadOnlyList<ObservedAddressSeries> observations,
        double tolerancePerAxis = DefaultTolerancePerAxis,
        int maxTimeShiftSeconds = DefaultMaxTimeShiftSeconds,
        double minMovingSpan = DefaultMinMovingSpan,
        double replayClockTicksPerSecond = ReplayClockTicksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentNullException.ThrowIfNull(observations);
        if (!double.IsFinite(tolerancePerAxis) || tolerancePerAxis <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerancePerAxis));
        }

        if (!double.IsFinite(replayClockTicksPerSecond) || replayClockTicksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replayClockTicksPerSecond));
        }

        if (maxTimeShiftSeconds is < 0 or > MaximumTimeShiftSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTimeShiftSeconds));
        }

        if (!double.IsFinite(minMovingSpan) || minMovingSpan < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minMovingSpan));
        }

        List<AxisSeries> groundSeries = BuildGroundSeries(groundTruth, tolerancePerAxis);
        if (groundSeries.Count == 0)
        {
            return [];
        }

        // Time-shift sweep (whole seconds): absorbs Start-marker anchor error
        // and wall-clock skew. A one-second tick mis-alignment at battle speed
        // is still within tolerance for typical tank movement.
        int[] shifts = new int[(2 * maxTimeShiftSeconds) + 1];
        for (int shift = -maxTimeShiftSeconds; shift <= maxTimeShiftSeconds; shift++)
        {
            shifts[shift + maxTimeShiftSeconds] = shift;
        }

        List<TrajectoryCorrelationResult> results = [];
        foreach (ObservedAddressSeries observation in observations)
        {
            if (observation is null
                || string.IsNullOrWhiteSpace(observation.Address)
                || observation.Samples is null)
            {
                continue;
            }

            List<CorrelationSample> valid = [];
            double minValue = double.PositiveInfinity;
            double maxValue = double.NegativeInfinity;
            foreach (CorrelationSample sample in observation.Samples)
            {
                if (sample is null || !double.IsFinite(sample.Value))
                {
                    continue;
                }

                valid.Add(sample);
                if (sample.Value < minValue) minValue = sample.Value;
                if (sample.Value > maxValue) maxValue = sample.Value;
            }

            if (valid.Count < 2)
            {
                continue;
            }

            double span = maxValue - minValue;
            if (span < minMovingSpan)
            {
                // Constant decoy: a frozen value reproduces no movement and
                // proves nothing about a position field.
                continue;
            }

            int total = valid.Count;
            int bestMatch = 0;
            int bestAxisIndex = 0;
            int bestSign = 1;
            ParticipantId? bestParticipant = null;
            long? bestEntity = null;

            foreach (AxisSeries ground in groundSeries)
            {
                for (int signIndex = 0; signIndex < 2; signIndex++)
                {
                    int sign = signIndex == 0 ? 1 : -1;
                    int matches = CountMatches(
                        valid,
                        ground,
                        sign,
                        shifts,
                        tolerancePerAxis,
                        replayClockTicksPerSecond,
                        replayStartWallTimeUtc);
                    if (matches > bestMatch)
                    {
                        bestMatch = matches;
                        bestAxisIndex = ground.AxisIndex;
                        bestSign = sign;
                        bestParticipant = ground.ParticipantId;
                        bestEntity = ground.EntityId;
                        if (bestMatch == total)
                        {
                            // Perfect reproduction — cannot improve further.
                            break;
                        }
                    }
                }

                if (bestMatch == total)
                {
                    break;
                }
            }

            if (bestMatch == 0)
            {
                continue;
            }

            results.Add(new TrajectoryCorrelationResult(
                observation.Address,
                bestParticipant,
                bestEntity,
                Axes[bestAxisIndex],
                bestSign,
                bestMatch,
                total,
                span,
                (double)bestMatch / total));
        }

        return [.. results.OrderByDescending(static r => r.MatchCount)
            .ThenByDescending(static r => r.Span)];
    }

    private static int CountMatches(
        List<CorrelationSample> samples,
        AxisSeries ground,
        int sign,
        int[] shifts,
        double tolerance,
        double ticksPerSecond,
        DateTimeOffset replayStartWallTimeUtc)
    {
        // One CONSISTENT time alignment per (entity, axis, sign): the anchor
        // error is a single constant offset, so the same shift must fit the
        // whole series. Counting per-sample independent shifts would let an
        // address wander within the swept band without ever reproducing a
        // coherent trajectory — weak, noisy evidence. The max over shifts is
        // the correct alignment model and a far stronger discriminator.
        double[] baseTicks = new double[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            double wallSeconds =
                (samples[index].WallTimeUtc - replayStartWallTimeUtc).TotalSeconds;
            baseTicks[index] = wallSeconds * ticksPerSecond;
        }

        int best = 0;
        for (int shiftIndex = 0; shiftIndex < shifts.Length; shiftIndex++)
        {
            double shiftTicks = shifts[shiftIndex] * ticksPerSecond;
            int matches = 0;
            for (int index = 0; index < samples.Count; index++)
            {
                if (ground.TryGetValueAtTick(
                        (long)(baseTicks[index] + shiftTicks),
                        out double expected)
                    && Math.Abs((sign * samples[index].Value) - expected) <= tolerance)
                {
                    matches++;
                }
            }

            if (matches > best)
            {
                best = matches;
                if (best == samples.Count)
                {
                    // Perfect alignment under one shift — cannot improve.
                    break;
                }
            }
        }

        return best;
    }

    private static List<AxisSeries> BuildGroundSeries(
        TrajectoryGroundTruth groundTruth,
        double tolerance)
    {
        List<AxisSeries> series = [];
        if (groundTruth.Entities is null)
        {
            return series;
        }

        foreach (EntityTrajectory entity in groundTruth.Entities)
        {
            if (entity?.Samples is null || entity.Samples.Count < 2)
            {
                continue;
            }

            TrajectorySample[] ordered = [.. entity.Samples
                .Where(static s => s is not null)
                .OrderBy(static s => s!.ReplayTimeTicks)];
            for (int axis = 0; axis < 3; axis++)
            {
                double min = double.PositiveInfinity;
                double max = double.NegativeInfinity;
                foreach (TrajectorySample sample in ordered)
                {
                    double value = SampleValue(sample, axis);
                    if (value < min) min = value;
                    if (value > max) max = value;
                }

                if (max - min <= tolerance)
                {
                    // Stationary axis: no movement to reproduce; matching it
                    // would prove nothing (constants match constants).
                    continue;
                }

                series.Add(new AxisSeries(
                    entity.ParticipantId,
                    entity.EntityId,
                    axis,
                    ordered));
            }
        }

        return series;
    }

    private static double SampleValue(TrajectorySample sample, int axis) => axis switch
    {
        0 => sample.X,
        1 => sample.Y,
        _ => sample.Z,
    };

    /// <summary>
    /// One entity axis with O(log n) piecewise-linear lookup by replay tick.
    /// Clamps outside the sample window.
    /// </summary>
    private sealed class AxisSeries
    {
        private readonly TrajectorySample[] _samples;
        private readonly double[] _ticks;

        internal AxisSeries(
            ParticipantId? participantId,
            long? entityId,
            int axisIndex,
            TrajectorySample[] samples)
        {
            ParticipantId = participantId;
            EntityId = entityId;
            AxisIndex = axisIndex;
            _samples = samples;
            _ticks = new double[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                _ticks[index] = samples[index].ReplayTimeTicks;
            }
        }

        internal ParticipantId? ParticipantId { get; }

        internal long? EntityId { get; }

        internal int AxisIndex { get; }

        internal bool TryGetValueAtTick(long tick, out double value)
        {
            int index = Array.BinarySearch(_ticks, tick);
            if (index >= 0)
            {
                value = SampleValue(_samples[index], AxisIndex);
                return true;
            }

            int insertion = ~index;
            if (insertion == 0)
            {
                value = SampleValue(_samples[0], AxisIndex);
                return true;
            }

            if (insertion >= _samples.Length)
            {
                value = SampleValue(_samples[^1], AxisIndex);
                return true;
            }

            TrajectorySample left = _samples[insertion - 1];
            TrajectorySample right = _samples[insertion];
            double leftTick = _ticks[insertion - 1];
            double rightTick = _ticks[insertion];
            double tickSpan = rightTick - leftTick;
            double fraction = tickSpan <= 0 ? 0 : (tick - leftTick) / tickSpan;
            double leftValue = SampleValue(left, AxisIndex);
            double rightValue = SampleValue(right, AxisIndex);
            value = leftValue + ((rightValue - leftValue) * fraction);
            return true;
        }
    }
}
