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

/// <summary>
/// Correlated evidence for one monitored address. <see cref="ShiftSeconds"/>
/// is the sweep shift (real seconds) that produced the best match: negative
/// when the observed series trails the anchor (e.g. load latency after the
/// Start marker), approximately zero when the anchor matched battle start.
/// It is the single most diagnostic audit value for whether a survivor is
/// real.
///
/// <see cref="ShiftMinSeconds"/> and <see cref="ShiftMaxSeconds"/> bound the
/// AMBIGUITY BAND: every shift in [Min, Max] achieves the same match count on
/// a locally linear trajectory (band width = tolerance / |local slope|). The
/// reported <see cref="ShiftSeconds"/> is the closest-to-zero point of that
/// band; the band edges expose how wide the alignment really is and whether
/// it touches the sweep edge (a boundary-riding alignment is a bad-anchor
/// symptom even when the reported shift looks benign).
/// </summary>
public sealed record TrajectoryCorrelationResult(
    string Address,
    ParticipantId? ParticipantId,
    long? EntityId,
    string Axis,
    int Sign,
    double ShiftSeconds,
    double ShiftMinSeconds,
    double ShiftMaxSeconds,
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

    /// <summary>
    /// Granularity of the time-shift sweep. Whole-second steps leave a
    /// residual of up to 0.5s, which is a CONSTANT position offset of
    /// speed x residual on every sample: a fast mover at 17 m/s is offset up
    /// to 8.5 units and permanently outside a 6-unit tolerance, so a true
    /// field would score ~0. Sub-second steps keep speed x residual inside
    /// tolerance (0.25s x 17 = 4.25 units).
    /// </summary>
    public const double DefaultShiftStepSeconds = 0.5;

    private static readonly string[] Axes = ["x", "y", "z"];

    /// <summary>
    /// Scores every observation against the ground truth. Returns one result
    /// per scored address (addresses with a moving series that matched at least
    /// one entity axis within tolerance), ordered by match count descending,
    /// then observed span descending.
    ///
    /// Match semantics: a sample matches only if its (shifted) replay tick
    /// falls INSIDE the entity's ground-sample window AND its value is within
    /// tolerance of the interpolated ground value. Samples whose shifted tick
    /// lands outside the window (before battle start or after battle end) are
    /// counted in <see cref="TrajectoryCorrelationResult.TotalSamples"/> but
    /// never match -- out-of-window alignment is not evidence, so it does not
    /// contribute to <see cref="TrajectoryCorrelationResult.MatchCount"/> and
    /// lowers the resulting <see cref="TrajectoryCorrelationResult.Score"/>.
    /// </summary>
    public static IReadOnlyList<TrajectoryCorrelationResult> Score(
        TrajectoryGroundTruth groundTruth,
        DateTimeOffset replayStartWallTimeUtc,
        IReadOnlyList<ObservedAddressSeries> observations,
        double tolerancePerAxis = DefaultTolerancePerAxis,
        int maxTimeShiftSeconds = DefaultMaxTimeShiftSeconds,
        double minMovingSpan = DefaultMinMovingSpan,
        double shiftStepSeconds = DefaultShiftStepSeconds,
        double replayClockTicksPerSecond = ReplayClockTicksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentNullException.ThrowIfNull(observations);
        // A default anchor turns every tick into a huge positive value that
        // clamps to the last ground-truth sample: silent, meaningless evidence
        // instead of an error.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            replayStartWallTimeUtc,
            DateTimeOffset.MinValue,
            nameof(replayStartWallTimeUtc));

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

        if (!double.IsFinite(shiftStepSeconds) || shiftStepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shiftStepSeconds));
        }

        List<AxisSeries> groundSeries = BuildGroundSeries(groundTruth, tolerancePerAxis);
        if (groundSeries.Count == 0)
        {
            return [];
        }

        // Time-shift sweep in shiftStepSeconds increments: absorbs Start-marker
        // anchor error, wall-clock skew, and load latency. Sub-second steps keep
        // the residual position offset (speed x residual) inside tolerance for
        // fast movers; whole-second steps would reject them (see
        // DefaultShiftStepSeconds).
        int shiftCount = (int)((2.0 * maxTimeShiftSeconds) / shiftStepSeconds) + 1;
        double[] shifts = new double[shiftCount];
        for (int index = 0; index < shiftCount; index++)
        {
            shifts[index] = -maxTimeShiftSeconds + (index * shiftStepSeconds);
        }

        // Clamp the final candidate exactly to +max; floating-point accumulation
        // can otherwise end at max + epsilon.
        shifts[^1] = maxTimeShiftSeconds;

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
            double bestShiftSeconds = 0;
            double bestShiftMinSeconds = 0;
            double bestShiftMaxSeconds = 0;
            int bestAxisIndex = 0;
            int bestSign = 1;
            ParticipantId? bestParticipant = null;
            long? bestEntity = null;
            // Band width of the current best alignment. On an exact tie in
            // match count between two (axis, sign, entity) candidates, the
            // NARROWER ambiguity band is preferred: a tie means the address
            // reproduces two trajectories equally often, and the tighter
            // alignment is the more precise attribution. Without this, the
            // first-iterated axis (x of the first entity) wins every tie,
            // structurally biasing the evidence toward axis x.
            double bestBandWidth = double.PositiveInfinity;

            // Wall-relative tick for every sample is fixed per address: it does
            // not depend on the ground series or the sign, so compute it once
            // instead of once per (ground, sign) pair.
            double[] baseTicks = new double[total];
            for (int index = 0; index < total; index++)
            {
                double wallSeconds =
                    (valid[index].WallTimeUtc - replayStartWallTimeUtc).TotalSeconds;
                baseTicks[index] = wallSeconds * replayClockTicksPerSecond;
            }

            foreach (AxisSeries ground in groundSeries)
            {
                for (int signIndex = 0; signIndex < 2; signIndex++)
                {
                    int sign = signIndex == 0 ? 1 : -1;
                    (int matches, double shiftSeconds, double shiftMinSeconds, double shiftMaxSeconds) =
                        CountMatches(
                            valid,
                            baseTicks,
                            ground,
                            sign,
                            shifts,
                            tolerancePerAxis,
                            replayClockTicksPerSecond);
                    double bandWidth = shiftMaxSeconds - shiftMinSeconds;
                    if (matches > bestMatch
                        || (matches == bestMatch && bestMatch > 0 && bandWidth < bestBandWidth))
                    {
                        bestMatch = matches;
                        bestShiftSeconds = shiftSeconds;
                        bestShiftMinSeconds = shiftMinSeconds;
                        bestShiftMaxSeconds = shiftMaxSeconds;
                        bestBandWidth = bandWidth;
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
                bestShiftSeconds,
                bestShiftMinSeconds,
                bestShiftMaxSeconds,
                bestMatch,
                total,
                span,
                (double)bestMatch / total));
        }

        return [.. results.OrderByDescending(static r => r.MatchCount)
            .ThenByDescending(static r => r.Span)];
    }

    private static (int Matches, double ShiftSeconds, double ShiftMinSeconds, double ShiftMaxSeconds) CountMatches(
        List<CorrelationSample> samples,
        double[] baseTicks,
        AxisSeries ground,
        int sign,
        double[] shifts,
        double tolerance,
        double ticksPerSecond)
    {
        // One CONSISTENT time alignment per (entity, axis, sign): the anchor
        // error is a single constant offset, so the same shift must fit the
        // whole series. Counting per-sample independent shifts would let an
        // address wander within the swept band without ever reproducing a
        // coherent trajectory — weak, noisy evidence. The max over shifts is
        // the correct alignment model and a far stronger discriminator.
        int best = 0;
        double bestShiftSeconds = 0;
        double shiftMinSeconds = 0;
        double shiftMaxSeconds = 0;
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

            // On a locally linear trajectory many shifts within the tolerance
            // band tie. The reported shift is the closest-to-zero candidate
            // (the anchor error is expected small, so the band's center is the
            // least misleading point), but the BAND [min, max] is tracked for
            // every shift achieving the best count so callers can see the full
            // alignment ambiguity and detect sweep-edge riding.
            if (matches > best)
            {
                best = matches;
                bestShiftSeconds = shifts[shiftIndex];
                shiftMinSeconds = shifts[shiftIndex];
                shiftMaxSeconds = shifts[shiftIndex];
            }
            else if (matches == best && best > 0)
            {
                shiftMinSeconds = Math.Min(shiftMinSeconds, shifts[shiftIndex]);
                shiftMaxSeconds = Math.Max(shiftMaxSeconds, shifts[shiftIndex]);
                if (Math.Abs(shifts[shiftIndex]) < Math.Abs(bestShiftSeconds))
                {
                    bestShiftSeconds = shifts[shiftIndex];
                }
            }

            // No early break on perfect alignment: scanning every shift keeps
            // the ambiguity band COMPLETE (an early exit at shift 0 would
            // truncate the positive extent of a wide symmetric band, e.g.
            // reporting [-15, 0] for a true [-15, +15]). The full sweep is a
            // bounded one-shot cost (<= 241 shifts at 120s/0.5s step).
        }

        return (best, bestShiftSeconds, shiftMinSeconds, shiftMaxSeconds);
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
    /// Lookup succeeds ONLY inside the sample window: ticks before the first
    /// sample or after the last return false (no ground truth exists there),
    /// so a shift pushing the series outside the battle window cannot
    /// fabricate matches against the endpoint's constant value. This is what
    /// keeps the edge-riding false survivors (spawn-plateau / end-position
    /// coincidences) out of the evidence.
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
                // Before the first ground sample: NO ground truth exists there.
                // Clamping to the first sample value would fabricate matches
                // for any address whose value coincides with the tank's
                // spawn/start position (a constant plateau), producing
                // exactly the edge-riding false survivors the driver's
                // shift-band audit exists to catch. A shift that pushes the
                // series before the window simply does not align.
                value = 0;
                return false;
            }

            if (insertion >= _samples.Length)
            {
                // Past the last ground sample (battle end): no ground truth
                // exists there either. Clamping to the final position would
                // match any address parked at the tank's end position, again
                // fabricating tail evidence.
                value = 0;
                return false;
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
