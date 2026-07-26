namespace WotBTreader.GameHarness;

/// <summary>
/// Common guard data for capture and replay-only window operations.
/// </summary>
public sealed record GuardedOperationContext(
    Guid CorrelationId,
    ExpectedGameIdentity ExpectedIdentity,
    Guid LaunchCorrelationId,
    InputArm? Arm,
    bool DryRun,
    string CaptureOutputDirectory,
    TimeSpan Timeout,
    string? ReplaySha256);

public sealed record GuardedActionData(
    string Command,
    bool Executed,
    int ProcessId,
    long WindowHandle,
    ScreenshotAuditEvidence? Before,
    ScreenshotAuditEvidence? After);

public sealed record OfflineReplayScenarioRequest(
    Guid CorrelationId,
    ReplayLaunchRequest Launch,
    ExpectedGameIdentity ExpectedIdentity,
    string CaptureOutputDirectory,
    TimeSpan Timeout);

public sealed record OfflineReplayScenarioData(
    Guid LaunchCorrelationId,
    int ProcessId,
    ReplayLifecycleEvidence Lifecycle,
    ScreenshotAuditEvidence Screenshot);

/// <summary>
/// Implements bounded orchestration and all safety decisions independently of
/// Win32 details. The native adapter is never called for input until the full
/// safety policy passes, a one-shot arm is consumed, and a pre-action capture
/// succeeds.
/// </summary>
public sealed class GameHarnessService : IDisposable
{
    private const string AuditSchemaVersion = "wotb-treader.game-harness-audit/v1";
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);

    private readonly IGameHarnessPlatform _platform;
    private readonly IHarnessAuditSink _auditSink;
    private readonly HarnessSafetyPolicy _safetyPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _armLock = new();
    private readonly HashSet<Guid> _consumedArms = [];

    public GameHarnessService(
        IGameHarnessPlatform platform,
        IHarnessAuditSink auditSink,
        HarnessSafetyPolicy? safetyPolicy = null,
        TimeProvider? timeProvider = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _safetyPolicy = safetyPolicy ?? new HarnessSafetyPolicy(timeProvider: _timeProvider);
    }

    public async ValueTask<HarnessCommandResult> ProbeAsync(
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!_platform.Capabilities.ProcessDiscovery)
        {
            var unavailable = new HarnessProbeData(
                _platform.Capabilities,
                GameRunning: false,
                ProcessId: null,
                ExecutableFileName: null,
                ExecutableVersion: null,
                ExecutableSha256: null,
                WindowHandle: null,
                Foreground: null,
                IntegrityEqual: null,
                ReplayState: ReplayLifecycleState.Unknown,
                ReplayStateObservedAtUtc: null,
                LogWatermark: null);
            return Success(correlationId, unavailable);
        }

        var observation = await _platform.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var data = observation is null
            ? new HarnessProbeData(
                _platform.Capabilities,
                GameRunning: false,
                ProcessId: null,
                ExecutableFileName: null,
                ExecutableVersion: null,
                ExecutableSha256: null,
                WindowHandle: null,
                Foreground: null,
                IntegrityEqual: null,
                ReplayState: ReplayLifecycleState.NotRunning,
                ReplayStateObservedAtUtc: null,
                LogWatermark: null)
            : new HarnessProbeData(
                _platform.Capabilities,
                observation.IsRunning,
                observation.ProcessId,
                SafeFileName(observation.ExecutablePath),
                observation.ExecutableVersion,
                observation.ExecutableSha256,
                observation.WindowHandle,
                observation.IsForegroundWindow,
                observation.GameIntegrity != ProcessIntegrityLevel.Unknown
                    && observation.GameIntegrity == observation.HarnessIntegrity,
                observation.Lifecycle.State,
                observation.Lifecycle.ObservedAtUtc,
                observation.Lifecycle.LogWatermark);

        return Success(correlationId, data);
    }

    public async ValueTask<HarnessCommandResult> LaunchReplayAsync(
        Guid correlationId,
        ReplayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateLaunchRequest(request);
        if (validation is not null)
        {
            return Failure(
                correlationId,
                HarnessExitCode.InvalidInput,
                validation.Value.Code,
                validation.Value.Message);
        }

        if (!_platform.Capabilities.ReplayLaunch)
        {
            await AppendAuditAsync(
                Audit(
                    correlationId,
                    "launch-replay",
                    timeout: request.Timeout,
                    replaySha256: request.ReplaySha256,
                    validationCode: "harness.capability_unavailable",
                    succeeded: false,
                    resultCode: "harness.capability_unavailable"),
                cancellationToken).ConfigureAwait(false);
            return Unsupported(correlationId, _platform.Capabilities.UnavailableReason);
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy(correlationId);
        }

        try
        {
            using var timeout = CreateTimeout(request.Timeout, cancellationToken);
            var result = await _platform.LaunchReplayAsync(request, timeout.Token).ConfigureAwait(false);
            await AppendAuditAsync(
                Audit(
                    correlationId,
                    "launch-replay",
                    processId: result.ProcessId,
                    timeout: request.Timeout,
                    replaySha256: request.ReplaySha256,
                    expectedState: ReplayLifecycleState.OfflineReplayActive.ToString(),
                    validationCode: "harness.launch_requested",
                    succeeded: true,
                    resultCode: "harness.launch_requested"),
                cancellationToken).ConfigureAwait(false);
            return Success(correlationId, result);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ValueTask<HarnessCommandResult> CaptureWindowAsync(
        GuardedOperationContext context,
        CancellationToken cancellationToken) =>
        RunCaptureAsync(context, cancellationToken);

    public ValueTask<HarnessCommandResult> FocusAsync(
        GuardedOperationContext context,
        CancellationToken cancellationToken) =>
        RunGuardedActionAsync(
            "focus",
            context,
            requireInputCapability: false,
            static (platform, observation, cancellationToken) =>
                platform.FocusWindowAsync(
                    observation.ProcessId,
                    observation.WindowHandle,
                    cancellationToken),
            cancellationToken);

    public ValueTask<HarnessCommandResult> SendKeyAsync(
        GuardedOperationContext context,
        ReplayKeyControl control,
        CancellationToken cancellationToken) =>
        RunGuardedActionAsync(
            "send-key:" + control,
            context,
            requireInputCapability: true,
            (platform, observation, actionCancellationToken) =>
                platform.SendReplayKeyAsync(
                    observation.ProcessId,
                    observation.WindowHandle,
                    control,
                    actionCancellationToken),
            cancellationToken);

    public ValueTask<HarnessCommandResult> ClickAsync(
        GuardedOperationContext context,
        ReplayClickControl control,
        CancellationToken cancellationToken) =>
        RunGuardedActionAsync(
            "click:" + control,
            context,
            requireInputCapability: true,
            (platform, observation, actionCancellationToken) =>
                platform.ClickReplayControlAsync(
                    observation.ProcessId,
                    observation.WindowHandle,
                    control,
                    actionCancellationToken),
            cancellationToken);

    public async ValueTask<HarnessCommandResult> TailLogAsync(
        Guid correlationId,
        LogTailRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MaximumEvents is < 1 or > 1_000
            || !IsValidTimeout(request.Timeout))
        {
            return Failure(
                correlationId,
                HarnessExitCode.InvalidInput,
                "harness.invalid_tail_request",
                "Maximum events must be 1-1000 and timeout must be positive and bounded.");
        }

        if (!_platform.Capabilities.NativeLogTail)
        {
            return Unsupported(correlationId, _platform.Capabilities.UnavailableReason);
        }

        using var timeout = CreateTimeout(request.Timeout, cancellationToken);
        var events = await _platform.TailReplayLogAsync(request, timeout.Token).ConfigureAwait(false);
        return Success(correlationId, events);
    }

    public async ValueTask<HarnessCommandResult> WaitForStateAsync(
        Guid correlationId,
        ReplayStateWaitRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidWaitState(request.ExpectedState) || !IsValidTimeout(request.Timeout))
        {
            return Failure(
                correlationId,
                HarnessExitCode.InvalidInput,
                "harness.invalid_wait_request",
                "Expected state is not allowlisted or timeout is not positive and bounded.");
        }

        if (!_platform.Capabilities.LifecycleWait)
        {
            return Unsupported(correlationId, _platform.Capabilities.UnavailableReason);
        }

        using var timeout = CreateTimeout(request.Timeout, cancellationToken);
        var result = await _platform.WaitForReplayStateAsync(request, timeout.Token).ConfigureAwait(false);
        if (!result.Matched)
        {
            return Failure(
                correlationId,
                HarnessExitCode.ConflictOrBusy,
                "harness.wait_timeout",
                "The expected replay state was not observed before the bounded timeout.",
                retryable: true,
                data: result);
        }

        return Success(correlationId, result);
    }

    public async ValueTask<HarnessCommandResult> RunOfflineReplaySmokeScenarioAsync(
        OfflineReplayScenarioRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var launchValidation = ValidateLaunchRequest(request.Launch);
        if (launchValidation is not null || !IsValidTimeout(request.Timeout))
        {
            return Failure(
                request.CorrelationId,
                HarnessExitCode.InvalidInput,
                launchValidation?.Code ?? "harness.invalid_timeout",
                launchValidation?.Message ?? "Scenario timeout is not positive and bounded.");
        }

        if (!_platform.Capabilities.ReplayLaunch
            || !_platform.Capabilities.LifecycleWait
            || !_platform.Capabilities.ProcessDiscovery
            || !_platform.Capabilities.WindowCapture)
        {
            return Unsupported(request.CorrelationId, _platform.Capabilities.UnavailableReason);
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy(request.CorrelationId);
        }

        try
        {
            using var timeout = CreateTimeout(request.Timeout, cancellationToken);
            var launch = await _platform.LaunchReplayAsync(
                request.Launch,
                timeout.Token).ConfigureAwait(false);
            var wait = await _platform.WaitForReplayStateAsync(
                new ReplayStateWaitRequest(
                    ReplayLifecycleState.OfflineReplayActive,
                    launch.LaunchCorrelationId,
                    request.Timeout),
                timeout.Token).ConfigureAwait(false);
            if (!wait.Matched)
            {
                await AppendAuditAsync(
                    Audit(
                        request.CorrelationId,
                        "run-scenario:offline-replay-smoke",
                        processId: launch.ProcessId,
                        timeout: request.Timeout,
                        replaySha256: request.Launch.ReplaySha256,
                        expectedState: ReplayLifecycleState.OfflineReplayActive.ToString(),
                        logWatermark: wait.Evidence.LogWatermark,
                        validationCode: "harness.wait_timeout",
                        succeeded: false,
                        resultCode: "harness.wait_timeout"),
                    cancellationToken).ConfigureAwait(false);
                return Failure(
                    request.CorrelationId,
                    HarnessExitCode.ConflictOrBusy,
                    "harness.wait_timeout",
                    "The offline replay lifecycle marker was not observed.",
                    retryable: true);
            }

            var observation = await _platform.ProbeAsync(timeout.Token).ConfigureAwait(false);
            var decision = _safetyPolicy.Evaluate(
                observation,
                request.ExpectedIdentity,
                launch.LaunchCorrelationId,
                arm: null,
                requireArm: false);
            if (!decision.Allowed || observation is null)
            {
                await AppendAuditAsync(
                    AuditFromDecision(
                        request.CorrelationId,
                        "run-scenario:offline-replay-smoke",
                        observation,
                        request.Timeout,
                        request.Launch.ReplaySha256,
                        decision),
                    cancellationToken).ConfigureAwait(false);
                return SafetyDenied(request.CorrelationId, decision);
            }

            var screenshot = await _platform.CaptureWindowAsync(
                CreateCaptureRequest(
                    request.CorrelationId,
                    "scenario",
                    request.CaptureOutputDirectory,
                    request.Timeout,
                    observation),
                timeout.Token).ConfigureAwait(false);
            var screenshotEvidence = screenshot.ToAuditEvidence();
            await AppendAuditAsync(
                Audit(
                    request.CorrelationId,
                    "run-scenario:offline-replay-smoke",
                    observation,
                    request.Timeout,
                    request.Launch.ReplaySha256,
                    "harness.safety_passed",
                    succeeded: true,
                    resultCode: "harness.scenario_passed",
                    before: screenshotEvidence),
                cancellationToken).ConfigureAwait(false);
            return Success(
                request.CorrelationId,
                new OfflineReplayScenarioData(
                    launch.LaunchCorrelationId,
                    observation.ProcessId,
                    observation.Lifecycle,
                    screenshotEvidence));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        _operationGate.Dispose();
    }

    private async ValueTask<HarnessCommandResult> RunCaptureAsync(
        GuardedOperationContext context,
        CancellationToken cancellationToken)
    {
        var invalid = ValidateContext(context, requireArm: false);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!_platform.Capabilities.ProcessDiscovery || !_platform.Capabilities.WindowCapture)
        {
            return Unsupported(context.CorrelationId, _platform.Capabilities.UnavailableReason);
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy(context.CorrelationId);
        }

        try
        {
            using var timeout = CreateTimeout(context.Timeout, cancellationToken);
            var observation = await _platform.ProbeAsync(timeout.Token).ConfigureAwait(false);
            var decision = _safetyPolicy.Evaluate(
                observation,
                context.ExpectedIdentity,
                context.LaunchCorrelationId,
                arm: null,
                requireArm: false);
            if (!decision.Allowed || observation is null)
            {
                await AppendAuditAsync(
                    AuditFromDecision(
                        context.CorrelationId,
                        "capture-window",
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        decision),
                    cancellationToken).ConfigureAwait(false);
                return SafetyDenied(context.CorrelationId, decision);
            }

            if (context.DryRun)
            {
                await AppendAuditAsync(
                    Audit(
                        context.CorrelationId,
                        "capture-window",
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        "harness.safety_passed",
                        succeeded: true,
                        resultCode: "harness.dry_run"),
                    cancellationToken).ConfigureAwait(false);
                return Success(
                    context.CorrelationId,
                    new GuardedActionData(
                        "capture-window",
                        Executed: false,
                        observation.ProcessId,
                        observation.WindowHandle,
                        Before: null,
                        After: null),
                    ["Dry run: no screenshot was captured."]);
            }

            var result = await _platform.CaptureWindowAsync(
                CreateCaptureRequest(
                    context.CorrelationId,
                    "capture",
                    context.CaptureOutputDirectory,
                    context.Timeout,
                    observation),
                timeout.Token).ConfigureAwait(false);
            var evidence = result.ToAuditEvidence();
            await AppendAuditAsync(
                Audit(
                    context.CorrelationId,
                    "capture-window",
                    observation,
                    context.Timeout,
                    context.ReplaySha256,
                    "harness.safety_passed",
                    succeeded: true,
                    resultCode: "harness.capture_succeeded",
                    before: evidence),
                cancellationToken).ConfigureAwait(false);
            return Success(context.CorrelationId, evidence);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<HarnessCommandResult> RunGuardedActionAsync(
        string command,
        GuardedOperationContext context,
        bool requireInputCapability,
        Func<
            IGameHarnessPlatform,
            GameProcessObservation,
            CancellationToken,
            ValueTask<PlatformOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        var invalid = ValidateContext(context, requireArm: !context.DryRun);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!_platform.Capabilities.ProcessDiscovery
            || !_platform.Capabilities.WindowCapture
            || !_platform.Capabilities.WindowFocus
            || (requireInputCapability && !_platform.Capabilities.GuardedInput))
        {
            return Unsupported(context.CorrelationId, _platform.Capabilities.UnavailableReason);
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Busy(context.CorrelationId);
        }

        try
        {
            using var timeout = CreateTimeout(context.Timeout, cancellationToken);
            var observation = await _platform.ProbeAsync(timeout.Token).ConfigureAwait(false);
            var decision = _safetyPolicy.Evaluate(
                observation,
                context.ExpectedIdentity,
                context.LaunchCorrelationId,
                context.Arm,
                requireArm: !context.DryRun);
            if (!decision.Allowed || observation is null)
            {
                await AppendAuditAsync(
                    AuditFromDecision(
                        context.CorrelationId,
                        command,
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        decision),
                    cancellationToken).ConfigureAwait(false);
                return SafetyDenied(context.CorrelationId, decision);
            }

            if (context.DryRun)
            {
                await AppendAuditAsync(
                    Audit(
                        context.CorrelationId,
                        command,
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        "harness.safety_passed",
                        succeeded: true,
                        resultCode: "harness.dry_run"),
                    cancellationToken).ConfigureAwait(false);
                return Success(
                    context.CorrelationId,
                    new GuardedActionData(
                        command,
                        Executed: false,
                        observation.ProcessId,
                        observation.WindowHandle,
                        Before: null,
                        After: null),
                    ["Dry run: no focus or input action was sent."]);
            }

            if (context.Arm is null || !TryConsumeArm(context.Arm.ArmId))
            {
                var armDecision = HarnessSafetyDecision.Deny(
                    "harness.arm_already_consumed",
                    "The one-shot input arm has already been used.");
                await AppendAuditAsync(
                    AuditFromDecision(
                        context.CorrelationId,
                        command,
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        armDecision),
                    cancellationToken).ConfigureAwait(false);
                return SafetyDenied(context.CorrelationId, armDecision);
            }

            var before = await _platform.CaptureWindowAsync(
                CreateCaptureRequest(
                    context.CorrelationId,
                    command,
                    context.CaptureOutputDirectory,
                    context.Timeout,
                    observation,
                    suffix: "before"),
                timeout.Token).ConfigureAwait(false);
            var beforeEvidence = before.ToAuditEvidence();

            // Persist intent and pre-action evidence before the single native
            // action. A crash can therefore never erase the fact that input was
            // prepared for this exact process and log watermark.
            await AppendAuditAsync(
                Audit(
                    context.CorrelationId,
                    command,
                    observation,
                    context.Timeout,
                    context.ReplaySha256,
                    "harness.safety_passed",
                    succeeded: false,
                    resultCode: "harness.action_prepared",
                    before: beforeEvidence),
                cancellationToken).ConfigureAwait(false);

            var actionResult = await operation(_platform, observation, timeout.Token).ConfigureAwait(false);
            if (!actionResult.Succeeded)
            {
                var errorCode = IsStableCode(actionResult.StableErrorCode)
                    ? actionResult.StableErrorCode!
                    : "harness.platform_action_failed";
                await AppendAuditAsync(
                    Audit(
                        context.CorrelationId,
                        command,
                        observation,
                        context.Timeout,
                        context.ReplaySha256,
                        "harness.safety_passed",
                        succeeded: false,
                        resultCode: errorCode,
                        before: beforeEvidence),
                    cancellationToken).ConfigureAwait(false);
                return Failure(
                    context.CorrelationId,
                    HarnessExitCode.ConflictOrBusy,
                    errorCode,
                    "The guarded platform operation did not succeed.",
                    retryable: false);
            }

            var after = await _platform.CaptureWindowAsync(
                CreateCaptureRequest(
                    context.CorrelationId,
                    command,
                    context.CaptureOutputDirectory,
                    context.Timeout,
                    observation,
                    suffix: "after"),
                timeout.Token).ConfigureAwait(false);
            var afterEvidence = after.ToAuditEvidence();
            await AppendAuditAsync(
                Audit(
                    context.CorrelationId,
                    command,
                    observation,
                    context.Timeout,
                    context.ReplaySha256,
                    "harness.safety_passed",
                    succeeded: true,
                    resultCode: "harness.action_succeeded",
                    before: beforeEvidence,
                    after: afterEvidence),
                cancellationToken).ConfigureAwait(false);
            return Success(
                context.CorrelationId,
                new GuardedActionData(
                    command,
                    Executed: true,
                    observation.ProcessId,
                    observation.WindowHandle,
                    beforeEvidence,
                    afterEvidence));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static HarnessCommandResult? ValidateContext(
        GuardedOperationContext context,
        bool requireArm)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.ExpectedIdentity);

        if (context.CorrelationId == Guid.Empty
            || context.LaunchCorrelationId == Guid.Empty
            || !IsValidTimeout(context.Timeout)
            || string.IsNullOrWhiteSpace(context.CaptureOutputDirectory)
            || string.IsNullOrWhiteSpace(context.ExpectedIdentity.ExecutablePath)
            || string.IsNullOrWhiteSpace(context.ExpectedIdentity.ExecutableVersion)
            || !IsSha256(context.ExpectedIdentity.ExecutableSha256))
        {
            return Failure(
                context.CorrelationId,
                HarnessExitCode.InvalidInput,
                "harness.invalid_guard_context",
                "Guard context identity, correlation, capture path, or timeout is invalid.");
        }

        if (requireArm && context.Arm is null)
        {
            return Failure(
                context.CorrelationId,
                HarnessExitCode.InvalidInput,
                "harness.input_not_armed",
                "Game input is disabled until explicitly armed for one process.");
        }

        return null;
    }

    private static (string Code, string Message)? ValidateLaunchRequest(
        ReplayLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReplayPath)
            || !string.Equals(
                Path.GetExtension(request.ReplayPath),
                ".wotbreplay",
                StringComparison.OrdinalIgnoreCase))
        {
            return (
                "harness.invalid_replay_path",
                "Replay path must name a .wotbreplay file.");
        }

        if (!IsSha256(request.ReplaySha256))
        {
            return (
                "harness.invalid_replay_hash",
                "Replay SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (!IsValidTimeout(request.Timeout))
        {
            return (
                "harness.invalid_timeout",
                "Timeout must be positive and no longer than two minutes.");
        }

        return null;
    }

    private static bool IsValidTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout <= MaximumTimeout;

    private static bool IsValidWaitState(ReplayLifecycleState state) =>
        state is ReplayLifecycleState.OfflineReplayActive
            or ReplayLifecycleState.OfflineReplayStopped
            or ReplayLifecycleState.NotRunning;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsStableCode(string? value) =>
        value is not null
        && value.StartsWith("harness.", StringComparison.Ordinal)
        && value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character is '.' or '_');

    private bool TryConsumeArm(Guid armId)
    {
        lock (_armLock)
        {
            return _consumedArms.Add(armId);
        }
    }

    private static CancellationTokenSource CreateTimeout(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // CancelAfter has no TimeProvider overload; the wall-clock timer is
        // acceptable because guarded timeouts are bounded to two minutes.
        source.CancelAfter(timeout);
        return source;
    }

    private static WindowCaptureRequest CreateCaptureRequest(
        Guid correlationId,
        string command,
        string outputDirectory,
        TimeSpan timeout,
        GameProcessObservation observation,
        string? suffix = null)
    {
        var safeCommand = string.Concat(
            command.Select(character =>
                char.IsAsciiLetterOrDigit(character) ? character : '-'));
        var fileStem = string.Join(
            '-',
            new[]
            {
                correlationId.ToString("N"),
                safeCommand,
                suffix,
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new WindowCaptureRequest(
            observation.ProcessId,
            observation.WindowHandle,
            outputDirectory,
            fileStem,
            CaptureBackendPreference.WindowsGraphicsCapture,
            timeout);
    }

    private HarnessActionAuditRecord AuditFromDecision(
        Guid correlationId,
        string command,
        GameProcessObservation? observation,
        TimeSpan timeout,
        string? replaySha256,
        HarnessSafetyDecision decision) =>
        Audit(
            correlationId,
            command,
            observation,
            timeout,
            replaySha256,
            decision.StableCode,
            succeeded: false,
            resultCode: decision.StableCode);

    private HarnessActionAuditRecord Audit(
        Guid correlationId,
        string command,
        GameProcessObservation? observation = null,
        TimeSpan timeout = default,
        string? replaySha256 = null,
        string validationCode = "harness.not_evaluated",
        bool succeeded = false,
        string? resultCode = null,
        ScreenshotAuditEvidence? before = null,
        ScreenshotAuditEvidence? after = null,
        int? processId = null,
        string? expectedState = null,
        string? logWatermark = null) =>
        Audit(
            correlationId,
            command,
            processId ?? observation?.ProcessId,
            observation?.ExecutableSha256,
            replaySha256,
            observation?.WindowHandle,
            observation?.DpiX,
            observation?.DpiY,
            logWatermark ?? observation?.Lifecycle.LogWatermark,
            timeout,
            expectedState ?? ReplayLifecycleState.OfflineReplayActive.ToString(),
            validationCode,
            succeeded,
            resultCode,
            before,
            after);

    private HarnessActionAuditRecord Audit(
        Guid correlationId,
        string command,
        int? processId,
        string? executableSha256,
        string? replaySha256,
        long? windowHandle,
        int? dpiX,
        int? dpiY,
        string? logWatermark,
        TimeSpan timeout,
        string? expectedState,
        string validationCode,
        bool succeeded,
        string? resultCode,
        ScreenshotAuditEvidence? before,
        ScreenshotAuditEvidence? after) =>
        new(
            AuditSchemaVersion,
            Guid.CreateVersion7(_timeProvider.GetUtcNow()),
            _timeProvider.GetUtcNow(),
            correlationId,
            command,
            processId,
            executableSha256,
            replaySha256,
            windowHandle,
            dpiX,
            dpiY,
            logWatermark,
            (long)timeout.TotalMilliseconds,
            expectedState,
            validationCode,
            succeeded,
            resultCode,
            before,
            after);

    private ValueTask AppendAuditAsync(
        HarnessActionAuditRecord record,
        CancellationToken cancellationToken) =>
        _auditSink.AppendAsync(record, cancellationToken);

    private static string? SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static HarnessCommandResult Success(
        Guid correlationId,
        object? data = null,
        IReadOnlyList<string>? warnings = null) =>
        new(
            HarnessExitCode.Success,
            HarnessEnvelope.Ok(correlationId, data, warnings));

    private static HarnessCommandResult Unsupported(Guid correlationId, string? reason) =>
        Failure(
            correlationId,
            HarnessExitCode.UnsupportedCapability,
            "harness.capability_unavailable",
            reason ?? "The requested native harness capability is unavailable.");

    private static HarnessCommandResult SafetyDenied(
        Guid correlationId,
        HarnessSafetyDecision decision) =>
        Failure(
            correlationId,
            HarnessExitCode.ConflictOrBusy,
            decision.StableCode,
            decision.Reasons.Count == 0
                ? "The guarded operation was denied."
                : decision.Reasons[0],
            retryable: false,
            data: new { reasons = decision.Reasons });

    private static HarnessCommandResult Busy(Guid correlationId) =>
        Failure(
            correlationId,
            HarnessExitCode.ConflictOrBusy,
            "harness.operation_busy",
            "Another guarded operation is in progress.",
            retryable: true);

    private static HarnessCommandResult Failure(
        Guid correlationId,
        HarnessExitCode exitCode,
        string code,
        string message,
        bool retryable = false,
        object? data = null) =>
        new(
            exitCode,
            HarnessEnvelope.Fail(
                correlationId,
                code,
                message,
                retryable,
                data));
}
