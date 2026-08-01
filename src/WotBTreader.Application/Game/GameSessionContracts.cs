using WotBTreader.Application.Results;
using WotBTreader.Core;

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
public sealed record GameReplayLaunchRequest(SourceArtifactId SourceArtifactId);

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
/// Returns <see cref="GameMemoryObservationAvailability.Unsupported"/>
/// when the offline-session gate is not satisfied.
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
        bool advanceBaseline = false);

    /// <summary>Discards a stored snapshot session.</summary>
    void DiscardSession(string sessionId);

    /// <summary>Reads a window of memory around a known offset and reports all plausible values.</summary>
    ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
        MemoryNeighborhoodRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Request to create a memory snapshot with value filters.</summary>
public sealed record MemorySnapshotRequest(
    int ValueSize,
    float? FloatMin,
    float? FloatMax,
    int? IntMin,
    int? IntMax,
    long MinAddress,
    long MaxAddress,
    MemoryValueKind ValueKind = MemoryValueKind.Int32Value,
    int Alignment = 1,
    MemoryRegionSelection RegionSelection = MemoryRegionSelection.Default,
    long? LongMin = null,
    long? LongMax = null,
    ulong? UIntMin = null,
    ulong? UIntMax = null);

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
