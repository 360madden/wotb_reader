using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// One decoded yaw sample for an entity: the packet-derived facing (radians)
/// at a replay time, persisted in <c>position_samples.yaw</c> (migration 5).
/// Pure offline ground truth — no process access.
/// </summary>
public sealed record YawSample(
    TimeSpan ReplayTime,
    long EntityId,
    double YawRadians);

/// <summary>
/// The decoded yaw timeline for one battle session: the replay clock span
/// plus every persisted rotation sample, used as ground truth by the facing
/// (yaw) record-diffing discovery playbook.
/// </summary>
public sealed record YawGroundTruth(
    TimeSpan Duration,
    IReadOnlyList<YawSample> Samples);

/// <summary>
/// One yaw window a candidate matched: the replay span and the expected
/// (wrapped) radian delta the field's float32 move reproduced.
/// </summary>
public sealed record MatchedYawWindow(
    TimeSpan FromReplayTime,
    TimeSpan ToReplayTime,
    double ExpectedDeltaRadians);

/// <summary>
/// A ranked facing-correlation candidate: a 4-byte-aligned float32 field
/// whose wrap-aware value delta matches the target entity's packet yaw delta
/// in the same replay-time windows. <see cref="Score"/> is matched / turn
/// windows; <see cref="Flatness"/> is the fraction of stationary (control)
/// windows in which the field was unchanged (1.0 when there are no control
/// windows) — it separates the yaw field (constant when the tank is
/// stationary, proven) from drifting decoys. Ranking prefers score, then
/// flatness, then precision, then offset.
/// </summary>
public sealed record HeadingCorrelationCandidate(
    int Offset,
    double Score,
    int MatchedWindows,
    int TotalWindows,
    int ChangedWindows,
    double Flatness,
    int ControlWindows,
    int ChangedControlWindows,
    IReadOnlyList<MatchedYawWindow>? MatchedWindowList,
    string Explanation);

/// <summary>
/// Correlates the target entity's packet-derived yaw against the bucketed
/// change windows: a candidate is a 4-byte-aligned float32 field whose
/// little-endian value delta (wrapped to [-pi, pi]) equals the yaw delta
/// (also wrapped) between the window's snapshots. Pure and offline; the
/// memory side (trusted reader) is a separate approved-session step. Windows
/// whose expected |delta| exceeds <see cref="ControlDeltaThresholdRadians"/>
/// are TURN windows (score denominator); the rest are CONTROL windows
/// (flatness denominator — the yaw field must be unchanged there). Windows
/// whose From/To replay times fall outside the yaw sample span have no
/// ground truth and are excluded from both denominators.
/// </summary>
public static class HeadingCorrelator
{
    /// <summary>Default match tolerance in radians (~2.9 degrees).</summary>
    public const double DefaultToleranceRadians = 0.05;

    /// <summary>Expected |delta| at or below this is a stationary control
    /// window (~1.1 degrees).</summary>
    public const double ControlDeltaThresholdRadians = 0.02;

    /// <summary>
    /// Ranks candidate yaw fields for <paramref name="targetEntityId"/>. Only
    /// offsets that matched at least one turn window are returned, ordered by
    /// score descending, then flatness descending, then precision
    /// (matched / changed turn-windows) descending, then offset ascending.
    /// </summary>
    public static IReadOnlyList<HeadingCorrelationCandidate> Correlate(
        IReadOnlyList<ByteChangeWindow> windows,
        IReadOnlyList<YawSample> yawSamples,
        long targetEntityId,
        double toleranceRadians = DefaultToleranceRadians)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(yawSamples);
        if (!double.IsFinite(toleranceRadians) || toleranceRadians <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceRadians));
        }

        int regionLength = windows.Count > 0 ? windows[0].Before.Length : 0;
        foreach (ByteChangeWindow window in windows)
        {
            if (window.Before.Length != regionLength || window.After.Length != regionLength)
            {
                throw new ArgumentException(
                    "All change windows must carry the same region length.",
                    nameof(windows));
            }
        }

        YawLookup lookup = new(
            [.. yawSamples
                .Where(sample => sample is not null && sample.EntityId == targetEntityId)
                .OrderBy(sample => sample.ReplayTime)]);
        if (lookup.IsEmpty)
        {
            return [];
        }

        // Expected wrapped delta per window from the ground truth. Windows
        // without ground truth on either end are excluded entirely.
        Dictionary<ByteChangeWindow, double> expectedByWindow = [];
        List<ByteChangeWindow> controlWindows = [];
        foreach (ByteChangeWindow window in windows)
        {
            if (!lookup.TryGetValueAt(window.FromReplayTime, out double fromYaw)
                || !lookup.TryGetValueAt(window.ToReplayTime, out double toYaw))
            {
                continue;
            }

            double expected = WrapPi(toYaw - fromYaw);
            if (Math.Abs(expected) > ControlDeltaThresholdRadians)
            {
                expectedByWindow[window] = expected;
            }
            else
            {
                controlWindows.Add(window);
            }
        }

        if (expectedByWindow.Count == 0)
        {
            return [];
        }

        List<HeadingCorrelationCandidate> candidates = [];
        int maxOffset = regionLength - sizeof(float);
        for (int offset = 0; offset <= maxOffset; offset += sizeof(float))
        {
            int matched = 0;
            int changed = 0;
            List<MatchedYawWindow> matchedWindows = [];
            foreach ((ByteChangeWindow window, double expected) in expectedByWindow)
            {
                float before = BinaryPrimitives.ReadSingleLittleEndian(window.Before.AsSpan(offset));
                float after = BinaryPrimitives.ReadSingleLittleEndian(window.After.AsSpan(offset));
                double delta = WrapPi(after - before);
                if (Math.Abs(delta) <= toleranceRadians)
                {
                    continue;
                }

                changed++;
                if (Math.Abs(WrapPi(delta - expected)) <= toleranceRadians)
                {
                    matched++;
                    matchedWindows.Add(new MatchedYawWindow(
                        window.FromReplayTime,
                        window.ToReplayTime,
                        expected));
                }
            }

            if (matched == 0)
            {
                continue;
            }

            int controlChanged = 0;
            foreach (ByteChangeWindow control in controlWindows)
            {
                float before = BinaryPrimitives.ReadSingleLittleEndian(control.Before.AsSpan(offset));
                float after = BinaryPrimitives.ReadSingleLittleEndian(control.After.AsSpan(offset));
                if (Math.Abs(WrapPi(after - before)) > toleranceRadians)
                {
                    controlChanged++;
                }
            }

            double score = (double)matched / expectedByWindow.Count;
            double flatness = controlWindows.Count == 0
                ? 1.0
                : (double)(controlWindows.Count - controlChanged) / controlWindows.Count;
            candidates.Add(new HeadingCorrelationCandidate(
                offset,
                score,
                matched,
                expectedByWindow.Count,
                changed,
                flatness,
                controlWindows.Count,
                controlChanged,
                matchedWindows,
                $"float32 at +0x{offset:X}: yaw delta matched {matched}/{expectedByWindow.Count} "
                + $"turn windows (precision {matched}/{changed}); flatness "
                + $"{flatness:0.##} ({controlWindows.Count - controlChanged}/"
                + $"{controlWindows.Count} control windows unchanged); wrap-aware |delta - expected| <= {toleranceRadians:0.###} rad"));
        }

        return [.. candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Flatness)
            .ThenByDescending(candidate => (double)candidate.MatchedWindows / candidate.ChangedWindows)
            .ThenBy(candidate => candidate.Offset)];
    }

    /// <summary>Wraps an angle to [-pi, pi].</summary>
    public static double WrapPi(double angle)
    {
        double wrapped = angle % (2.0 * Math.PI);
        if (wrapped > Math.PI)
        {
            wrapped -= 2.0 * Math.PI;
        }
        else if (wrapped < -Math.PI)
        {
            wrapped += 2.0 * Math.PI;
        }

        return wrapped;
    }

    /// <summary>
    /// Nearest-sample yaw lookup by replay time, fail-closed outside the
    /// sample span (no ground truth exists there). The reader labels each
    /// dump with the replay clock, which lands on the packet the tank state
    /// was sent at — the NEAREST decoded sample, not an invented value
    /// between packets. This also makes the rehearsal exact: snapshots built
    /// at (millisecond-rounded) sample times resolve back to the same sample
    /// the field was set from.
    /// </summary>
    private sealed class YawLookup
    {
        private readonly YawSample[] _samples;
        private readonly double[] _ticks;

        internal YawLookup(YawSample[] samples)
        {
            _samples = samples;
            _ticks = new double[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                _ticks[index] = samples[index].ReplayTime.Ticks;
            }
        }

        internal bool IsEmpty => _samples.Length == 0;

        internal bool TryGetValueAt(TimeSpan replayTime, out double value)
        {
            double tick = replayTime.Ticks;
            int index = Array.BinarySearch(_ticks, tick);
            if (index >= 0)
            {
                value = _samples[index].YawRadians;
                return true;
            }

            int insertion = ~index;
            if (insertion == 0 || insertion >= _samples.Length)
            {
                // Before the first sample or past the last: no ground truth
                // exists there; a shift/dump outside the window must not
                // fabricate matches against the endpoint's constant value.
                value = 0;
                return false;
            }

            // Nearest sample; ties resolve to the EARLIER sample.
            YawSample left = _samples[insertion - 1];
            YawSample right = _samples[insertion];
            value = (tick - _ticks[insertion - 1]) <= (_ticks[insertion] - tick)
                ? left.YawRadians
                : right.YawRadians;
            return true;
        }
    }
}
