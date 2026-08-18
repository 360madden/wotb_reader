using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Buckets consecutive trusted-reader region dumps into time-labeled change
/// windows. Pure and offline: no process access. Snapshots must be strictly
/// increasing in replay time (the reader labels each dump with the decoded
/// replay clock); a non-increasing pair is a contract violation and fails
/// closed. Unchanged pairs produce no window (nothing to correlate).
/// </summary>
public static class RecordChangeBucketer
{
    /// <summary>
    /// Returns one <see cref="ByteChangeWindow"/> per consecutive snapshot pair
    /// whose region bytes differ. Fewer than two snapshots yields no windows.
    /// </summary>
    public static IReadOnlyList<ByteChangeWindow> Bucket(
        IReadOnlyList<RecordSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count < 2)
        {
            return [];
        }

        List<ByteChangeWindow> windows = [];
        RecordSnapshot? previous = null;
        foreach (RecordSnapshot snapshot in snapshots)
        {
            if (previous is null)
            {
                previous = snapshot;
                continue;
            }

            if (snapshot.ReplayTime <= previous.ReplayTime)
            {
                throw new ArgumentException(
                    "Record snapshots must be strictly increasing in replay time.",
                    nameof(snapshots));
            }

            if (!snapshot.Bytes.AsSpan().SequenceEqual(previous.Bytes))
            {
                windows.Add(new ByteChangeWindow(
                    previous.ReplayTime,
                    snapshot.ReplayTime,
                    previous.Bytes,
                    snapshot.Bytes));
            }

            previous = snapshot;
        }

        return windows;
    }
}

/// <summary>
/// Which direction a candidate field moves when the target's damage events
/// fire. HP drops by the window's damage sum (Decrement); a scoreboard
/// damage-dealt counter increments by it (Increment). The correlator matches
/// the field's little-endian int32 delta against the summed damage with the
/// sign of this direction.
/// </summary>
public enum DamageCorrelationDirection
{
    /// <summary>The field decreases by the summed damage (e.g. HP).</summary>
    Decrement,

    /// <summary>The field increases by the summed damage (e.g. scoreboard damage dealt).</summary>
    Increment,
}

/// <summary>
/// How a candidate field's value move is matched against the summed damage of
/// a window.
/// </summary>
public enum DamageMatchMode
{
    /// <summary>
    /// Exact equality: drop == −Σ damage. The purity check — no overkill, no
    /// under-sum. Misses the destroying hit when the recorded damage exceeds
    /// the remaining HP.
    /// </summary>
    Strict,

    /// <summary>
    /// Drop &gt;= Σ damage: the field lost at least as much as the window's
    /// damage. Matches the destroying hit's overkill and multi-source
    /// under-sums; still rejects upward moves and small coincidental drops.
    /// </summary>
    Lenient,
}

/// <summary>
/// Correlates the target entity's damage events against the bucketed change
/// windows: a candidate is a 4-byte-aligned int32 field (plus 2-byte-aligned
/// int16 fields when <c>includeInt16Candidates</c> is set — the static
/// playerHP evidence pins current health as int16 at [entity+0xB8]) whose
/// little-endian value move in a window matches ±(Σ damage for the target
/// entity whose event times fall in that window) per the
/// <see cref="DamageMatchMode"/> and
/// <see cref="DamageCorrelationDirection"/>. Pure and offline; the memory
/// side (trusted reader) is a separate approved-session step. Decrement
/// direction (HP): Strict requires the drop to equal the summed damage
/// exactly; Lenient accepts any drop at least as large (overkill killing
/// blows, multi-source under-sums). Increment direction (damage dealt):
/// Strict requires the rise to equal the summed damage exactly; Lenient
/// accepts any rise at least as large (a multi-hit window where the counter
/// also absorbed an unobserved sub-event). Events whose replay time falls
/// outside the observed window span are observation gaps and do not inflate
/// the denominator.
/// </summary>
public static class HpDamageCorrelator
{
    /// <summary>
    /// Ranks candidate fields for <paramref name="targetEntityId"/>. In the
    /// Decrement direction (the default, HP) the target id is matched against
    /// each event's victim <c>EntityId</c>; in the Increment direction
    /// (damage dealt) it is matched against the event's
    /// <c>AttackerEntityId</c> — the events whose damage the scoreboard
    /// counter accumulates. Only offsets that matched at least one damage
    /// window are returned, ordered by score (matched / damage windows)
    /// descending, then precision (matched / changed damage-windows)
    /// descending, then offset ascending.
    ///
    /// The attribution window is one-directional by default: an event
    /// attributes to a change window when its decoded replay time falls in
    /// (From - <paramref name="eventLagToleranceSeconds"/>, To]. That models
    /// a memory clock that LAGS the decoded clock (the health write lands a
    /// few seconds after the decoded damage time — OD-RECOVERY-087). Some
    /// replays show the opposite skew (medvedkovo memory LEADS the decoded
    /// clock by ~2.5 s — OD-RECOVERY-089 measured it for yaw): the write
    /// lands BEFORE the decoded event time, so the event's decoded time
    /// postdates the window that contains its write and the one-directional
    /// window cannot see it. <paramref name="eventLagLeadSeconds"/> (default
    /// 0 = unchanged) extends the window FORWARD to (From - lag, To + lead],
    /// admitting those lead-side events so the drop can match exactly.
    /// </summary>
    /// <summary>
    /// Whether to also score 2-byte-aligned int16 candidates. The default
    /// (false) scans only 4-byte-aligned int32 fields. Static evidence for
    /// the 11.19.0.10 build (VerifyPlayerHpChain, 2026-08-11) pins the
    /// vehicle current-health field as a SIGNED int16 at <c>[entity+0xB8]</c>
    /// (alive byte at +0xBA, healing int16 at +0x11E) — the entity base's
    /// own record, not the tank record at <c>[entity+0x3C]</c> — so an HP
    /// session must scan int16 candidates or it will never find the field.
    /// </summary>
    public static IReadOnlyList<DamageCorrelationCandidate> Correlate(
        IReadOnlyList<ByteChangeWindow> windows,
        IReadOnlyList<HpDamageEvent> damageEvents,
        long targetEntityId,
        DamageMatchMode matchMode = DamageMatchMode.Strict,
        DamageCorrelationDirection direction = DamageCorrelationDirection.Decrement,
        bool includeInt16Candidates = false,
        double eventLagToleranceSeconds = 0,
        double eventLagLeadSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(damageEvents);
        if (eventLagToleranceSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventLagToleranceSeconds),
                "The event-lag tolerance must be >= 0.");
        }

        if (eventLagLeadSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventLagLeadSeconds),
                "The event-lag lead must be >= 0.");
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

        // Sum the target entity's damage per window (event time in (From, To]).
        // Decrement direction keys on the event's victim entity id (HP);
        // Increment keys on the attacker entity id (damage dealt).
        Dictionary<ByteChangeWindow, long> damageByWindow = [];
        foreach (ByteChangeWindow window in windows)
        {
            long sum = 0;
            foreach (HpDamageEvent damageEvent in damageEvents)
            {
                bool belongsToTarget = direction == DamageCorrelationDirection.Increment
                    ? damageEvent.AttackerEntityId == targetEntityId
                    : damageEvent.EntityId == targetEntityId;
                if (!belongsToTarget
                    || damageEvent.Damage is not int amount
                    || amount <= 0)
                {
                    continue;
                }

                // Attribution window: (From - lag, To + lead]. Live memory
                // reads (OD-RECOVERY-087) measured the game applying decoded
                // damage events to the health field with a VARIABLE lag of
                // ~1-10 s (the event's decoded packet time precedes the
                // state-sync write by a few seconds, and the lag varies per
                // event), so the before/after dump pair around the decoded
                // event time cannot bracket the memory write. A bounded lag
                // tolerance (default 0 = exact, unchanged) lets the event
                // attribute to the change window that actually contains its
                // memory write. The bounded LEAD side (default 0 = unchanged)
                // admits events whose decoded time POSTDATES the window — the
                // memory clock can also lead the decoded clock (medvedkovo
                // leads by ~2.5 s, OD-RECOVERY-089), so the write for such an
                // event lands in an EARLIER window than its decoded time.
                if (damageEvent.ReplayTime
                        > window.FromReplayTime - TimeSpan.FromSeconds(eventLagToleranceSeconds)
                    && damageEvent.ReplayTime
                        <= window.ToReplayTime + TimeSpan.FromSeconds(eventLagLeadSeconds))
                {
                    sum += amount;
                }
            }

            if (sum > 0)
            {
                damageByWindow[window] = sum;
            }
        }

        if (damageByWindow.Count == 0)
        {
            return [];
        }

        // Lag path: per-window candidate events (t in (From - lag, To +
        // lead]), in time order, for the greedy subset-sum attribution
        // below. The lagged
        // memory write can land in the window AFTER the event's own (the
        // write crosses a dump boundary when lag > dump gap), so a pure
        // time-window attribution over-counts or mis-attributes; matching
        // each drop against the SUM of a subset of its candidate events
        // (each event consumed at most once) attributes the true field's
        // drops exactly (OD-RECOVERY-087: every HP drop equals its damage
        // sum while the spurious pointer fields change in every window).
        List<ByteChangeWindow> orderedWindows = windows
            .OrderBy(window => window.FromReplayTime)
            .ToList();
        Dictionary<ByteChangeWindow, List<int>> candidatesByWindow = [];
        if (eventLagToleranceSeconds > 0)
        {
            for (int eventIndex = 0; eventIndex < damageEvents.Count; eventIndex++)
            {
                HpDamageEvent damageEvent = damageEvents[eventIndex];
                bool belongsToTarget = direction == DamageCorrelationDirection.Increment
                    ? damageEvent.AttackerEntityId == targetEntityId
                    : damageEvent.EntityId == targetEntityId;
                if (!belongsToTarget
                    || damageEvent.Damage is not int
                    || damageEvent.Damage <= 0)
                {
                    continue;
                }

                foreach (ByteChangeWindow window in orderedWindows)
                {
                    if (damageEvent.ReplayTime
                            > window.FromReplayTime - TimeSpan.FromSeconds(eventLagToleranceSeconds)
                        && damageEvent.ReplayTime
                            <= window.ToReplayTime + TimeSpan.FromSeconds(eventLagLeadSeconds))
                    {
                        if (!candidatesByWindow.TryGetValue(window, out List<int>? indices))
                        {
                            indices = [];
                            candidatesByWindow[window] = indices;
                        }

                        indices.Add(eventIndex);
                    }
                }
            }
        }

        // Control windows: change windows in which the target took NO damage.
        // HP is flat there (no healing in WoTB); a monotonic drain or other
        // decoy changes in them — flatness separates the two. The bucketer
        // only emits windows for pairs whose bytes differ, so a "control
        // change" means OTHER bytes changed while this field stayed put.
        List<ByteChangeWindow> controlWindows = windows
            .Where(window => !damageByWindow.ContainsKey(window))
            .ToList();

        List<DamageCorrelationCandidate> candidates = [];

        // Int32 pass: every 4-byte-aligned offset (the historic candidate
        // grid). Int16 pass (opt-in): every 2-byte-aligned offset — the
        // static playerHP evidence pins current health as int16 at
        // [entity+0xB8], which an int32-only scan would fold into garbage
        // (health + alive byte + padding) or miss entirely.
        var passes = new List<(int Width, int Stride)>
        {
            (sizeof(int), sizeof(int)),
        };
        if (includeInt16Candidates)
        {
            passes.Add((sizeof(short), sizeof(short)));
        }

        foreach ((int width, int stride) in passes)
        {
            int maxOffset = regionLength - width;
            for (int offset = 0; offset <= maxOffset; offset += stride)
            {
                int matched = 0;
                int changed = 0;
                int damageWindowsWithEvents = 0;
                List<MatchedDamageWindow> matchedWindows = [];
                bool[] consumed = eventLagToleranceSeconds > 0
                    ? new bool[damageEvents.Count]
                    : [];
                IEnumerable<ByteChangeWindow> loopWindows = eventLagToleranceSeconds > 0
                    ? orderedWindows
                    : damageByWindow.Keys;
                foreach (ByteChangeWindow window in loopWindows)
                {
                    long before = width == sizeof(int)
                        ? BinaryPrimitives.ReadInt32LittleEndian(window.Before.AsSpan(offset))
                        : BinaryPrimitives.ReadInt16LittleEndian(window.Before.AsSpan(offset));
                    long after = width == sizeof(int)
                        ? BinaryPrimitives.ReadInt32LittleEndian(window.After.AsSpan(offset))
                        : BinaryPrimitives.ReadInt16LittleEndian(window.After.AsSpan(offset));
                    long delta = after - before;
                    if (delta == 0)
                    {
                        continue;
                    }

                    changed++;
                    if (eventLagToleranceSeconds > 0)
                    {
                        // Subset-sum attribution: the drop must equal (Strict)
                        // or cover (Lenient) the sum of SOME subset of the
                        // window's candidate events, with each event consumed
                        // at most once across windows. This is what makes the
                        // variable-lag memory writes match: the true field's
                        // drop lands in the window containing the write, and
                        // the subset selects exactly the events that fired
                        // there (a multi-hit window matches its combined sum;
                        // a lagged event that crossed a dump boundary still
                        // matches because the tolerance admits it).
                        if (!candidatesByWindow.TryGetValue(window, out List<int>? indices)
                            || indices.Count == 0)
                        {
                            // A change with no candidate events is a control
                            // change — the flatness pass scores it.
                            continue;
                        }

                        List<int> available = indices
                            .Where(index => !consumed[index])
                            .ToList();
                        if (available.Count == 0)
                        {
                            continue;
                        }

                        damageWindowsWithEvents++;
                        long target = Math.Abs(delta);
                        int[] amounts = available
                            .Select(index => (int)damageEvents[index].Damage!)
                            .ToArray();
                        (long subsetSum, int subsetMask) =
                            LargestSubsetSumAtMost(amounts, target);
                        if (subsetSum == 0)
                        {
                            continue;
                        }

                        bool isMatch = matchMode == DamageMatchMode.Lenient
                            || subsetSum == target;
                        if (!isMatch)
                        {
                            continue;
                        }

                        for (int bit = 0; bit < available.Count; bit++)
                        {
                            if ((subsetMask & (1 << bit)) != 0)
                            {
                                consumed[available[bit]] = true;
                            }
                        }

                        matched++;
                        matchedWindows.Add(new MatchedDamageWindow(
                            window.FromReplayTime,
                            window.ToReplayTime,
                            subsetSum));
                        continue;
                    }

                    long sum = damageByWindow[window];
                    bool exactMatch = matchMode == DamageMatchMode.Lenient
                        ? (direction == DamageCorrelationDirection.Increment
                            ? delta >= sum
                            : delta <= -sum)
                        : (direction == DamageCorrelationDirection.Increment
                            ? delta == sum
                            : delta == -sum);
                    if (exactMatch)
                    {
                        matched++;
                        matchedWindows.Add(new MatchedDamageWindow(
                            window.FromReplayTime,
                            window.ToReplayTime,
                            sum));
                    }
                }

                if (matched == 0)
                {
                    continue;
                }

                int controlChanged = 0;
                foreach (ByteChangeWindow control in controlWindows)
                {
                    long before = width == sizeof(int)
                        ? BinaryPrimitives.ReadInt32LittleEndian(control.Before.AsSpan(offset))
                        : BinaryPrimitives.ReadInt16LittleEndian(control.Before.AsSpan(offset));
                    long after = width == sizeof(int)
                        ? BinaryPrimitives.ReadInt32LittleEndian(control.After.AsSpan(offset))
                        : BinaryPrimitives.ReadInt16LittleEndian(control.After.AsSpan(offset));
                    if (before != after)
                    {
                        controlChanged++;
                    }
                }

                // Lag path: the denominator is the candidate's changed
                // windows that carry candidate events (the true field's drops
                // each land in exactly one such window, so score 1.0 is
                // reachable; spurious pointer fields change in many more
                // windows than they can subset-match). Exact path: the
                // field-agnostic event-bearing windows, unchanged.
                int denominator = eventLagToleranceSeconds > 0
                    ? damageWindowsWithEvents
                    : damageByWindow.Count;
                double score = denominator == 0 ? 0.0 : (double)matched / denominator;
                double flatness = controlWindows.Count == 0
                    ? 1.0
                    : (double)(controlWindows.Count - controlChanged) / controlWindows.Count;
                string matchText = matchMode == DamageMatchMode.Lenient
                    ? (direction == DamageCorrelationDirection.Increment
                        ? "rise >= +Σ damage"
                        : "drop >= -Σ damage")
                    : (direction == DamageCorrelationDirection.Increment
                        ? "delta == +Σ damage"
                        : "delta == -Σ damage");
                string moveWord = direction == DamageCorrelationDirection.Increment ? "rise" : "drop";
                string typeWord = width == sizeof(int) ? "int32" : "int16";
                candidates.Add(new DamageCorrelationCandidate(
                    offset,
                    width,
                    score,
                    matched,
                    denominator,
                    changed,
                    $"{typeWord} at +0x{offset:X}: value {moveWord} matched {matched}/{denominator} "
                    + $"damage windows (precision {matched}/{changed}); flatness "
                    + $"{flatness:0.##} ({controlWindows.Count - controlChanged}/"
                    + $"{controlWindows.Count} control windows unchanged); {matchText}",
                    flatness,
                    controlWindows.Count,
                    controlChanged,
                    matchedWindows));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Flatness)
            .ThenByDescending(candidate => (double)candidate.MatchedDamageWindows / candidate.ChangedWindows)
            .ThenBy(candidate => candidate.Offset)
            .ToList();
    }

    /// <summary>
    /// Returns the largest subset sum of <paramref name="amounts"/> that is
    /// at most <paramref name="target"/>, plus the subset bitmask (bit i set
    /// = amounts[i] chosen). Zero sum means no affordable positive subset.
    /// Small n (events per window are few) via bitmask enumeration; beyond 20
    /// items the sum of all is returned as the pragmatic upper bound.
    /// </summary>
    private static (long Sum, int Mask) LargestSubsetSumAtMost(
        int[] amounts,
        long target)
    {
        int n = amounts.Length;
        if (n == 0 || target <= 0)
        {
            return (0, 0);
        }

        if (n > 20)
        {
            long total = 0;
            foreach (int amount in amounts)
            {
                total += amount;
            }

            return (Math.Min(total, target), (1 << n) - 1);
        }

        long best = 0;
        int bestMask = 0;
        int full = 1 << n;
        for (int mask = 1; mask < full; mask++)
        {
            long sum = 0;
            for (int bit = 0; bit < n; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    sum += amounts[bit];
                    if (sum > target)
                    {
                        sum = long.MaxValue;
                        break;
                    }
                }
            }

            if (sum <= target && sum > best)
            {
                best = sum;
                bestMask = mask;
            }
        }

        return (best, bestMask);
    }
}
