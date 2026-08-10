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
/// A ranked HP-correlation candidate: a 4-byte-aligned int32 field whose
/// little-endian value drops match the target entity's damage events in the
/// same replay-time windows per the correlation's <see cref="DamageMatchMode"/>
/// (Strict: delta == −Σ damage; Lenient: drop ≥ Σ damage). A higher score
/// means more damage windows matched; the precision tiebreak prefers fields
/// that changed only when damage actually landed.
/// </summary>
public sealed record DamageCorrelationCandidate(
    int Offset,
    int Length,
    double Score,
    int MatchedDamageWindows,
    int TotalDamageWindows,
    int ChangedWindows,
    string Explanation);
