namespace WotBTreader.Core.Discovery;

/// <summary>
/// One trusted-reader dump of a memory region (the entity record) at a replay
/// clock time. <c>ReplayTime</c> is the decoded replay clock of the snapshot
/// (event-bound observation — the trusted reader labels each dump with the
/// replay time it was taken at). <c>Bytes</c> is the FULL region dump so any
/// field can be read exactly for a window; snapshots must be strictly
/// increasing in replay time.
/// </summary>
public sealed record RecordSnapshot(
    TimeSpan ReplayTime,
    byte[] Bytes);

/// <summary>
/// One time-bucketed change observation: between two consecutive snapshots at
/// <c>FromReplayTime</c> (exclusive) and <c>ToReplayTime</c> (inclusive), the
/// region's bytes went from <c>Before</c> to <c>After</c>. Both arrays are
/// full-region dumps so any 4-byte-aligned int32 field can be read exactly.
/// Damage events whose replay time falls in (From, To] belong to this window.
/// </summary>
public sealed record ByteChangeWindow(
    TimeSpan FromReplayTime,
    TimeSpan ToReplayTime,
    byte[] Before,
    byte[] After);

/// <summary>
/// A ranked damage-correlation candidate: a 4-byte-aligned int32 field whose
/// little-endian value move (drop for Decrement/HP, rise for
/// Increment/damage-dealt) matches the target entity's damage events in the
/// same replay-time windows per the correlation's <see cref="DamageMatchMode"/>
/// (Strict: |delta| == Σ damage; Lenient: |delta| ≥ Σ damage). A higher score
/// means more damage windows matched. <see cref="Flatness"/> is the fraction
/// of zero-damage (control) change windows in which the field was UNCHANGED
/// (1.0 when there are no control windows) — it separates HP (flat except when
/// hit) from monotonic drains or other decoys that drop in every window; the
/// ranking prefers score, then flatness, then precision, then offset.
/// <see cref="MatchedWindows"/> lists the per-window replay span and summed
/// damage for every matched window (the verdict contract's matched-window
/// list, replay times + deltas vs. the provider's events).
/// </summary>
public sealed record DamageCorrelationCandidate(
    int Offset,
    int Length,
    double Score,
    int MatchedDamageWindows,
    int TotalDamageWindows,
    int ChangedWindows,
    string Explanation,
    double Flatness = 1.0,
    int ControlWindows = 0,
    int ChangedControlWindows = 0,
    IReadOnlyList<MatchedDamageWindow>? MatchedWindows = null);

/// <summary>One damage window a candidate matched: its replay span and the
/// summed damage of the target's events inside it (the exact drop the
/// field's value matched).</summary>
public sealed record MatchedDamageWindow(
    TimeSpan FromReplayTime,
    TimeSpan ToReplayTime,
    long DamageSum);
