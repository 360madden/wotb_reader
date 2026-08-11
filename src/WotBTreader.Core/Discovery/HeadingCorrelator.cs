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
    string Explanation,
    double? BestLagSeconds = null,
    double? LagSpreadSeconds = null);

/// <summary>
/// Correlates the target entity's packet-derived yaw against the bucketed
/// change windows: a candidate is a 4-byte-aligned float32 field whose
/// little-endian value delta (wrapped to [-pi, pi]) equals the yaw delta
/// (also wrapped) between the window's snapshots. Pure and offline; the
/// memory side (trusted reader) is a separate approved-session step. Windows
/// whose expected |delta| exceeds the match tolerance are TURN windows
/// (score denominator); the rest are CONTROL windows (flatness denominator
/// — the yaw field must be unchanged there). The turn boundary is the match
/// tolerance itself: a window whose expected |delta| is at or below it can
/// never be verified (its observed delta reads as "unchanged" and is
/// skipped), so counting it in the score denominator would make a perfect
/// field score below 1.0. Windows whose From/To replay times fall outside
/// the yaw sample span have no ground truth and are excluded from both
/// denominators.
/// </summary>
public static class HeadingCorrelator
{
    /// <summary>Default match tolerance in radians (~2.9 degrees).</summary>
    public const double DefaultToleranceRadians = 0.05;

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
            if (Math.Abs(expected) > toleranceRadians)
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

    /// <summary>
    /// Value-match correlation with a bounded, optionally bidirectional,
    /// optionally per-dump lag search. Two findings drive this:
    /// (1) OD-RECOVERY-088 — the ring record applies decoded packet state
    /// with a ~1-5 s memory-apply delay, so a delta-over-window comparison
    /// between a before/after dump pair misses the change (the delta path's
    /// honest-negative in the 088 live run). This path instead matches each
    /// dump's raw float at the candidate offset against the packet yaw at
    /// (dump time - lag).
    /// (2) OD-RECOVERY-089 — the G2 replay-clock LABEL itself carries a
    /// per-session, per-dump-varying skew that is OPPOSITE in sign between
    /// replays (Oasis: memory LAGS the label by ~3-5 s; Dead Rail: memory
    /// LEADS the label by ~2-5 s). A one-directional shared lag therefore
    /// caps below 1.0 on Dead Rail even though +0x30 is byte-exact
    /// (56/56). When <paramref name="perDumpLag"/> is set, each dump
    /// independently picks its best lag in
    /// [-<paramref name="maxMemoryLeadSeconds"/>, +<paramref name="maxLagSeconds"/>]
    /// and the candidate reports the median lag plus the spread
    /// (<see cref="HeadingCorrelationCandidate.LagSpreadSeconds"/>) so the
    /// skew structure stays visible evidence, never silent per-dump fitting.
    /// Score = matched dumps / matchable dumps. Flatness = the fraction of
    /// stationary CONTROL dumps (packet yaw constant, |expected delta| &lt;=
    /// tolerance) that also match at their lag — a decoy that happens to
    /// track yaw during turns is separated by drifting in the stationary
    /// segments (controls are stationary, so the lag choice cannot hide
    /// drift). Additive: default behavior (lead 0, shared lag) is unchanged;
    /// the window-delta <see cref="Correlate"/> path is unchanged.
    /// </summary>
    public static IReadOnlyList<HeadingCorrelationCandidate> CorrelateWithLag(
        IReadOnlyList<RecordSnapshot> snapshots,
        IReadOnlyList<YawSample> yawSamples,
        long targetEntityId,
        double toleranceRadians = DefaultToleranceRadians,
        double maxLagSeconds = 10.0,
        double lagStepSeconds = 0.25,
        double maxMemoryLeadSeconds = 0.0,
        bool perDumpLag = false)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(yawSamples);
        if (!double.IsFinite(toleranceRadians) || toleranceRadians <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceRadians));
        }

        if (!double.IsFinite(maxLagSeconds) || maxLagSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLagSeconds));
        }

        if (!double.IsFinite(maxMemoryLeadSeconds) || maxMemoryLeadSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMemoryLeadSeconds));
        }

        if (!double.IsFinite(lagStepSeconds) || lagStepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lagStepSeconds));
        }

        if (snapshots.Count == 0)
        {
            return [];
        }

        int regionLength = snapshots[0].Bytes.Length;
        foreach (RecordSnapshot snapshot in snapshots)
        {
            if (snapshot.Bytes.Length != regionLength)
            {
                throw new ArgumentException(
                    "All snapshots must carry the same region length.",
                    nameof(snapshots));
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

        // Classify each dump as TURN or CONTROL by comparing the packet yaw
        // against the previous dump's packet yaw (the stationary segments
        // prove exactly constant yaw).
        List<(RecordSnapshot Snapshot, bool IsControl)> classified = [];
        RecordSnapshot? previous = null;
        foreach (RecordSnapshot snapshot in snapshots.OrderBy(s => s.ReplayTime))
        {
            if (previous is not null
                && lookup.TryGetValueAt(previous.ReplayTime, out double fromYaw)
                && lookup.TryGetValueAt(snapshot.ReplayTime, out double toYaw)
                && Math.Abs(WrapPi(toYaw - fromYaw)) <= toleranceRadians)
            {
                classified.Add((snapshot, IsControl: true));
            }
            else
            {
                classified.Add((snapshot, IsControl: false));
            }

            previous = snapshot;
        }

        int controlCount = classified.Count(item => item.IsControl);
        int maxOffset = regionLength - sizeof(float);
        double lagMin = -maxMemoryLeadSeconds;
        double lagMax = maxLagSeconds;
        List<HeadingCorrelationCandidate> candidates = [];
        for (int offset = 0; offset <= maxOffset; offset += sizeof(float))
        {
            double score;
            int matched;
            int matchable;
            int controlMatched;
            double bestLag;
            double? lagSpread;
            if (perDumpLag)
            {
                // Each dump picks its own best lag in the bounded range; the
                // reported median + spread make the skew structure visible.
                List<double> perDumpLags = [];
                matched = 0;
                matchable = 0;
                controlMatched = 0;
                foreach ((RecordSnapshot snapshot, bool isControl) in classified)
                {
                    double? bestError = null;
                    double bestDumpLag = 0;
                    bool foundGroundTruth = false;
                    for (double lag = lagMin; lag <= lagMax + 1e-9; lag += lagStepSeconds)
                    {
                        if (!lookup.TryGetValueAt(
                                snapshot.ReplayTime - TimeSpan.FromSeconds(lag),
                                out double expected))
                        {
                            continue;
                        }

                        foundGroundTruth = true;
                        float memory = BinaryPrimitives.ReadSingleLittleEndian(snapshot.Bytes.AsSpan(offset));
                        double error = Math.Abs(WrapPi(memory - expected));
                        if (bestError is null || error < bestError.Value)
                        {
                            bestError = error;
                            bestDumpLag = lag;
                        }
                    }

                    if (!foundGroundTruth)
                    {
                        continue;
                    }

                    matchable++;
                    if (bestError is not null && bestError.Value <= toleranceRadians)
                    {
                        matched++;
                        perDumpLags.Add(bestDumpLag);
                        if (isControl)
                        {
                            controlMatched++;
                        }
                    }
                }

                if (matchable == 0 || matched == 0)
                {
                    continue;
                }

                List<double> sortedLags = [.. perDumpLags.Order()];
                bestLag = sortedLags[sortedLags.Count / 2];
                lagSpread = sortedLags.Count > 1
                    ? sortedLags[^1] - sortedLags[0]
                    : 0.0;
                score = (double)matched / matchable;
            }
            else
            {
                // Search the SHARED lag maximizing the match count; the yaw
                // field must align at ONE lag across the whole session (the
                // memory-apply delay is a property of the read, not per-dump
                // noise). Bidirectional when a memory lead is allowed.
                int bestMatched = 0;
                int bestMatchable = 0;
                bestLag = 0;
                for (double lag = lagMin; lag <= lagMax + 1e-9; lag += lagStepSeconds)
                {
                    int currentMatched = 0;
                    int currentMatchable = 0;
                    foreach ((RecordSnapshot snapshot, _) in classified)
                    {
                        if (!lookup.TryGetValueAt(
                                snapshot.ReplayTime - TimeSpan.FromSeconds(lag),
                                out double expected))
                        {
                            continue;
                        }

                        currentMatchable++;
                        float memory = BinaryPrimitives.ReadSingleLittleEndian(snapshot.Bytes.AsSpan(offset));
                        if (Math.Abs(WrapPi(memory - expected)) <= toleranceRadians)
                        {
                            currentMatched++;
                        }
                    }

                    if (currentMatchable > 0
                        && (double)currentMatched / currentMatchable > (double)bestMatched / Math.Max(1, bestMatchable)
                        || (currentMatchable == bestMatchable && currentMatched > bestMatched))
                    {
                        bestMatched = currentMatched;
                        bestMatchable = currentMatchable;
                        bestLag = lag;
                    }
                }

                if (bestMatchable == 0 || bestMatched == 0)
                {
                    continue;
                }

                matched = bestMatched;
                matchable = bestMatchable;
                lagSpread = null;
                score = (double)matched / matchable;

                // Flatness over control dumps at the chosen lag: the field
                // must equal the (constant) packet yaw in every stationary
                // segment.
                controlMatched = 0;
                foreach ((RecordSnapshot snapshot, bool isControl) in classified)
                {
                    if (!isControl)
                    {
                        continue;
                    }

                    if (!lookup.TryGetValueAt(
                            snapshot.ReplayTime - TimeSpan.FromSeconds(bestLag),
                            out double expected))
                    {
                        continue;
                    }

                    float memory = BinaryPrimitives.ReadSingleLittleEndian(snapshot.Bytes.AsSpan(offset));
                    if (Math.Abs(WrapPi(memory - expected)) <= toleranceRadians)
                    {
                        controlMatched++;
                    }
                }
            }

            double flatness = controlCount == 0
                ? 1.0
                : (double)controlMatched / controlCount;
            string lagDescription = perDumpLag
                ? $"per-dump best lag median {bestLag:0.##}s spread {lagSpread:0.##}s "
                : $"shared lag {bestLag:0.##}s ";
            candidates.Add(new HeadingCorrelationCandidate(
                offset,
                score,
                matched,
                matchable,
                matchable - matched,
                flatness,
                controlCount,
                controlCount - controlMatched,
                null,
                $"float32 at +0x{offset:X}: yaw VALUE matched {matched}/{matchable} "
                + $"dumps at {lagDescription}(|wrapped value - yaw(t - lag)| <= "
                + $"{toleranceRadians:0.###} rad); flatness {flatness:0.##} "
                + $"({controlMatched}/{controlCount} stationary dumps unchanged)",
                bestLag,
                lagSpread));
        }

        return [.. candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Flatness)
            .ThenByDescending(candidate => candidate.BestLagSeconds)
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
