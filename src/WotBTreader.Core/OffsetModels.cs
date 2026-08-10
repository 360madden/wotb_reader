namespace WotBTreader.Core;

/// <summary>
/// Confidence tier for a discovered memory offset.
/// Mirrors the <c>memory-offsets/schema.json</c> confidence levels.
/// </summary>
public enum OffsetConfidence
{
    /// <summary>No offsets discovered — placeholder table.</summary>
    None,

    /// <summary>Scanner found candidates, unverified.</summary>
    Low,

    /// <summary>Scanner found 1–3 candidates, matches game behaviour in one session.</summary>
    Medium,

    /// <summary>Verified across multiple sessions and game restarts.</summary>
    High,
}

/// <summary>The C++ type of a memory field at the discovered offset.</summary>
public enum OffsetFieldType
{
    Unknown,
    Int32Field,
    FloatField,
    DoubleField,
}

/// <summary>Who (or what tool) produced this offset claim and with what method.</summary>
public enum OffsetProvenanceKind
{
    Unknown,
    StaticAnalysis,
    DynamicScan,
    GameHarness,
    ManualVerification,
}

/// <summary>Per-field provenance evidence for a single offset claim.</summary>
public sealed record OffsetFieldEvidence(
    OffsetProvenanceKind ProvenanceKind,
    string SourceTool,
    string? Notes);

/// <summary>Validation status for a single field in an offset table.</summary>
public enum OffsetFieldStatus
{
    /// <summary>Offset is zero — not yet discovered.</summary>
    Unknown,

    /// <summary>Candidate offset found but not yet verified across sessions.</summary>
    Candidate,

    /// <summary>
    /// Offset verified via static evidence, dynamic observation across at least
    /// two independent process launches, and GameHarness invariant checks.
    /// </summary>
    Verified,

    /// <summary>Offset was previously verified but is now stale or contradicted.</summary>
    Stale,
}

/// <summary>One discovered field in a game-version-specific offset table.</summary>
public sealed record OffsetField(
    string Name,
    OffsetFieldType FieldType,
    long Offset,
    OffsetFieldStatus Status,
    OffsetConfidence Confidence,
    IReadOnlyList<OffsetFieldEvidence> Evidence);

/// <summary>Kind of one hop in a published pointer chain.</summary>
public enum OffsetChainHopKind
{
    /// <summary>First hop: module base + RVA yields the root pointer.</summary>
    RootRva,

    /// <summary>Intermediate hop: dereference a pointer at the current address + offset.</summary>
    MemberOffset,

    /// <summary>Final hop: add a fixed record offset to the final pointer (no dereference).</summary>
    RecordOffset,

    /// <summary>
    /// Ring/array-record hop: dereference the ring pointer stored at
    /// (object + <see cref="OffsetChainHop.Value"/>), read the current index
    /// Int32 at (object + <see cref="OffsetChainHop.IndexOffset"/>), and move
    /// to <c>ring + index * <see cref="OffsetChainHop.Stride"/></c>.
    /// Requires <see cref="OffsetChainHop.IndexOffset"/> and
    /// <see cref="OffsetChainHop.Stride"/>.
    /// </summary>
    RingIndex,
}

/// <summary>One hop in a published pointer chain (see <c>memory-offsets/schema.json</c>).</summary>
public sealed record OffsetChainHop(
    OffsetChainHopKind Kind,
    int Value,
    string? Note,
    int? IndexOffset = null,
    int? Stride = null);

/// <summary>
/// Immutable offset table for one specific game version and executable hash.
/// Loaded from a <c>memory-offsets/&lt;version&gt;.json</c> file and validated
/// against the observed executable identity before use.
/// </summary>
/// <remarks>
/// <para><c>Chains</c> carries the published pointer chains keyed by field name.
/// Chained fields keep <c>Offset</c> 0 by design — the legacy observation path
/// computes <c>moduleBase + offset</c> and cannot represent a chain — so chain
/// resolution is a separate capability (<see cref="Discovery.OffsetChainWalker"/>).</para>
/// </remarks>
public sealed record OffsetTable(
    int SchemaVersion,
    string GameVersion,
    string ExecutableSha256,
    DateTimeOffset? DiscoveredAtUtc,
    OffsetConfidence Confidence,
    string? Notes,
    IReadOnlyList<OffsetField> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<OffsetChainHop>>? Chains = null);
