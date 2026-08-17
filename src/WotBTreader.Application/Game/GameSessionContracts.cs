using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Game;

/// <summary>
/// Evidence-backed state of the local game session. Only
/// <see cref="OfflineReplayVerified"/> permits guarded memory observation.
/// </summary>
public enum GameSessionVerificationState
{
    Unknown,
    GameAbsent,
    GamePresentUnverified,
    OfflineReplayVerified,
    EvidenceStale,
    Denied,
}

/// <summary>
/// Capability-neutral session information for hosts and tools. This snapshot
/// is informational and cannot be exchanged for process access.
/// </summary>
public sealed record GameSessionSnapshot(
    GameSessionVerificationState State,
    bool GamePresent,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? EvidenceExpiresAtUtc,
    string ReasonCode);

/// <summary>
/// Reads the current safe game-session state. This is a read-only surface
/// that never exposes process handles, authorization details, or offsets.
/// </summary>
public interface IGameSessionState
{
    /// <summary>
    /// Returns the current evidence-backed session state snapshot.
    /// Never throws — returns <see cref="GameSessionVerificationState.Unknown"/>
    /// when the state cannot be determined.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current session state snapshot.</returns>
    ValueTask<GameSessionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Requests launch of an immutable replay already managed by the application.
/// The adapter owns launch correlation and never accepts caller-supplied
/// process or executable identity.
/// </summary>
public sealed record GameReplayLaunchRequest(
    SourceArtifactId SourceArtifactId,
    BattleSessionId? BattleSessionId = null);

public enum ManagedReplayAssociationStatus
{
    Missing = 0,
    PendingVerification,
    Verified,
    Stale,
}

/// <summary>
/// Ephemeral proof that a decoded battle session belongs to the currently
/// verified managed replay. The adapter keeps its launch epoch private.
/// </summary>
public interface IManagedReplayAssociationLease
{
    BattleSessionId BattleSessionId { get; }

    ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken);
}

public sealed record ManagedReplayAssociationAcquireResult(
    ManagedReplayAssociationStatus Status,
    string ReasonCode,
    IManagedReplayAssociationLease? Lease);

/// <summary>
/// Acquires an association lease owned by the guarded game-session adapter.
/// The lease is metadata correlation, never process-read authorization.
/// </summary>
public interface IManagedReplayAssociationLeaseSource
{
    ValueTask<ManagedReplayAssociationAcquireResult> AcquireAsync(
        CancellationToken cancellationToken);
}

/// <summary>Safe result of a managed replay launch request.</summary>
public sealed record GameReplayLaunchOutcome(DateTimeOffset RequestedAtUtc);

/// <summary>
/// Launches managed replay artifacts through the verified game adapter.
/// The adapter owns correlation and never accepts caller-supplied paths.
/// </summary>
public interface IGameReplayLauncher
{
    /// <summary>
    /// Launches a managed replay artifact through the installed game.
    /// Returns the launch outcome on success, or an error with a stable code.
    /// </summary>
    /// <param name="request">The managed artifact to launch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Launch outcome on success, or an application error.</returns>
    ValueTask<OperationResult<GameReplayLaunchOutcome>> LaunchAsync(
        GameReplayLaunchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Availability of a guarded memory observation. Unknown and unsupported are
/// distinct from legitimate zero-valued telemetry.
/// </summary>
public enum GameMemoryObservationAvailability
{
    Unknown,
    Unsupported,
    Available,
}

/// <summary>
/// Ephemeral, capability-neutral telemetry from a positively verified offline
/// replay. Null fields are unknown and are never silently treated as zero.
/// </summary>
public sealed record GameMemoryObservation(
    GameMemoryObservationAvailability Availability,
    DateTimeOffset CapturedAtUtc,
    double? ReplayTimeSeconds,
    int? PlayerHitPoints,
    float? PlayerPositionX,
    float? PlayerPositionY,
    float? PlayerPositionZ,
    float? PlayerYaw,
    float? CameraPitch,
    int? AliveTankCount);

/// <summary>
/// Returns safe memory observations without exposing process identity,
/// handles, authorization leases, offsets, or attachment operations.
/// Returns <see cref="GameMemoryObservationAvailability.Unknown"/> when
/// the offline-session gate is not satisfied (the coordinator emits
/// <c>Available</c> or <c>Unknown</c> only; <c>Unsupported</c> is reserved
/// and not produced today).
/// </summary>
public interface IGameMemoryObserver
{
    /// <summary>
    /// Captures a single memory observation snapshot from the verified game process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bounded observation with nullable telemetry fields.</returns>
    ValueTask<GameMemoryObservation> ObserveAsync(
        CancellationToken cancellationToken);
}

/// <summary>Primitive representation used by typed scans and next-scan comparisons.</summary>
public enum MemoryValueKind
{
    Bytes,
    Int32Value,
    UInt32Value,
    Int64Value,
    UInt64Value,
    FloatValue,
    DoubleValue,
}

/// <summary>Controls which virtual-memory mappings are eligible for discovery.</summary>
[Flags]
public enum MemoryRegionSelection
{
    None = 0,
    Private = 1,
    Mapped = 2,
    Image = 4,
    Default = Private | Mapped,
}

/// <summary>
/// Request to scan the verified game process memory for a typed value or an
/// AOB-style byte pattern. Non-zero tolerance-mask bytes are wildcards.
/// </summary>
public sealed record MemoryScanRequest(
    string FieldName,
    string FieldType,
    byte[] ExpectedValue,
    byte[]? ToleranceMask,
    int MaxCandidates,
    long MinRegionSize,
    int Alignment = 1,
    MemoryRegionSelection RegionSelection = MemoryRegionSelection.Default,
    bool IncludeWorkingSetClassification = false,
    MemoryValueKind ValueKind = MemoryValueKind.Bytes,
    float? FloatTolerance = null);

/// <summary>One bounded, single-root pointer-chain resolution request.</summary>
public sealed record MemoryPointerChainRequest(
    long RootRelativeOffset,
    IReadOnlyList<long> PointerOffsets,
    int MaxDepth = 4);

/// <summary>A resolved pointer-chain candidate. It is evidence only, never a runtime offset.</summary>
public sealed record MemoryPointerChainCandidate(
    long RootAddress,
    long FinalAddress,
    IReadOnlyList<long> TraversedAddresses,
    string AddressKind);

/// <summary>Result of a bounded, single-root pointer-chain exploration.</summary>
public sealed record MemoryPointerChainResult(
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<MemoryPointerChainCandidate> Candidates,
    int RejectedChains);

/// <summary>
/// One candidate address returned by a memory scan. BaseDisplacement is an
/// arithmetic displacement from the supplied scan base; it is not a module RVA
/// unless ownership by the main image has been independently proven.
/// </summary>
public sealed record MemoryScanCandidate(
    long AbsoluteAddress,
    long BaseDisplacement,
    byte[] ObservedValue,
    string ValueSummary,
    string AddressKind = "absolute",
    bool IsCopyOnWrite = false);

/// <summary>Results of a single memory scan pass for offset discovery.</summary>
public sealed record MemoryScanResult(
    DateTimeOffset CompletedAtUtc,
    long BaseAddress,
    int RegionsScanned,
    long BytesScanned,
    IReadOnlyList<MemoryScanCandidate> Candidates,
    int TotalMatchesBeforeTruncation,
    string TargetArchitecture = "unknown",
    string ModuleName = "unknown",
    long ModuleSize = 0,
    int Alignment = 1,
    bool Truncated = false,
    string ScanKind = "value");

/// <summary>
/// Scans the verified game process memory for specific value patterns.
/// Only callable when the offline-session gate is satisfied.
/// </summary>
public interface IGameMemoryScanner
{
    ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
        MemoryScanRequest request,
        CancellationToken cancellationToken);

    /// <summary>Scans an AOB/wildcard pattern using the same guarded region pipeline.</summary>
    ValueTask<OperationResult<MemoryScanResult>> ScanPatternAsync(
        MemoryScanRequest request,
        CancellationToken cancellationToken);

    /// <summary>Resolves a short, bounded pointer chain for evidence collection.</summary>
    ValueTask<OperationResult<MemoryPointerChainResult>> ResolvePointerChainAsync(
        MemoryPointerChainRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates a snapshot of all values matching the filter. Returns a session ID.</summary>
    ValueTask<OperationResult<string>> CreateSnapshotAsync(
        MemorySnapshotRequest request,
        CancellationToken cancellationToken);

    /// <summary>Compares current memory against a stored snapshot.</summary>
    ValueTask<OperationResult<MemoryCompareResult>> CompareAsync(
        string sessionId,
        string compareMode,
        int maxCandidates,
        CancellationToken cancellationToken,
        bool advanceBaseline = false,
        double? deltaTarget = null,
        double? deltaTolerance = null);

    /// <summary>Discards a stored snapshot session.</summary>
    void DiscardSession(string sessionId);

    /// <summary>Reads a window of memory around a known offset and reports all plausible values.</summary>
    ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
        MemoryNeighborhoodRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads a fixed, staged set of absolute addresses (the replay-guided
    /// correlation monitor primitive). Only callable when the offline-session
    /// gate is satisfied.
    /// </summary>
    ValueTask<OperationResult<MemoryReadResult>> ReadAddressesAsync(
        MemoryReadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one decoded replay entity ID through the server-owned,
    /// exact-build BWEntities layout and reads its newest retained position.
    /// No process ID or runtime address is caller-controlled or returned.
    /// </summary>
    ValueTask<OperationResult<EntityPositionReadResult>> ReadEntityPositionAsync(
        EntityPositionReadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a bounded region of the entity's ring record (≤ 4 KB) and labels
    /// it with the replay clock. The caller supplies only the decoded entity
    /// id and the region length; the coordinator owns process identity and the
    /// resolved record address. Returns ONLY the bytes + replay time — never
    /// an absolute address.
    /// </summary>
    ValueTask<OperationResult<EntityRecordRegionReadResult>> ReadEntityRegionAsync(
        EntityRecordRegionReadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads bounded regions of up to 16 entities in one round trip with ONE
    /// replay-clock attestation for the batch (the per-frame live surface).
    /// Caller supplies only decoded entity ids + region lengths + anchors;
    /// the coordinator owns process identity and every resolved address.
    /// Returns ONLY bytes + one replay time — never absolute addresses.
    /// Per-entity statuses are authoritative: an unresolved entity fails only
    /// itself; a gate-level failure (build mismatch, inactive phase) fails
    /// the whole batch.
    /// </summary>
    ValueTask<OperationResult<EntityRegionsReadResult>> ReadEntityRegionsAsync(
        EntityRegionsReadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the live avatar-family roster (entity ids ONLY) through the
    /// module-rooted BWEntities maps — the live counterpart to a decoded
    /// participants roster (design: docs/operations/live-roster-read-design.md).
    /// Diagnostic-only: the returned ids feed the existing
    /// <see cref="ReadEntityRegionsAsync"/> batch surface unchanged. No
    /// absolute address, process id, or module base ever leaves the
    /// coordinator — enumeration addresses are consumed inside it and the
    /// result carries ids plus the filter precision counters
    /// (<see cref="EntityRosterReadResult.CandidatesSeen"/> /
    /// <see cref="EntityRosterReadResult.FilteredOut"/>).
    /// </summary>
    ValueTask<OperationResult<EntityRosterReadResult>> EnumerateEntitiesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Composes one live frame (design: docs/operations/live-frame-loop-design.md):
    /// enumerate the avatar-family roster once, batch-read every roster
    /// entity's ring record through the batch surface (ONE G2 clock
    /// attestation when a battle session id is supplied), decode position
    /// (+0x10) and hull yaw (+0x30), read the CAM-001 camera pose, and
    /// assemble the frame — all under the scan authorization. The batch
    /// also reads each entity's entity-base region (L1: current health
    /// int16 +0xB8, max +0x11C) so the frame carries live health; health
    /// fields stay honest nulls when that read failed or decoded invalid.
    /// Per-tank statuses are authoritative; gate-level failures (build
    /// mismatch, inactive phase, revoked authorization) fail the WHOLE
    /// frame. No absolute address, process id, or module base ever leaves
    /// the coordinator.
    /// </summary>
    ValueTask<OperationResult<LiveFrameReadResult>> ReadLiveFrameAsync(
        LiveFrameReadRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Diagnostic-only, gate-verified: runs the same traversal as
    /// <see cref="ReadEntityPositionAsync"/> and returns the ring-record page
    /// address so the guard-page interceptor can arm the exact page a poll
    /// reads. The address is never returned by the read path and never lands
    /// in poll results or persisted aggregates.
    /// </summary>
    ValueTask<OperationResult<EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
        EntityPositionAddressRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the live GameCamera pose through the CAM-001 fixed member-path
    /// (avatar vftable anchor → BattleResources → camera controller →
    /// GameCamera) with an identity gate on every hop, for a gate-verified
    /// offline session. The chain is deliberately gate-free with respect to
    /// the session-controller vftable (CAM-003: it flips between launches),
    /// so this works in both phases. Only the pose + identity flags leave the
    /// coordinator; process identity stays inside.
    /// </summary>
    ValueTask<OperationResult<CameraPoseReadResult>> ReadCameraPoseAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Captures bounded register-derived position triples at a version-pinned
    /// game-code instruction. The coordinator, not the caller, supplies the
    /// process identity, module, RVA, register, and member displacement.
    /// </summary>
    ValueTask<OperationResult<InstructionSnapshotResult>> CaptureInstructionSnapshotAsync(
        InstructionSnapshotRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request to re-read a fixed set of absolute addresses. The set is staged by
/// an earlier scan; the values are correlated against the decoded replay
/// trajectory while the replay plays.
/// </summary>
public sealed record MemoryReadRequest(
    IReadOnlyList<long> Addresses,
    int ValueSize = 4,
    MemoryValueKind ValueKind = MemoryValueKind.FloatValue);

/// <summary>One per-address read result.</summary>
public sealed record MemoryReadItem(
    long AbsoluteAddress,
    bool ReadOk,
    byte[]? ObservedValue,
    string ValueSummary);

/// <summary>Result of re-reading a staged address set.</summary>
public sealed record MemoryReadResult(
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<MemoryReadItem> Reads);

/// <summary>
/// Request for one module-rooted, entity-ID-bound position read. The entity ID
/// must come from decoded replay evidence; the coordinator owns the process,
/// module, build layout, and all addresses.
/// </summary>
/// <summary>
/// One bounded module-rooted entity-position lookup. When
/// <paramref name="BattleSessionId"/> is supplied, the coordinator may attest
/// same-decoded-clock alignment from that session's replay-clock segments;
/// when null, <c>SameDecodedClockProven</c> is never claimed.
/// </summary>
public sealed record EntityPositionReadRequest(
    int EntityId,
    BattleSessionId? BattleSessionId = null);

/// <summary>
/// Privacy-safe result from the exact-build type-10 entity resolver. A
/// successful consistent double-read is not a hardware-atomic snapshot and is
/// not automatically aligned to a decoded replay clock.
/// </summary>
public sealed record EntityPositionReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    Type10EntityPositionStatus Status,
    int EntityId,
    float? X,
    float? Y,
    float? Z,
    string? EntitySource,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted,
    bool EntityIdentityRevalidated,
    bool ConsistentDoubleRead,
    bool HardwareAtomicReadProven,
    bool SameDecodedClockProven);

/// <summary>
/// Which object a region dump anchors on: the movement ring record the
/// position resolver reads (position +0x10, velocity +0x28, ring stride
/// 0x38), the per-entity tank record reached by dereferencing
/// <c>[entity + 0x3C]</c>, or the entity base record itself (the
/// statically-verified health fields: current HP as signed int16 at
/// [entity+0xB8], alive byte at +0xBA, healing int16 at +0x11E per
/// VerifyPlayerHpChain on the 11.19.0.10 build). The coordinator owns all
/// addresses; the caller only picks the anchor.
/// </summary>
public enum EntityRecordRegionAnchor
{
    /// <summary>The movement ring record (the position resolver's target).</summary>
    RingRecord = 0,

    /// <summary>
    /// The tank record at <c>[entity + 0x3C]</c> — the Ghidra-candidate
    /// position/rotation region. The coordinator dereferences the pointer
    /// itself under the same guarded lease; only bytes leave.
    /// </summary>
    EntityTankRecord = 1,

    /// <summary>
    /// The entity base record itself (offset 0 of the resolved entity). The
    /// static playerHP evidence pins current health at <c>[entity+0xB8]</c>
    /// (signed int16), the alive byte at <c>[entity+0xBA]</c>, and the
    /// healing int16 at <c>[entity+0x11E]</c> — so an HP session anchors
    /// here (region length ≥ 0x120) and correlates int16 candidates.
    /// </summary>
    EntityBase = 2,

    /// <summary>
    /// The entity-factory Avatar object's battle-stats quad (L3 damage-dealt
    /// discovery). The <see cref="EntityRecordRegionReadRequest.EntityId"/> is
    /// IGNORED for this anchor: instead of the entity-ID resolver, the
    /// coordinator runs the gated vftable AOB scan targeted at
    /// <c>moduleBase + 0x032752a4</c> (entity-Avatar vftable, 11.19.0.10
    /// build), re-gates the chosen candidate's vftable dword, and anchors the
    /// dump at the candidate + 0x118 (the contiguous uint32 battle-stats
    /// quad +0x118/+0x11c/+0x120/+0x124 — property indices 0xA–0xD).
    /// <see cref="EntityRecordRegionReadRequest.AvatarCandidateIndex"/>
    /// selects which scan candidate to dump so the increment correlator can
    /// discriminate the own Avatar at scoring time (only the own counter
    /// increments on own-attacker events; other candidates stay flat as
    /// built-in control windows).
    /// </summary>
    AvatarStats = 3,

    /// <summary>
    /// The viewpoint-vehicle ownership walk (penetration v0.3, H1). Ignores
    /// <see cref="EntityRecordRegionReadRequest.EntityId"/>: the coordinator
    /// runs the gated vftable AOB scan for the unique VehicleGunRotator
    /// (<c>moduleBase + 0x32eeb40</c>), then the fixed five-read chain
    /// (rotator→owner→forward round-trip→gun vftable→entity HP) and returns
    /// only aggregate booleans/counts. No address or pointer leaves the
    /// coordinator. See docs/operations/pen-ownership-walk-proof-protocol.md.
    /// </summary>
    PenOwnershipWalk = 4,

    /// <summary>
    /// Phase 2–4 semantic-field snapshot (penetration v0.3). Reuses the
    /// ownership walk (process-local cached rotator after a confirmed walk,
    /// vftable re-validated under the lease; AOB on miss or mismatch), then
    /// two-pass reads the published gun-marker at rotator +0x50 and the
    /// VehicleGun reload/state block. Returns aggregate flags plus
    /// investigation yaw/pitch/enum diagnostics. No raw region bytes or
    /// addresses leave. See docs/operations/pen-weapon-semantic-fields.md.
    /// </summary>
    PenSemanticFields = 5,
}

/// <summary>
/// Bounded request for one entity record region dump (the L0 seam the
/// HP / facing / damage-dealt / replayTime live plans all consume). Only the
/// decoded entity id, a bounded region length (≤ 4096 bytes), and the
/// region anchor are caller-supplied; the coordinator owns process identity,
/// module, build layout, and the resolved record address. When
/// <paramref name="BattleSessionId"/> is supplied, the coordinator may
/// attest same-decoded-clock alignment from that session's replay-clock
/// segments; when null, <c>SameDecodedClockProven</c> is never claimed.
/// </summary>
public sealed record EntityRecordRegionReadRequest(
    int EntityId,
    int RegionLength,
    BattleSessionId? BattleSessionId = null,
    EntityRecordRegionAnchor RegionAnchor = EntityRecordRegionAnchor.RingRecord,
    int? AvatarCandidateIndex = null,
    int? OwnershipCandidateIndex = null)
{
    /// <summary>
    /// Maximum region size the L0 seam will read. 4 KB bounds the dump to a
    /// few entity records while keeping the guarded read atomic-enough for
    /// the correlators (the ring record itself is 0x38 bytes; the extra
    /// headroom covers adjacent per-entity records and the
    /// position/velocity/rotation candidates in one dump).
    /// </summary>
    public const int MaxLength = 4096;

    /// <summary>
    /// The entity-member offset to the tank record for
    /// <see cref="EntityRecordRegionAnchor.EntityTankRecord"/> dumps
    /// (Ghidra-candidate, test-local until live verification).
    /// </summary>
    public const int EntityTankRecordOffset = 0x3C;

    /// <summary>
    /// Maximum scan candidates the <see cref="EntityRecordRegionAnchor.AvatarStats"/>
    /// anchor will enumerate (mirrors the camera chain's MaxCandidates 4).
    /// </summary>
    public const int MaxAvatarCandidates = 4;

    /// <summary>
    /// The battle-stats quad offset on the entity-Avatar object for
    /// <see cref="EntityRecordRegionAnchor.AvatarStats"/> dumps
    /// (uint32 quad at +0x118/+0x11c/+0x120/+0x124, property indices
    /// 0xA–0xD — hash-bound L3 static finding, 11.19.0.10 build).
    /// </summary>
    public const int AvatarStatsQuadOffset = 0x118;

    /// <summary>
    /// Length of the battle-stats quad (four uint32 values).
    /// </summary>
    public const int AvatarStatsQuadLength = 0x10;

    /// <summary>
    /// Maximum scan candidates the <see cref="EntityRecordRegionAnchor.PenOwnershipWalk"/>
    /// anchor will enumerate (one rotator is expected; the cap bounds a
    /// hostile/ambiguous scan).
    /// </summary>
    public const int MaxOwnershipCandidates = 8;

    /// <summary>
    /// VehicleGunRotator primary vftable RVA (11.19.0.10, hash-bound
    /// <c>1cda5c31…</c>, see pen-ownership-walk-proof-protocol.md).
    /// </summary>
    public const uint VehicleGunRotatorVftableRva = 0x32eeb40;

    /// <summary>
    /// VehicleGun primary vftable RVA (11.19.0.10, hash-bound
    /// <c>1cda5c31…</c>, see pen-ownership-walk-proof-protocol.md).
    /// </summary>
    public const uint VehicleGunVftableRva = 0x32dacf4;

    /// <summary>
    /// VehicleGunRotator +0x10 stores its owner (the AvatarGameLogic object).
    /// </summary>
    public const int PenOwnershipRotatorOwnerOffset = 0x10;

    /// <summary>
    /// AvatarGameLogic +0x1fc stores the VehicleGunRotator (refptr).
    /// </summary>
    public const int PenOwnershipOwnerRotatorOffset = 0x1fc;

    /// <summary>
    /// AvatarGameLogic +0x204 stores the VehicleGun (raw pointer).
    /// </summary>
    public const int PenOwnershipOwnerGunOffset = 0x204;

    /// <summary>
    /// VehicleGameLogic +0x04 stores the entity (OD-068 slot +0x04 getter).
    /// </summary>
    public const int PenOwnershipOwnerEntityOffset = 0x04;

    /// <summary>
    /// Entity +0xB8 stores current HP as a signed int16 (OD-087/091, Verified).
    /// </summary>
    public const int EntityHealthOffset = 0xB8;

    /// <summary>
    /// Published GetGunMarkerPosition copy on VehicleGunRotator (pos3 + dir3
    /// + scalar). Hash-bound static derivation, unpromoted.
    /// </summary>
    public const int PenMarkerPublishedOffset = 0x50;

    /// <summary>Seven float32 values: xyz, direction xyz, range-like scalar.</summary>
    public const int PenMarkerPublishedLength = 28;

    /// <summary>VehicleGun reload/state enum (int32). Ctor 9 = unset; live 0..8.</summary>
    public const int PenGunReloadEnumOffset = 0x3C;

    /// <summary>VehicleGun reload progress float32.</summary>
    public const int PenGunReloadProgressOffset = 0x40;

    /// <summary>VehicleGun reload time float32.</summary>
    public const int PenGunReloadTimeOffset = 0x44;

    /// <summary>VehicleGun reload flag byte.</summary>
    public const int PenGunReloadFlagOffset = 0x4C;

    /// <summary>
    /// Entity-base hull yaw float32 (rotation triple +0x48/+0x4C/+0x50).
    /// </summary>
    public const int EntityHullYawOffset = 0x50;
}

/// <summary>
/// Privacy-safe result from one entity ring-record region dump: the raw
/// bytes + replay time ONLY. No absolute address, process id, or module
/// base ever leaves the coordinator.
/// </summary>
public sealed record EntityRecordRegionReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    Type10EntityPositionStatus Status,
    int EntityId,
    double? ReplayTimeSeconds,
    byte[]? RegionBytes,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted,
    bool EntityIdentityRevalidated,
    bool ConsistentDoubleRead,
    bool SameDecodedClockProven,
    int AvatarCandidateCount = 0,
    int PenOwnershipRotatorCandidateCount = 0,
    bool PenOwnershipOwnerPointerReadable = false,
    bool PenOwnershipForwardRoundTripConfirmed = false,
    bool PenOwnershipGunVtableConfirmed = false,
    bool PenOwnershipEntityHpPlausible = false,
    bool PenOwnershipTwoPassStable = false,
    bool PenSemanticReloadEnumInRange = false,
    bool PenSemanticMarkerFinite = false,
    bool PenSemanticMarkerDirectionUnit = false,
    bool PenSemanticTwoPassStable = false,
    int? PenSemanticReloadEnum = null,
    double? PenSemanticMarkerYawRadians = null,
    double? PenSemanticMarkerPitchRadians = null,
    double? PenSemanticHullYawRadians = null);

/// <summary>
/// One entity region in a batch read (mirrors the single-read fields).
/// When <see cref="EntityBaseRegionLength"/> is set (1..4096), the batch
/// ALSO reads that many bytes of the entity-base region for the same
/// entity (the L1 HP surface — current int16 +0xB8 / max +0x11C) under
/// the SAME resolve and the SAME single replay-clock attestation, so the
/// frame gets position + facing + health from one coherent moment without
/// doubling batch items past the 16-item cap (design:
/// docs/operations/live-frame-loop-design.md).
/// </summary>
public sealed record EntityRegionReadRequestItem(
    int EntityId,
    int RegionLength,
    EntityRecordRegionAnchor RegionAnchor = EntityRecordRegionAnchor.RingRecord,
    int? EntityBaseRegionLength = null);

/// <summary>
/// Batch request for up to <see cref="MaxEntities"/> entity region dumps in
/// one round trip (the per-frame live read surface design — see
/// docs/operations/batch-entity-read-design.md). The whole batch carries ONE
/// replay-clock attestation so a frame read gets a coherent timestamp, and
/// total region bytes are bounded by <see cref="MaxTotalBytes"/>.
/// </summary>
public sealed record EntityRegionsReadRequest(
    IReadOnlyList<EntityRegionReadRequestItem> Entities,
    BattleSessionId? BattleSessionId = null)
{
    /// <summary>Maximum entities per batch (safety cap; the frame read is 14).</summary>
    public const int MaxEntities = 16;

    /// <summary>Maximum total region bytes per batch (16 × the 4 KB single-read cap).</summary>
    public const int MaxTotalBytes = 16 * 1024;
}

/// <summary>
/// Outcome of one entity within a batch region read. The optional
/// <see cref="EntityBaseRegionBytes"/> (and its failure stage) cover the
/// L1 entity-base read when the request asked for one: an entity whose
/// primary region resolved but whose entity-base read failed keeps its
/// ring bytes and reports the entity-base failure separately — the frame
/// renders position/facing and leaves HP honest-null for that tank.
/// </summary>
public sealed record EntityRegionReadResultItem(
    int EntityId,
    Type10EntityPositionStatus Status,
    double? ReplayTimeSeconds,
    byte[]? RegionBytes,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted,
    bool EntityIdentityRevalidated,
    bool ConsistentDoubleRead,
    byte[]? EntityBaseRegionBytes = null,
    string? EntityBaseFailureStage = null,
    int EntityBaseAttempts = 0,
    int RegionReadAttempts = 0,
    bool RegionTearObserved = false,
    bool EntityBaseTearObserved = false);

/// <summary>
/// Wall-clock measurement of the batch read pass (the item-7 atomicity
/// groundwork: the verification window between the batch's first resolve
/// and last read, plus the replay-clock snapshot moment, quantifies how
/// "one coherent moment" the frame read is). Null when no reads happened
/// (gate-level batch outcomes). Durations are honest wall-clock spans, not
/// evidence claims.
/// </summary>
public sealed record EntityRegionsReadMeasurement(
    DateTimeOffset BatchStartedAtUtc,
    DateTimeOffset BatchEndedAtUtc,
    DateTimeOffset? ClockSnapshotAtUtc);

/// <summary>
/// Wall-clock measurement of one composed live frame (design:
/// docs/operations/live-frame-loop-design.md): the frame pass window from
/// the camera-anchor scan start through the camera-pose read end, plus the
/// ONE G2 replay-clock snapshot moment carried by the batch read (null when
/// no battle session id was supplied). Null when no reads happened
/// (gate-level frame outcomes). The window is the loop's per-frame timing
/// budget — the item-7 atomicity groundwork. Durations are honest
/// wall-clock spans, not evidence claims.
/// </summary>
public sealed record LiveFrameReadMeasurement(
    DateTimeOffset FrameStartedAtUtc,
    DateTimeOffset FrameEndedAtUtc,
    DateTimeOffset? ClockSnapshotAtUtc);

/// <summary>
/// Privacy-safe batch region read result: the raw bytes + ONE replay-time
/// label per batch. No absolute address, process id, or module base ever
/// leaves the coordinator. <see cref="Status"/> is the gate-level outcome
/// (<c>Resolved</c> when the read pass completed — inspect per-entity
/// statuses for individual entities); <c>ReplaySessionInactive</c> fails the
/// WHOLE batch (the pre-battle phase is global — a frame cannot be
/// half-timed). <see cref="Measurement"/> carries the read-pass window
/// when reads happened.
/// </summary>
public sealed record EntityRegionsReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    Type10EntityPositionStatus Status,
    double? ReplayTimeSeconds,
    bool SameDecodedClockProven,
    IReadOnlyList<EntityRegionReadResultItem> Regions,
    EntityRegionsReadMeasurement? Measurement = null);

/// <summary>
/// Privacy-safe result of a live roster enumeration (design:
/// docs/operations/live-roster-read-design.md): the avatar-family entity ids
/// ONLY — the coordinator consumes the resolved addresses internally and the
/// result carries ids plus the filter-precision counters so the live
/// rehearsal can cross-check the enumeration against the decoded roster.
/// <see cref="Status"/> is the gate-level outcome: <c>Resolved</c> when the
/// enumeration pass completed; <c>ReplaySessionInactive</c> is the retryable
/// pre-battle phase; <c>TraversalLimitExceeded</c> fails closed (a partial
/// roster is never served as the roster).
/// </summary>
public sealed record EntityRosterReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    Type10EntityPositionStatus Status,
    string? FailureStage,
    int CandidatesSeen,
    int FilteredOut,
    bool ModuleRooted,
    bool TraversalLimited,
    IReadOnlyList<int> EntityIds);

/// <summary>
/// One tank of a live frame: the avatar-family entity with its ring-record
/// position and hull yaw, when that entity resolved and its region decoded,
/// plus its health from the entity-base region when that read also
/// resolved. Position/yaw come from the ring-record region dump (+0x10 /
/// +0x30) via the pure <c>RingRecordRegion</c> decoder; health comes from
/// the entity-base region dump (+0xB8 current int16 / +0x11C max / +0xBA
/// alive) via the pure <c>EntityBaseRegion</c> decoder (L1 live-confirmed
/// by OD-RECOVERY-087). All health fields are honest nulls when the
/// entity-base read was not requested, failed, or decoded to an invalid
/// value — the HUD must never render a fabricated health value (design:
/// docs/operations/live-frame-loop-design.md). World coordinates only;
/// addresses never leave the coordinator.
/// </summary>
public sealed record LiveFrameTankState(
    int EntityId,
    Type10EntityPositionStatus Status,
    float? X,
    float? Y,
    float? Z,
    float? YawRadians,
    float? HpCurrent,
    float? HpMax,
    bool? Alive,
    string? FailureStage,
    bool ModuleRooted,
    string? HpFailureStage = null,
    long? DamageDealt = null);

/// <summary>
/// One composed live frame (design: docs/operations/live-frame-loop-design.md):
/// the roster enumeration (ids), the whole-roster batch region read with ONE
/// G2 clock attestation, and the CAM-001 camera pose, assembled by the
/// coordinator under the scan authorization. <see cref="Status"/> is the
/// gate-level outcome (Resolved = the frame pass completed; inspect per-tank
/// statuses for individual entities); a gate-level failure (build mismatch,
/// inactive phase, revoked authorization) fails the WHOLE frame so the HUD
/// never renders a half-timed frame. <see cref="SameDecodedClockProven"/>
/// is true only when a battle session id was supplied and the G2 bound held.
/// No absolute address, process id, or module base ever leaves the
/// coordinator.
/// </summary>
public sealed record LiveFrameReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    Type10EntityPositionStatus Status,
    string? FailureStage,
    double? ReplayTimeSeconds,
    bool SameDecodedClockProven,
    CameraPoseReadResult? Camera,
    IReadOnlyList<LiveFrameTankState> Tanks,
    int RosterCandidatesSeen,
    int RosterFilteredOut,
    LiveFrameReadMeasurement? Measurement = null);

/// <summary>
/// Request for one composed live frame. The optional battle session id
/// enables the ONE G2 replay-clock attestation for the frame (same
/// semantics as the batch surface): omitted never claims the flag. The
/// optional <see cref="OwnEntityId"/> (the decoded session's viewpoint
/// participant's entity id) enables the own-row damage-dealt consumption:
/// the coordinator reads the own Avatar's battle-stats dword0 (the G2
/// published chain) and attaches it to that row — honest, fail-closed
/// (any read failure leaves the row's DamageDealt null, never guessed).
/// </summary>
public sealed record LiveFrameReadRequest(
    BattleSessionId? BattleSessionId = null,
    long? OwnEntityId = null);

/// <summary>Outcome of one CAM-001 gate-free camera-pose walk.</summary>
public enum CameraPoseStatus
{
    /// <summary>Pose read with all hop identity gates passed.</summary>
    Resolved,

    /// <summary>The running executable does not match the pinned camera layout.</summary>
    UnsupportedBuild,

    /// <summary>The avatar vftable anchor was not found in memory.</summary>
    AnchorNotFound,

    /// <summary>A pointer hop read failed or a hop identity gate did not pass.</summary>
    ChainBroken,
}

/// <summary>
/// Privacy-safe result of one gate-verified GameCamera pose read: the world
/// position, yaw/pitch, and view basis plus the per-hop identity flags. The
/// addresses are diagnostic evidence (formatted as hex by the endpoint),
/// never runtime read offsets.
/// </summary>
public sealed record CameraPoseReadResult(
    DateTimeOffset CompletedAtUtc,
    string GameVersion,
    CameraPoseStatus Status,
    string? FailureStage,
    long AvatarAddress,
    long CameraAddress,
    long CameraStateAddress,
    float X,
    float Y,
    float Z,
    float YawRadians,
    float PitchRadians,
    float[] Basis,
    bool AvatarIdentityVerified,
    bool CameraIdentityVerified,
    bool CameraStateIdentityVerified,
    bool ConsistentDoubleRead,
    bool ModuleRooted);

/// <summary>
/// Bounded diagnostic request for the gate-verified position-page endpoint.
/// Only the decoded entity ID is caller-supplied; the process identity and
/// the resolved page address are coordinator-owned.
/// </summary>
public sealed record EntityPositionAddressRequest(int EntityId);

/// <summary>
/// Diagnostic result from the gate-verified position-page endpoint: the
/// ring-record address and its page for the requested entity. Deliberately a
/// separate record from <see cref="EntityPositionReadResult"/> so the poll
/// path never carries process locations; used only to arm the guard-page
/// interceptor on the exact page a poll reads.
/// </summary>
public sealed record EntityPositionAddressResult(
    Type10EntityPositionStatus Status,
    uint? RecordAddress,
    uint? PageAddress,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted);

/// <summary>
/// Bounded operator request for the server-owned instruction-first position
/// probe. No process identity or memory address is caller-controlled.
/// </summary>
public sealed record InstructionSnapshotRequest(
    int DurationMilliseconds = 5_000,
    int MaxHits = 64);

/// <summary>
/// One privacy-projected entity-id and XYZ read captured while the matching
/// debug event was held. Absolute process addresses remain inside
/// GameIntegration.
/// </summary>
public sealed record InstructionSnapshotHit(
    int Sequence,
    string ObjectKey,
    DateTimeOffset CapturedAtUtc,
    bool ReplayEntityIdReadOk,
    int? ReplayEntityId,
    bool ReadOk,
    bool Finite,
    float? X,
    float? Y,
    float? Z,
    bool SameDebugEvent,
    bool SingleRead12Bytes,
    bool ObjectRegisterCaptured,
    bool HardwareAtomicReadProven,
    bool SameDecodedClockProven,
    bool ViewpointIdentityProven,
    bool StableRootProven);

/// <summary>
/// Aggregate result of the instruction-first position probe. A successful
/// capture proves entity/vector register provenance only; semantic player
/// identity and a stable resolver require separate evidence.
/// </summary>
public sealed record InstructionSnapshotResult(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string Status,
    string TargetModule,
    long TargetRva,
    bool InstructionFingerprintMatched,
    bool CleanupProven,
    bool Truncated,
    IReadOnlyList<InstructionSnapshotHit> Hits);

/// <summary>Request to create a memory snapshot with value filters.</summary>
public sealed record MemorySnapshotRequest(
    int ValueSize,
    float? FloatMin,
    float? FloatMax,
    int? IntMin,
    int? IntMax,
    long MinAddress,
    // Exclusive upper address; zero means the supported user-space limit.
    long MaxAddress,
    MemoryValueKind ValueKind = MemoryValueKind.Int32Value,
    int Alignment = 1,
    MemoryRegionSelection RegionSelection = MemoryRegionSelection.Default,
    long? LongMin = null,
    long? LongMax = null,
    ulong? UIntMin = null,
    ulong? UIntMax = null,
    // Explicit retained-byte budget (0 means the engine ceiling of 512 MiB).
    // Bounded private/mapped campaigns use this instead of address windows so
    // no process-specific address selection is required.
    long MaxBytes = 0);

/// <summary>
/// Result of comparing a current scan against a stored snapshot. RetainedCount
/// reports prior candidates whose chunks could not be reread during a rolling
/// comparison; they are not included in the changed/unchanged counters.
/// </summary>
public sealed record MemoryCompareResult(
    DateTimeOffset CompletedAtUtc,
    int PreviousCount,
    int CurrentCount,
    int ChangedCount,
    int UnchangedCount,
    int IncreasedCount,
    int DecreasedCount,
    IReadOnlyList<MemoryScanCandidate> Candidates,
    bool Truncated = false,
    bool ComparedAgainstRollingBaseline = false,
    int RetainedCount = 0);

/// <summary>Request to scan a memory neighborhood around a known offset.</summary>
public sealed record MemoryNeighborhoodRequest(
    long ReferenceOffset,
    int WindowSize,
    bool IncludeFloat,
    bool IncludeInt32,
    bool IncludeDouble,
    float? FloatMin,
    float? FloatMax,
    int? IntMin,
    int? IntMax,
    bool IncludeWorkingSetClassification = false);

/// <summary>Safe result of a plain game process launch (no replay).</summary>
public sealed record GameProcessLaunchOutcome(
    int ProcessId,
    DateTimeOffset LaunchedAtUtc);

/// <summary>
/// Launches the installed game process without a replay.
/// Used for offset discovery and smoke testing where the game
/// needs to be running but no replay is required.
/// </summary>
public interface IGameProcessLauncher
{
    /// <summary>
    /// Starts the installed game executable as a new process.
    /// Returns the launched process ID on success, or an error.
    /// </summary>
    ValueTask<OperationResult<GameProcessLaunchOutcome>> LaunchAsync(
        CancellationToken cancellationToken);
}
