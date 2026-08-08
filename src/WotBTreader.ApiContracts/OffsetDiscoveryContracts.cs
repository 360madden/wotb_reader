using System.Text.Json.Serialization;

namespace WotBTreader.ApiContracts;

/// <summary>
/// Request to scan the game process memory for a specific value and discover
/// its offset. POST /api/v1/game/discover.
/// </summary>
public sealed record OffsetDiscoveryRequest
{
    /// <summary>Which field we're trying to discover (e.g. "playerPositionX").</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>The C++ type: Float, Int32, or Double.</summary>
    public string FieldType { get; init; } = "Float";

    /// <summary>
    /// The expected value to search for. For floats/int32s, use the raw bytes
    /// as a hex string (e.g. "0000803F" for 1.0f little-endian).
    /// </summary>
    public string ExpectedValueHex { get; init; } = string.Empty;

    /// <summary>
    /// Optional per-byte tolerance mask as hex. Zero bytes require exact
    /// match; non-zero bytes are wildcards (any value matches).
    /// </summary>
    public string? ToleranceMaskHex { get; init; }

    /// <summary>
    /// Optional numeric tolerance for a Float scan. Unlike a byte mask, this
    /// compares decoded single-precision values and preserves exponent bits.
    /// Must be finite and non-negative.
    /// </summary>
    public float? FloatTolerance { get; init; }

    /// <summary>Maximum number of candidates to return (1–10000, default 500).</summary>
    public int MaxCandidates { get; init; } = 500;

    /// <summary>Minimum region size in bytes to scan (default 4096).</summary>
    public long MinRegionSize { get; init; } = 4096;

    /// <summary>Address alignment: 1, 2, 4, or 8.</summary>
    public int Alignment { get; init; } = 1;

    /// <summary>Whether image mappings are included in addition to private/mapped regions.</summary>
    public bool IncludeImageRegions { get; init; }

    /// <summary>
    /// When true with IncludeImageRegions, scan only MEM_IMAGE regions (exclude private/mapped).
    /// Ignored when IncludeImageRegions is false.
    /// </summary>
    public bool ImageRegionsOnly { get; init; }

    /// <summary>Whether working-set page classification is requested.</summary>
    public bool IncludeWorkingSetClassification { get; init; }
}

/// <summary>One candidate address found by the offset discovery scanner.</summary>
public sealed record OffsetDiscoveryCandidate
{
    /// <summary>Absolute virtual address in the process (hex).</summary>
    public string AbsoluteAddress { get; init; } = "0x0";

    /// <summary>Arithmetic displacement from the supplied scan base (hex); this is not a module RVA without proven main-image ownership.</summary>
    public string BaseDisplacement { get; init; } = "0x0";

    /// <summary>Arithmetic displacement from the supplied scan base as a decimal integer.</summary>
    public long BaseDisplacementDecimal { get; init; }

    /// <summary>Compatibility alias for clients using the former field name.</summary>
    [Obsolete("Use BaseDisplacement; this alias is retained for wire compatibility.")]
    [JsonPropertyName("relativeOffset")]
    public string RelativeOffset => BaseDisplacement;

    /// <summary>Compatibility alias for clients using the former field name.</summary>
    [Obsolete("Use BaseDisplacementDecimal; this alias is retained for wire compatibility.")]
    [JsonPropertyName("relativeOffsetDecimal")]
    public long RelativeOffsetDecimal => BaseDisplacementDecimal;

    /// <summary>The raw value at the address as a hex string.</summary>
    public string ObservedValueHex { get; init; } = string.Empty;

    /// <summary>Human-readable value summary.</summary>
    public string ValueSummary { get; init; } = string.Empty;

    /// <summary>How the address should be interpreted by evidence tooling.</summary>
    public string AddressKind { get; init; } = "absolute";

    /// <summary>Whether working-set evidence indicates a private page with COW-compatible protection; this is not proof that a COW event occurred.</summary>
    public bool IsCopyOnWrite { get; init; }
}

/// <summary>Results of one memory scan pass for offset discovery.</summary>
public sealed record OffsetDiscoveryResponse
{
    /// <summary>UTC timestamp when the scan completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Base address of the main module at scan time (hex).</summary>
    public string BaseAddress { get; init; } = "0x0";

    /// <summary>Count of memory regions scanned.</summary>
    public int RegionsScanned { get; init; }

    /// <summary>Approximate bytes scanned.</summary>
    public long BytesScanned { get; init; }

    /// <summary>Total matches before candidate cap, or 0 if all returned.</summary>
    public int TotalMatchesBeforeTruncation { get; init; }

    /// <summary>The top candidates, sorted by ascending address.</summary>
    public List<OffsetDiscoveryCandidate> Candidates { get; init; } = [];

    /// <summary>Target process architecture measured by the scanner.</summary>
    public string TargetArchitecture { get; init; } = "unknown";

    /// <summary>Main executable name captured from the authorized process identity; module membership is not inferred from this field.</summary>
    public string ModuleName { get; init; } = "unknown";

    /// <summary>Measured main module image size, when available; zero means unavailable.</summary>
    public long ModuleSize { get; init; }

    /// <summary>Alignment used by the scan.</summary>
    public int Alignment { get; init; } = 1;

    /// <summary>Whether candidate output was capped.</summary>
    public bool Truncated { get; init; }

    /// <summary>value, aob, or neighborhood.</summary>
    public string ScanKind { get; init; } = "value";
}

/// <summary>
/// Request for a bounded, single-root module pointer-chain evidence probe.
/// Each configured offset is added to the current address before the pointer
/// stored at that address is read; the read pointer becomes the next address.
/// </summary>
public sealed record PointerChainDiscoveryRequest
{
    public long RootRelativeOffset { get; init; }
    public List<long> PointerOffsets { get; init; } = [];
    public int MaxDepth { get; init; } = 4;
}

/// <summary>One bounded pointer-chain evidence result.</summary>
public sealed record PointerChainDiscoveryCandidate
{
    public string RootAddress { get; init; } = "0x0";
    public string FinalAddress { get; init; } = "0x0";
    public List<string> TraversedAddresses { get; init; } = [];
    public string AddressKind { get; init; } = "pointer-chain";
}

/// <summary>Response from a bounded, single-root pointer-chain evidence probe.</summary>
public sealed record PointerChainDiscoveryResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public List<PointerChainDiscoveryCandidate> Candidates { get; init; } = [];
    public int RejectedChains { get; init; }
}

/// <summary>
/// Request to create a bounded filtered memory snapshot.
/// <para>
/// <see cref="MaxAddress"/> is an exclusive upper bound when nonzero;
/// zero means no explicit upper bound. Therefore <see cref="MinAddress"/>
/// and <see cref="MaxAddress"/> must not be equal when a bounded range is
/// requested.
/// </para>
/// </summary>
public sealed record OffsetSnapshotRequest
{
    /// <summary>
    /// Hard retained-byte ceiling for a snapshot, matching the scanner engine.
    /// Requests above this ceiling are rejected at validation; the budget can
    /// only ever shrink a scan, never widen it.
    /// </summary>
    public const long MaximumSnapshotBytes = 512L * 1024 * 1024;

    public int ValueSize { get; init; } = 4;
    public float? FloatMin { get; init; }
    public float? FloatMax { get; init; }
    public int? IntMin { get; init; }
    public int? IntMax { get; init; }
    public long? LongMin { get; init; }
    public long? LongMax { get; init; }
    public ulong? UIntMin { get; init; }
    public ulong? UIntMax { get; init; }
    public long MinAddress { get; init; }
    /// <summary>Exclusive upper address; zero means the supported user-space limit.</summary>
    public long MaxAddress { get; init; }
    /// <summary>
    /// Explicit retained-byte budget for the snapshot. Zero means the engine
    /// ceiling (512 MiB). Values above the ceiling are rejected. A bounded
    /// budget lets private/mapped campaigns cap retained readable memory
    /// without selecting process-specific address windows.
    /// </summary>
    public long MaxBytes { get; init; }
    public string ValueKind { get; init; } = "Int32";
    public int Alignment { get; init; } = 1;
    public bool IncludeImageRegions { get; init; }

    /// <summary>
    /// When true with IncludeImageRegions, scan only MEM_IMAGE regions (exclude private/mapped).
    /// Ignored when IncludeImageRegions is false.
    /// </summary>
    public bool ImageRegionsOnly { get; init; }
}

/// <summary>Request to compare a snapshot against current memory.</summary>
public sealed record OffsetCompareRequest
{
    public string CompareMode { get; init; } = "changed";
    public int MaxCandidates { get; init; } = 100;
    public bool RollingBaseline { get; init; }

    /// <summary>
    /// Required with <see cref="CompareMode"/> = "delta" or "exact": the
    /// expected numeric value. For "delta" this is the expected change between
    /// the snapshot and current memory (e.g. a replay-derived position or speed
    /// delta); for "exact" it is the absolute value the current memory must
    /// match (e.g. the decoded replay clock at a paused frame). Must be finite.
    /// </summary>
    public double? DeltaTarget { get; init; }

    /// <summary>
    /// Required with <see cref="CompareMode"/> = "delta" or "exact": how close
    /// the observed value/change must be to <see cref="DeltaTarget"/>. Must be
    /// finite and non-negative.
    /// </summary>
    public double? DeltaTolerance { get; init; }
}

/// <summary>Request to scan a bounded memory window around a reference offset.</summary>
public sealed record OffsetNeighborhoodRequest
{
    public long ReferenceOffset { get; init; }
    public int WindowSize { get; init; } = 512;
    public bool IncludeFloat { get; init; } = true;
    public bool IncludeInt32 { get; init; } = true;
    public bool IncludeDouble { get; init; }
    public float? FloatMin { get; init; }
    public float? FloatMax { get; init; }
    public int? IntMin { get; init; }
    public int? IntMax { get; init; }
    public bool IncludeWorkingSetClassification { get; init; }
}

/// <summary>Response containing the identifier of a retained snapshot session.</summary>
public sealed record OffsetSnapshotResponse
{
    public string SessionId { get; init; } = string.Empty;
}

/// <summary>Response from a snapshot comparison.</summary>
public sealed record OffsetCompareResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int PreviousCount { get; init; }
    public int CurrentCount { get; init; }
    public int ChangedCount { get; init; }
    public int UnchangedCount { get; init; }
    public int IncreasedCount { get; init; }
    public int DecreasedCount { get; init; }
    public bool Truncated { get; init; }
    public bool ComparedAgainstRollingBaseline { get; init; }
    public int RetainedCount { get; init; }
    public List<OffsetDiscoveryCandidate> Candidates { get; init; } = [];
}

/// <summary>Response for a discarded snapshot session.</summary>
public sealed record OffsetDiscardResponse
{
    public string Discarded { get; init; } = string.Empty;
}

/// <summary>
/// Request to re-read a fixed set of staged absolute addresses (the
/// replay-guided correlation monitor primitive). Each address is read as a
/// single <see cref="ValueSize"/>-byte value of <see cref="ValueKind"/>.
/// </summary>
public sealed record OffsetReadRequest
{
    /// <summary>Absolute addresses as hex strings ("0x7FFA..." or plain hex).</summary>
    public List<string> Addresses { get; init; } = [];

    /// <summary>Float, Double, Int32, UInt32, Int64, or UInt64.</summary>
    public string ValueKind { get; init; } = "Float";

    /// <summary>Value width in bytes; must match the kind (4 or 8).</summary>
    public int ValueSize { get; init; } = 4;
}

/// <summary>One per-address read result.</summary>
public sealed record OffsetReadItem
{
    public string AbsoluteAddress { get; init; } = "0x0";
    public bool ReadOk { get; init; }
    public string ObservedValueHex { get; init; } = string.Empty;
    public string ValueSummary { get; init; } = string.Empty;
}

/// <summary>Result of re-reading a staged address set.</summary>
public sealed record OffsetReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int RequestedCount { get; init; }
    public int ReadCount { get; init; }
    public List<OffsetReadItem> Reads { get; init; } = [];
}

/// <summary>
/// Bounded instruction-first capture request. The server owns the target
/// process, module, instruction, registers, and displacements.
/// </summary>
public sealed record InstructionSnapshotRequest
{
    public int DurationMilliseconds { get; init; } = 5_000;
    public int MaxHits { get; init; } = 64;
}

/// <summary>One privacy-projected entity-bound XYZ snapshot.</summary>
public sealed record InstructionSnapshotHitResponse
{
    public int Sequence { get; init; }
    public string ObjectKey { get; init; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; init; }
    public bool ReplayEntityIdReadOk { get; init; }
    public int? ReplayEntityId { get; init; }
    public bool ReadOk { get; init; }
    public bool Finite { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public bool SameDebugEvent { get; init; }
    public bool SingleRead12Bytes { get; init; }
    public bool ObjectRegisterCaptured { get; init; }
    public bool HardwareAtomicReadProven { get; init; }
    public bool SameDecodedClockProven { get; init; }
    public bool ViewpointIdentityProven { get; init; }
    public bool StableRootProven { get; init; }
}

/// <summary>Privacy-safe aggregate instruction-first capture response.</summary>
public sealed record InstructionSnapshotResponse
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public string TargetModule { get; init; } = string.Empty;
    public string TargetRva { get; init; } = string.Empty;
    public bool InstructionFingerprintMatched { get; init; }
    public bool CleanupProven { get; init; }
    public bool Truncated { get; init; }
    public int HitCount { get; init; }
    public List<InstructionSnapshotHitResponse> Hits { get; init; } = [];
}

/// <summary>One downsampled ground-truth position sample.</summary>
public sealed record TrajectorySampleResponse(
    long ReplayTimeTicks,
    double X,
    double Y,
    double Z);

/// <summary>One tracked entity's ground-truth trajectory.</summary>
public sealed record TrajectoryEntityResponse(
    long? EntityId,
    string? ParticipantId,
    string? TankName,
    bool IsViewpoint,
    List<TrajectorySampleResponse> Samples);

/// <summary>Ground-truth trajectories for one decoded battle session.</summary>
public sealed record TrajectoryResponse(
    Guid BattleSessionId,
    long DurationTicks,
    List<TrajectoryEntityResponse> Entities);

/// <summary>One observed read of a monitored address (wall time + value).</summary>
public sealed record CorrelationSampleRequest(
    DateTimeOffset WallTimeUtc,
    double Value);

/// <summary>One monitored address's observed value series.</summary>
public sealed record CorrelationSeriesRequest(
    string Address,
    List<CorrelationSampleRequest> Samples);

/// <summary>
/// Scores monitored address series against the decoded replay trajectory
/// (strategy v4). The replay plays at 1x; no exact pause is required.
/// </summary>
public sealed record CorrelateRequest
{
    /// <summary>Decoded battle session providing the ground truth.</summary>
    public Guid GroundTruthSessionId { get; init; }

    /// <summary>
    /// Wall-clock time the replay started (the Start marker), anchoring wall
    /// time to replay tick 0. Residual error is absorbed by the shift sweep.
    /// </summary>
    public DateTimeOffset ReplayStartWallTimeUtc { get; init; }

    /// <summary>Per-axis tolerance in world units (default 6).</summary>
    public double TolerancePerAxis { get; init; } = 6.0;

    /// <summary>Shift sweep bound in seconds (default 8; the driver uses 30 to
    /// absorb load latency after the Start marker).</summary>
    public int MaxTimeShiftSeconds { get; init; } = 8;

    /// <summary>
    /// Shift sweep granularity in seconds (default 0.5). Sub-second steps keep
    /// the residual position offset (speed x residual) inside tolerance for
    /// fast movers; whole-second steps would reject them.
    /// </summary>
    public double ShiftStepSeconds { get; init; } = 0.5;

    /// <summary>Observed series with a span below this are treated as constants and skipped.</summary>
    public double MinMovingSpan { get; init; } = 0.5;

    public List<CorrelationSeriesRequest> Observations { get; init; } = [];
}

/// <summary>
/// Correlated evidence for one monitored address. <see cref="ShiftSeconds"/>
/// is the sweep shift (real seconds) that produced the best match; negative
/// when the observed series trails the anchor (e.g. load latency).
/// <see cref="ShiftMinSeconds"/>/<see cref="ShiftMaxSeconds"/> bound the
/// ambiguity band (all shifts in [Min, Max] achieve the same match count);
/// the band edges expose sweep-edge riding that the reported shift can mask.
/// </summary>
public sealed record CorrelateResultItemResponse(
    string Address,
    string? ParticipantId,
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

/// <summary>Result of a trajectory correlation pass.</summary>
public sealed record CorrelateResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int AddressesScored { get; init; }
    public int TotalSamples { get; init; }
    public List<CorrelateResultItemResponse> Results { get; init; } = [];

    /// <summary>
    /// Candidate coordinate families (strategy v4 M2): scored addresses that
    /// sit inside one small byte window and reproduce the same entity's axes,
    /// grouped by proximity. Empty when no neighbor grouping emerged.
    /// </summary>
    public List<TrajectoryFamilyResponse> Families { get; init; } = [];
}

/// <summary>One member of a candidate coordinate family.</summary>
public sealed record FamilyMemberResponse(
    string Address,
    int OffsetBytes,
    string Axis,
    int Sign,
    double ShiftSeconds,
    double ShiftMinSeconds,
    double ShiftMaxSeconds,
    double Score,
    bool EdgeAligned);

/// <summary>
/// A candidate coordinate family: scored addresses inside one small byte
/// window around a common base that reproduce the same entity's axes.
/// <see cref="Complete"/> is true only for the clean triple (one member per
/// axis x/y/z, none edge-aligned) — the "one session maps all three
/// coordinate components" result; multi-copy families are still reported but
/// flagged incomplete.
/// </summary>
public sealed record TrajectoryFamilyResponse(
    string BaseAddress,
    int SpanBytes,
    List<string> AxesCovered,
    bool Complete,
    List<FamilyMemberResponse> Members);
