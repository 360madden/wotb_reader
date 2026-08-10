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
/// How a candidate field's value drop is matched against the summed damage of
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
/// windows: a candidate is a 4-byte-aligned int32 field whose little-endian
/// value drop in a window matches −(Σ damage for the target entity whose event
/// times fall in that window) per the <see cref="DamageMatchMode"/>. Pure and
/// offline; the memory side (trusted reader) is a separate approved-session
/// step. Strict requires the drop to equal the summed damage exactly;
/// Lenient accepts any drop at least as large (overkill killing blows,
/// multi-source under-sums). Events whose replay time falls outside the
/// observed window span are observation gaps and do not inflate the
/// denominator.
/// </summary>
public static class HpDamageCorrelator
{
    /// <summary>
    /// Ranks candidate HP fields for <paramref name="targetEntityId"/>. Only
    /// offsets that matched at least one damage window are returned, ordered by
    /// score (matched / damage windows) descending, then precision (matched /
    /// changed damage-windows) descending, then offset ascending.
    /// </summary>
    public static IReadOnlyList<DamageCorrelationCandidate> Correlate(
        IReadOnlyList<ByteChangeWindow> windows,
        IReadOnlyList<HpDamageEvent> damageEvents,
        long targetEntityId,
        DamageMatchMode matchMode = DamageMatchMode.Strict)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(damageEvents);

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
        Dictionary<ByteChangeWindow, long> damageByWindow = [];
        foreach (ByteChangeWindow window in windows)
        {
            long sum = 0;
            foreach (HpDamageEvent damageEvent in damageEvents)
            {
                if (damageEvent.EntityId != targetEntityId
                    || damageEvent.Damage is not int amount
                    || amount <= 0)
                {
                    continue;
                }

                if (damageEvent.ReplayTime > window.FromReplayTime
                    && damageEvent.ReplayTime <= window.ToReplayTime)
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

        List<DamageCorrelationCandidate> candidates = [];
        int maxOffset = regionLength - sizeof(int);
        for (int offset = 0; offset <= maxOffset; offset += sizeof(int))
        {
            int matched = 0;
            int changed = 0;
            foreach ((ByteChangeWindow window, long sum) in damageByWindow)
            {
                int before = BinaryPrimitives.ReadInt32LittleEndian(window.Before.AsSpan(offset));
                int after = BinaryPrimitives.ReadInt32LittleEndian(window.After.AsSpan(offset));
                long delta = (long)after - before;
                if (delta == 0)
                {
                    continue;
                }

                changed++;
                bool isMatch = matchMode == DamageMatchMode.Lenient
                    ? delta <= -sum
                    : delta == -sum;
                if (isMatch)
                {
                    matched++;
                }
            }

            if (matched == 0)
            {
                continue;
            }

            double score = (double)matched / damageByWindow.Count;
            string matchText = matchMode == DamageMatchMode.Lenient
                ? "drop >= -Σ damage"
                : "delta == -Σ damage";
            candidates.Add(new DamageCorrelationCandidate(
                offset,
                sizeof(int),
                score,
                matched,
                damageByWindow.Count,
                changed,
                $"int32 at +0x{offset:X}: value drop matched {matched}/{damageByWindow.Count} "
                + $"damage windows (precision {matched}/{changed}); {matchText}"));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => (double)candidate.MatchedDamageWindows / candidate.ChangedWindows)
            .ThenBy(candidate => candidate.Offset)
            .ToList();
    }
}
