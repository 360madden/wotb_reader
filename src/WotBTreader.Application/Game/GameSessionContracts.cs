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

/// <summary>Reads the current safe game-session state.</summary>
public interface IGameSessionState
{
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

/// <summary>Launches managed replay artifacts through the verified game adapter.</summary>
public interface IGameReplayLauncher
{
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
/// Returns safe observations without exposing process identity, handles,
/// authorization leases, offsets, or attachment operations.
/// </summary>
public interface IGameMemoryObserver
{
    ValueTask<GameMemoryObservation> ObserveAsync(
        CancellationToken cancellationToken);
}
