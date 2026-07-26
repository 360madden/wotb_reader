namespace WotBTreader.GameHarness;

/// <summary>
/// Narrow, tool-owned boundary for Windows/game integration. Implementations
/// must obtain process identity from trusted OS handles, derive replay state
/// from native logs, and translate only semantic allowlisted controls.
/// </summary>
public interface IGameHarnessPlatform
{
    /// <summary>Returns capabilities verified on the current host.</summary>
    GameHarnessCapabilities Capabilities { get; }

    /// <summary>Discovers the configured WotB process and its current evidence.</summary>
    ValueTask<GameProcessObservation?> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Launches a replay through the configured, verified installation.</summary>
    ValueTask<ReplayLaunchResult> LaunchReplayAsync(
        ReplayLaunchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Captures a verified WotB HWND as a PNG.</summary>
    ValueTask<WindowCaptureResult> CaptureWindowAsync(
        WindowCaptureRequest request,
        CancellationToken cancellationToken);

    /// <summary>Requests foreground focus for the verified WotB window.</summary>
    ValueTask<PlatformOperationResult> FocusWindowAsync(
        int processId,
        long windowHandle,
        CancellationToken cancellationToken);

    /// <summary>Sends one fixed replay key operation.</summary>
    ValueTask<PlatformOperationResult> SendReplayKeyAsync(
        int processId,
        long windowHandle,
        ReplayKeyControl control,
        CancellationToken cancellationToken);

    /// <summary>Clicks one fixed replay UI target.</summary>
    ValueTask<PlatformOperationResult> ClickReplayControlAsync(
        int processId,
        long windowHandle,
        ReplayClickControl control,
        CancellationToken cancellationToken);

    /// <summary>Reads bounded native replay lifecycle events.</summary>
    ValueTask<IReadOnlyList<NativeReplayLogEvent>> TailReplayLogAsync(
        LogTailRequest request,
        CancellationToken cancellationToken);

    /// <summary>Waits once, with a bounded timeout, for a replay lifecycle state.</summary>
    ValueTask<ReplayStateWaitResult> WaitForReplayStateAsync(
        ReplayStateWaitRequest request,
        CancellationToken cancellationToken);
}
