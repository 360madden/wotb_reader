namespace WotBTreader.GameHarness;

/// <summary>
/// Native platform capabilities. A capability is true only after an adapter has
/// verified it can fulfill the operation on the current machine.
/// </summary>
public sealed record GameHarnessCapabilities(
    bool ProcessDiscovery,
    bool ReplayLaunch,
    bool WindowCapture,
    bool WindowFocus,
    bool GuardedInput,
    bool NativeLogTail,
    bool LifecycleWait,
    string? CaptureBackend,
    string? UnavailableReason);

/// <summary>
/// Windows integrity level used to enforce the SendInput UIPI boundary.
/// </summary>
public enum ProcessIntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    System = 5,
}

/// <summary>
/// State derived from native Blitz replay lifecycle evidence.
/// </summary>
public enum ReplayLifecycleState
{
    Unknown = 0,
    NotRunning = 1,
    LaunchPending = 2,
    OfflineReplayActive = 3,
    OfflineReplayStopped = 4,
    OnlineBattle = 5,
    Ambiguous = 6,
}

/// <summary>
/// Current process, window, integrity, and native-log evidence.
/// Full executable paths are held only for exact comparison and must not be
/// written to application logs or command responses.
/// </summary>
public sealed record GameProcessObservation(
    bool IsRunning,
    int ProcessId,
    long WindowHandle,
    string ExecutablePath,
    string ExecutableVersion,
    string ExecutableSha256,
    bool IsForegroundWindow,
    ProcessIntegrityLevel GameIntegrity,
    ProcessIntegrityLevel HarnessIntegrity,
    int DpiX,
    int DpiY,
    ReplayLifecycleEvidence Lifecycle);

/// <summary>
/// Native-log evidence associated with a specific replay launch and process.
/// </summary>
public sealed record ReplayLifecycleEvidence(
    ReplayLifecycleState State,
    DateTimeOffset ObservedAtUtc,
    string LogWatermark,
    Guid? LaunchCorrelationId,
    int? ProcessId,
    string Source);

/// <summary>
/// Exact game identity expected by a guarded operation.
/// </summary>
public sealed record ExpectedGameIdentity(
    string ExecutablePath,
    string ExecutableVersion,
    string ExecutableSha256);

/// <summary>
/// Explicit, short-lived, one-shot operator intent to send replay-only input to
/// one observed process image.
/// </summary>
public sealed record InputArm(
    Guid ArmId,
    int ProcessId,
    string ExecutableSha256,
    DateTimeOffset ArmedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Allowlisted semantic replay keys. Adapters translate these values to fixed
/// virtual-key sequences; callers can never supply arbitrary keys.
/// </summary>
public enum ReplayKeyControl
{
    TogglePause,
    SpeedUp,
    SpeedDown,
    SeekForward,
    SeekBackward,
}

/// <summary>
/// Allowlisted semantic replay UI targets. Adapters resolve the target for the
/// verified build; callers can never supply screen coordinates.
/// </summary>
public enum ReplayClickControl
{
    PlayPause,
    SpeedUp,
    SpeedDown,
    TimelineForward,
    TimelineBackward,
}

/// <summary>
/// Requested capture backend preference.
/// </summary>
public enum CaptureBackendPreference
{
    WindowsGraphicsCapture,
    DesktopDuplicationFallback,
}

public sealed record ReplayLaunchRequest(
    string ReplayPath,
    string ReplaySha256,
    TimeSpan Timeout);

public sealed record ReplayLaunchResult(
    Guid LaunchCorrelationId,
    int ProcessId,
    DateTimeOffset RequestedAtUtc);

public sealed record WindowCaptureRequest(
    int ProcessId,
    long WindowHandle,
    string OutputDirectory,
    string FileStem,
    CaptureBackendPreference PreferredBackend,
    TimeSpan Timeout);

public sealed record WindowCaptureResult(
    string FileName,
    string PngSha256,
    int PixelWidth,
    int PixelHeight,
    string Backend,
    DateTimeOffset CapturedAtUtc);

public sealed record LogTailRequest(string? AfterWatermark, int MaximumEvents, TimeSpan Timeout);

public sealed record NativeReplayLogEvent(
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    string EventType,
    ReplayLifecycleState State,
    string Watermark,
    Guid? LaunchCorrelationId);

public sealed record ReplayStateWaitRequest(
    ReplayLifecycleState ExpectedState,
    Guid? LaunchCorrelationId,
    TimeSpan Timeout);

public sealed record ReplayStateWaitResult(
    bool Matched,
    ReplayLifecycleEvidence Evidence,
    TimeSpan Elapsed);

public sealed record PlatformOperationResult(bool Succeeded, string? StableErrorCode, string? Message);

public sealed record HarnessProbeData(
    GameHarnessCapabilities Capabilities,
    bool GameRunning,
    int? ProcessId,
    string? ExecutableFileName,
    string? ExecutableVersion,
    string? ExecutableSha256,
    long? WindowHandle,
    bool? Foreground,
    bool? IntegrityEqual,
    ReplayLifecycleState ReplayState,
    DateTimeOffset? ReplayStateObservedAtUtc,
    string? LogWatermark);
