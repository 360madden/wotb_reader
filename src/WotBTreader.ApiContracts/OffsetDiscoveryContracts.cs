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
/// Requests one server-owned module-rooted lookup by decoded replay entity ID.
/// The request intentionally contains no process identity or memory address.
/// </summary>
public sealed record EntityPositionReadRequest
{
    public int EntityId { get; init; }

    /// <summary>
    /// Optional battle session id (GUID) whose replay-clock segments attest
    /// same-decoded-clock alignment. Omitted or unparseable ids never claim
    /// the flag.
    /// </summary>
    public string? BattleSessionId { get; init; }
}

/// <summary>
/// Privacy-safe result of one exact-build entity-position lookup. A resolved
/// double-read is neither hardware atomic nor automatically same-clock with
/// decoded replay telemetry.
/// </summary>
public sealed record EntityPositionReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int EntityId { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public string? EntitySource { get; init; }
    public string? FailureStage { get; init; }
    public int Attempts { get; init; }
    public int NodesVisited { get; init; }
    public bool ModuleRooted { get; init; }
    public bool EntityIdentityRevalidated { get; init; }
    public bool ConsistentDoubleRead { get; init; }
    public bool HardwareAtomicReadProven { get; init; }
    public bool SameDecodedClockProven { get; init; }
}

/// <summary>
/// Requests one bounded entity ring-record region dump (the L0 seam the
/// HP / facing / damage-dealt / replayTime live plans all consume). Only the
/// decoded entity id and a bounded region length (≤ 4096 bytes) are
/// caller-supplied; the coordinator owns the process identity and the
/// resolved record address.
/// </summary>
public sealed record EntityRecordRegionReadRequest
{
    public int EntityId { get; init; }

    /// <summary>Bounded region length in bytes (1..4096).</summary>
    public int RegionLength { get; init; }

    /// <summary>
    /// Optional battle session id (GUID) whose replay-clock segments attest
    /// same-decoded-clock alignment and label the dump with replay time.
    /// Omitted or unparseable ids never claim the flag.
    /// </summary>
    public string? BattleSessionId { get; init; }

    /// <summary>
    /// Which object the dump anchors on: <c>ring-record</c> (the movement
    /// ring record the position resolver reads — the default),
    /// <c>entity-tank-record</c> (the per-entity tank record at
    /// <c>[entity+0x3C]</c>), <c>entity-base</c> (the entity base record
    /// itself — the statically-verified HP fields live at
    /// <c>[entity+0xB8]</c> int16 current health, +0xBA alive byte,
    /// +0x11E healing, per VerifyPlayerHpChain on the 11.19.0.10 build), or
    /// <c>avatar-stats</c> (the entity-factory Avatar's uint32 battle-stats
    /// quad at <c>[avatar+0x118..0x124]</c> — the L3 damage-dealt family;
    /// for this anchor <c>entityId</c> is ignored and the coordinator runs
    /// the gated vftable AOB scan for the entity-Avatar instead), or
    /// <c>pen-ownership-walk</c> (the viewpoint-vehicle ownership walk: the
    /// coordinator scans for the unique VehicleGunRotator and runs the fixed
    /// five-read chain, returning only aggregate booleans/counts), or
    /// <c>shell-state</c> (the loaded-shell index + identity fingerprint:
    /// reuses the rotator scan to reach the owner, then reads the embedded
    /// AmmoController's index and the resolved shell identity dwords,
    /// aggregate-only). Unknown values fail closed (no dump).
    /// </summary>
    public string? RegionAnchor { get; init; }

    /// <summary>
    /// For <c>avatar-stats</c> dumps: which scan candidate (0..3) to anchor
    /// on. The scan enumerates up to 4 entity-Avatar candidates (one per
    /// player); the increment correlator discriminates the OWN counter at
    /// scoring time (only the own counter increments on own-attacker
    /// events; other candidates stay flat as built-in control windows).
    /// Defaults to 0. Ignored for entity-keyed anchors.
    /// </summary>
    public int? AvatarCandidateIndex { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: which rotator scan candidate
    /// (0..7) to validate. Defaults to 0. Ignored for other anchors.
    /// </summary>
    public int? OwnershipCandidateIndex { get; init; }
}

/// <summary>
/// Privacy-safe result of one entity ring-record region dump: the raw bytes
/// (base64) + replay time ONLY. No absolute address, process id, or module
/// base ever leaves the coordinator. Raw region bytes are session evidence,
/// never published in aggregates.
/// </summary>
public sealed record EntityRecordRegionReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int EntityId { get; init; }
    public double? ReplayTimeSeconds { get; init; }
    public string? RegionBase64 { get; init; }
    public string? FailureStage { get; init; }
    public int Attempts { get; init; }
    public int NodesVisited { get; init; }
    public bool ModuleRooted { get; init; }
    public bool SameDecodedClockProven { get; init; }

    /// <summary>
    /// For <c>avatar-stats</c> dumps: how many entity-Avatar scan candidates
    /// the scan found (0 when the anchor is entity-keyed). The caller loops
    /// <see cref="EntityRecordRegionReadRequest.AvatarCandidateIndex"/>
    /// 0..count-1 to dump every candidate's quad for the correlator's
    /// own-counter discrimination.
    /// </summary>
    public int AvatarCandidateCount { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: how many VehicleGunRotator scan
    /// candidates the scan found (0 for other anchors).
    /// </summary>
    public int PenOwnershipRotatorCandidateCount { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: the rotator's +0x10 owner
    /// pointer resolved to a non-null value.
    /// </summary>
    public bool PenOwnershipOwnerPointerReadable { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: the owner's +0x1fc field points
    /// back to the same rotator (forward round-trip).
    /// </summary>
    public bool PenOwnershipForwardRoundTripConfirmed { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: the owner's +0x204 points to an
    /// object whose first dword is the VehicleGun vftable.
    /// </summary>
    public bool PenOwnershipGunVtableConfirmed { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: the owner's +0x04 entity pointer
    /// resolves to a non-negative current-HP int16 at +0xB8.
    /// </summary>
    public bool PenOwnershipEntityHpPlausible { get; init; }

    /// <summary>
    /// For <c>pen-ownership-walk</c> probes: the two passes produced
    /// identical verdicts.
    /// </summary>
    public bool PenOwnershipTwoPassStable { get; init; }

    /// <summary>
    /// For <c>shell-state</c> probes: the current-shell index read from the
    /// embedded AmmoController (+0x38). Null for other anchors or when the
    /// walk did not resolve.
    /// </summary>
    public int? ShellStateIndex { get; init; }

    /// <summary>
    /// For <c>shell-state</c> probes: the resolved shell identity holder's
    /// first identity dword (+0x20). Null for other anchors or when the
    /// walk did not resolve.
    /// </summary>
    public int? ShellStateIdentity0 { get; init; }

    /// <summary>
    /// For <c>shell-state</c> probes: the resolved shell identity holder's
    /// second identity dword (+0x24). Null for other anchors or when the
    /// walk did not resolve.
    /// </summary>
    public int? ShellStateIdentity1 { get; init; }

    /// <summary>
    /// For <c>shell-state</c> probes: the two passes produced identical
    /// index + identity verdicts.
    /// </summary>
    public bool ShellStateTwoPassStable { get; init; }
}

/// <summary>
/// One entity region in a batch read (mirrors the single-read fields).
/// When <see cref="EntityBaseRegionLength"/> is set, the batch ALSO reads
/// that many bytes of the entity-base region for the same entity (the L1
/// HP surface) under the SAME resolve and the SAME single replay-clock
/// attestation.
/// </summary>
public sealed record EntityRegionReadItemRequest
{
    public int EntityId { get; init; }
    public int RegionLength { get; init; }
    public string? RegionAnchor { get; init; }
    public int? EntityBaseRegionLength { get; init; }
}

/// <summary>
/// Batch entity-region read: up to 16 bounded region dumps in one round
/// trip with ONE replay-clock attestation for the whole batch (the per-frame
/// live read surface design — see docs/operations/batch-entity-read-design.md).
/// </summary>
public sealed record EntityRegionsReadRequest
{
    public IReadOnlyList<EntityRegionReadItemRequest> Entities { get; init; } = [];
    public string? BattleSessionId { get; init; }
}

/// <summary>
/// Outcome of one entity within a batch region read. The optional
/// <see cref="EntityBaseRegionBase64"/> (and its failure stage) cover the
/// L1 entity-base read when the request asked for one: an entity whose
/// primary region resolved but whose entity-base read failed keeps its
/// ring bytes and reports the entity-base failure separately.
/// </summary>
public sealed record EntityRegionReadItemResponse
{
    public int EntityId { get; init; }
    public string Status { get; init; } = string.Empty;
    public double? ReplayTimeSeconds { get; init; }
    public string? RegionBase64 { get; init; }
    public string? FailureStage { get; init; }
    public int Attempts { get; init; }
    public int NodesVisited { get; init; }
    public bool ModuleRooted { get; init; }
    public bool EntityIdentityRevalidated { get; init; }
    public bool ConsistentDoubleRead { get; init; }
    public int RegionReadAttempts { get; init; }
    public bool RegionTearObserved { get; init; }
    public string? EntityBaseRegionBase64 { get; init; }
    public string? EntityBaseFailureStage { get; init; }
    public int EntityBaseAttempts { get; init; }
    public bool EntityBaseTearObserved { get; init; }
}

/// <summary>
/// Privacy-safe batch region read result: the raw bytes (base64) + ONE
/// replay-time label per batch. No absolute address, process id, or module
/// base ever leaves the coordinator. Per-entity statuses are authoritative;
/// the batch status is the gate-level outcome.
/// </summary>
public sealed record EntityRegionsReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double? ReplayTimeSeconds { get; init; }
    public bool SameDecodedClockProven { get; init; }
    public IReadOnlyList<EntityRegionReadItemResponse> Regions { get; init; } = [];

    /// <summary>
    /// Wall-clock measurement of the read pass (null when no reads
    /// happened). The verification window for the item-7 atomicity
    /// groundwork — how long the whole-roster frame read takes.
    /// </summary>
    public EntityRegionsReadMeasurementResponse? Measurement { get; init; }
}

/// <summary>Wall-clock measurement of one batch read pass.</summary>
public sealed record EntityRegionsReadMeasurementResponse
{
    public DateTimeOffset BatchStartedAtUtc { get; init; }
    public DateTimeOffset BatchEndedAtUtc { get; init; }
    public DateTimeOffset? ClockSnapshotAtUtc { get; init; }
}

/// <summary>
/// Privacy-safe response of the live roster enumeration endpoint: the
/// avatar-family entity ids ONLY, plus the filter-precision counters the
/// live rehearsal cross-checks against the decoded roster. No absolute
/// address, process id, or module base is ever returned (design:
/// docs/operations/live-roster-read-design.md).
/// </summary>
public sealed record EntityRosterReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? FailureStage { get; init; }
    public int CandidatesSeen { get; init; }
    public int FilteredOut { get; init; }
    public bool ModuleRooted { get; init; }
    public bool TraversalLimited { get; init; }
    public IReadOnlyList<int> EntityIds { get; init; } = [];
}

/// <summary>
/// Request for one composed live frame. The optional battle session id
/// enables the ONE G2 replay-clock attestation for the frame; omitted never
/// claims the flag.
/// </summary>
public sealed record LiveFrameReadRequest
{
    public string? BattleSessionId { get; init; }
}

/// <summary>
/// One tank of a live frame response: entity id + world position + hull
/// yaw, when that entity resolved, plus live health when the entity-base
/// read resolved (L1: current int16 +0xB8, max +0x11C, alive +0xBA). All
/// health fields are honest nulls when not read or not decodable — the HUD
/// must never render a fabricated health value (design:
/// docs/operations/live-frame-loop-design.md). World coordinates only.
/// </summary>
public sealed record LiveFrameTankResponse
{
    public int EntityId { get; init; }
    public string Status { get; init; } = string.Empty;
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? YawRadians { get; init; }
    public double? HpCurrent { get; init; }
    public double? HpMax { get; init; }
    public bool? Alive { get; init; }
    public string? FailureStage { get; init; }
    public bool ModuleRooted { get; init; }
    public string? HpFailureStage { get; init; }
}

/// <summary>
/// One composed live frame (design: docs/operations/live-frame-loop-design.md):
/// the camera pose, every roster tank's position/facing, and ONE replay-
/// clock label for the frame. No absolute address, process id, or module
/// base is ever returned. The camera pose is embedded (null when the pose
/// walk did not resolve).
/// </summary>
public sealed record LiveFrameReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? FailureStage { get; init; }
    public double? ReplayTimeSeconds { get; init; }
    public bool SameDecodedClockProven { get; init; }
    public CameraPoseReadResponse? Camera { get; init; }
    public IReadOnlyList<LiveFrameTankResponse> Tanks { get; init; } = [];
    public int RosterCandidatesSeen { get; init; }
    public int RosterFilteredOut { get; init; }

    /// <summary>
    /// Wall-clock measurement of the composed frame read pass (null when no
    /// reads happened). The loop's per-frame timing budget — the item-7
    /// atomicity groundwork.
    /// </summary>
    public LiveFrameReadMeasurementResponse? Measurement { get; init; }
}

/// <summary>Wall-clock measurement of one composed live frame read pass.</summary>
public sealed record LiveFrameReadMeasurementResponse
{
    public DateTimeOffset FrameStartedAtUtc { get; init; }
    public DateTimeOffset FrameEndedAtUtc { get; init; }
    public DateTimeOffset? ClockSnapshotAtUtc { get; init; }
}

/// <summary>
/// Response of the gate-verified camera-pose endpoint (CAM-001 chain): the
/// GameCamera world pose plus per-hop identity flags. Addresses are
/// diagnostic evidence formatted as hex, never runtime read offsets.
/// </summary>
public sealed record CameraPoseReadResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? FailureStage { get; init; }
    public string? AvatarAddress { get; init; }
    public string? CameraAddress { get; init; }
    public string? CameraStateAddress { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? YawRadians { get; init; }
    public double? PitchRadians { get; init; }
    public IReadOnlyList<double>? Basis { get; init; }
    public bool AvatarIdentityVerified { get; init; }
    public bool CameraIdentityVerified { get; init; }
    public bool CameraStateIdentityVerified { get; init; }
    public bool ConsistentDoubleRead { get; init; }
    public bool ModuleRooted { get; init; }
}

/// <summary>
/// Diagnostic request for the gate-verified position-page endpoint. Only the
/// decoded entity ID is caller-supplied; the process identity and the
/// resolved page address are coordinator-owned. Internal diagnostic surface
/// for guard-page interceptor arming - never returned by the poll read path.
/// </summary>
public sealed record EntityPositionAddressRequest
{
    public int EntityId { get; init; }
}

/// <summary>
/// Diagnostic response carrying the ring-record page address (hex) for
/// guard-page interceptor arming. Internal-only surface: never returned by
/// the poll read path and never persisted in poll aggregates.
/// </summary>
public sealed record EntityPositionAddressResponse
{
    public string Status { get; init; } = string.Empty;
    public string? RecordAddress { get; init; }
    public string? PageAddress { get; init; }
    public string? FailureStage { get; init; }
    public int Attempts { get; init; }
    public int NodesVisited { get; init; }
    public bool ModuleRooted { get; init; }
}

/// <summary>
/// One replay-clock synchronization segment to append for a battle session.
/// The caller supplies the anchor values (wall-clock UTC, replay anchor,
/// speed, source, uncertainty) and a monotonically increasing sequence; the
/// server assigns the segment id and creation time and enforces monotonicity.
/// </summary>
public sealed record AppendClockSegmentRequest
{
    public string? BattleSessionId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset SourceAnchorUtc { get; init; }
    public long ReplayAnchorTicks { get; init; }
    public double Speed { get; init; }
    public string? Source { get; init; }
    public long UncertaintyTicks { get; init; }
}

/// <summary>Privacy-safe confirmation of an appended replay-clock segment.</summary>
public sealed record AppendClockSegmentResponse
{
    public string? BattleSessionId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset SourceAnchorUtc { get; init; }
    public long ReplayAnchorTicks { get; init; }
    public double Speed { get; init; }
    public string? Source { get; init; }
    public long UncertaintyTicks { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
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

/// <summary>
/// Request to run one serialized, coordinator-owned penetration capture. The
/// caller names only the decoded run; the coordinator owns process identity,
/// module base, and every read location. POST
/// /api/v1/game/discover/pen-capture.
/// </summary>
public sealed record PenetrationCaptureRequest
{
    public string? DecodeRunId { get; init; }
}

/// <summary>
/// Privacy-safe evaluation of one managed-offline penetration capture. No
/// address, process id, path, token, or raw observation is represented.
/// </summary>
public sealed record PenetrationCaptureResponse
{
    public string Status { get; init; } = string.Empty;
    public string PrimaryReason { get; init; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public bool ExactWeaponOwnerProven { get; init; }
    public bool ExactLoadedShellProven { get; init; }
    public bool ExactGunRayProven { get; init; }
    public int OwnerCandidateCount { get; init; }
    public int ShellStatesObserved { get; init; }
    public int ShellIdentityMatches { get; init; }
    public int AimSamples { get; init; }
    public int RaySamples { get; init; }
    public int JoinedRaySamples { get; init; }
}
