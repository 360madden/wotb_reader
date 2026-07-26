namespace WotBTreader.GameHarness;

/// <summary>
/// Fail-closed adapter used until the reviewed GameIntegration/Win32 binding is
/// available. It intentionally never simulates discovery, capture, or input.
/// </summary>
public sealed class UnavailableGameHarnessPlatform : IGameHarnessPlatform
{
    private const string Reason =
        "Native game integration is unavailable in this build; no game operation was attempted.";

    public GameHarnessCapabilities Capabilities { get; } = new(
        ProcessDiscovery: false,
        ReplayLaunch: false,
        WindowCapture: false,
        WindowFocus: false,
        GuardedInput: false,
        NativeLogTail: false,
        LifecycleWait: false,
        CaptureBackend: null,
        UnavailableReason: Reason);

    public ValueTask<GameProcessObservation?> ProbeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<GameProcessObservation?>(null);

    public ValueTask<ReplayLaunchResult> LaunchReplayAsync(
        ReplayLaunchRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<ReplayLaunchResult>(Unavailable());

    public ValueTask<WindowCaptureResult> CaptureWindowAsync(
        WindowCaptureRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<WindowCaptureResult>(Unavailable());

    public ValueTask<PlatformOperationResult> FocusWindowAsync(
        int processId,
        long windowHandle,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Failed());

    public ValueTask<PlatformOperationResult> SendReplayKeyAsync(
        int processId,
        long windowHandle,
        ReplayKeyControl control,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Failed());

    public ValueTask<PlatformOperationResult> ClickReplayControlAsync(
        int processId,
        long windowHandle,
        ReplayClickControl control,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Failed());

    public ValueTask<IReadOnlyList<NativeReplayLogEvent>> TailReplayLogAsync(
        LogTailRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<NativeReplayLogEvent>>([]);

    public ValueTask<ReplayStateWaitResult> WaitForReplayStateAsync(
        ReplayStateWaitRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<ReplayStateWaitResult>(Unavailable());

    private static NotSupportedException Unavailable() => new(Reason);

    private static PlatformOperationResult Failed() =>
        new(false, "harness.capability_unavailable", Reason);
}
