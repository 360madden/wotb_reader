using System.Globalization;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Session;

internal enum ReplayLifecycleState
{
    Unknown,
    OfflineReplayStarted,
    OfflineReplayStopped,
    OnlineBattle,
}

internal enum ReplayEvidenceSource
{
    Unknown,
    BlitzNativeLog,
}

internal sealed record GameProcessEvidence(
    int ProcessId,
    long ProcessStartIdentity,
    bool IsAlive,
    string ObservedCanonicalExecutablePath,
    string ObservedProductVersion,
    ContentHash ObservedExecutableSha256,
    long WindowHandle,
    int WindowOwnerProcessId);

internal sealed record ReplayLifecycleEvidence(
    ReplayLifecycleState State,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? SourceTimestampUtc,
    ReplayEvidenceSource Source,
    string SourceIdentity,
    long SourceGeneration,
    long SourceSequence,
    long SourceByteOffset,
    LifecycleMarkerProvenance Provenance,
    int ProcessId,
    long ProcessStartIdentity,
    string LaunchCorrelation);

internal sealed record GameSessionEvidence(
    bool GamePresent,
    bool MonitorHealthy,
    bool ReplayUiConfirmed,
    GameProcessEvidence? Process,
    ReplayLifecycleEvidence? Lifecycle);

internal sealed class ManagedGameLaunchContext
{
    private readonly Dictionary<string, LifecycleSourceCursor> _sourceBaselines;

    public ManagedGameLaunchContext(
        string launchCorrelation,
        InstalledGameIdentity trustedGameIdentity,
        int processId,
        long processStartIdentity,
        IReadOnlyList<LifecycleSourceCursor> lifecycleSourceBaselines,
        long sourceSequenceBaseline,
        DateTimeOffset lifecycleBaselineCapturedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchCorrelation);
        ArgumentNullException.ThrowIfNull(trustedGameIdentity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processStartIdentity);
        ArgumentNullException.ThrowIfNull(lifecycleSourceBaselines);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSequenceBaseline);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            lifecycleBaselineCapturedAtUtc,
            DateTimeOffset.MinValue);

        LifecycleSourceCursor[] snapshot = [.. lifecycleSourceBaselines];
        _sourceBaselines = new Dictionary<string, LifecycleSourceCursor>(
            snapshot.Length,
            StringComparer.Ordinal);
        foreach (LifecycleSourceCursor source in snapshot)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId.Value);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Generation);
            ArgumentOutOfRangeException.ThrowIfNegative(source.LastByteOffset);
            if (!_sourceBaselines.TryAdd(source.SourceId.Value, source))
            {
                throw new ArgumentException(
                    "Lifecycle source identities must be unique.",
                    nameof(lifecycleSourceBaselines));
            }
        }

        LaunchCorrelation = launchCorrelation;
        TrustedGameIdentity = trustedGameIdentity;
        ProcessId = processId;
        ProcessStartIdentity = processStartIdentity;
        LifecycleSourceBaselines = Array.AsReadOnly(snapshot);
        SourceSequenceBaseline = sourceSequenceBaseline;
        LifecycleBaselineCapturedAtUtc = lifecycleBaselineCapturedAtUtc;
    }

    public string LaunchCorrelation { get; }

    public InstalledGameIdentity TrustedGameIdentity { get; }

    public int ProcessId { get; }

    public long ProcessStartIdentity { get; }

    public IReadOnlyList<LifecycleSourceCursor> LifecycleSourceBaselines { get; }

    public long SourceSequenceBaseline { get; }

    public DateTimeOffset LifecycleBaselineCapturedAtUtc { get; }

    public bool TryGetSourceBaseline(
        string sourceIdentity,
        out LifecycleSourceCursor? sourceBaseline) =>
        _sourceBaselines.TryGetValue(sourceIdentity, out sourceBaseline);
}

/// <summary>
/// Owns the evidence-backed offline state and orchestrates managed replay
/// launches through the M2 suspended-process pipeline. Public consumers
/// receive only safe snapshots and observations; authorization and process
/// identity never leave this adapter.
/// </summary>
internal sealed class GameSessionCoordinator : IGameSessionState,
    IGameReplayLauncher, IGameMemoryObserver, IGameMemoryScanner, IAsyncDisposable, IDisposable
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly GameIntegrationOptions _options;
    private readonly ILogger<GameSessionCoordinator> _logger;
    private readonly IManagedLaunchPreparer _preparer;
    private readonly IManagedReplayArtifactStager _artifactStager;
    private readonly ISuspendedProcessPlatform _suspendedPlatform;
    private readonly IManagedLaunchCorrelationRegistrar _correlationRegistrar;
    private readonly IThreadResumePlatform _threadResumePlatform;
    private readonly IGameProcessIdentityObserver _processIdentityObserver;
    private readonly IGuardedMemoryReaderFactory _memoryReaderFactory;
    private readonly IGameProcessModuleBaseAddressResolver _moduleBaseAddressResolver;
    private readonly IOffsetTableReader _offsetTableReader;
    private readonly MemoryScanDiscoverer _scanDiscoverer;
    private const int MaximumReadAddresses = 2000;
    private readonly IBlitzReplayLifecycleFeed _lifecycleFeed;
    private readonly MemoryScanEngine _scanEngine;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private GameSessionSnapshot _snapshot = new(
        GameSessionVerificationState.Unknown,
        GamePresent: false,
        DateTimeOffset.MinValue,
        EvidenceExpiresAtUtc: null,
        "session.initial");
    private AuthorizedObservation? _authorization;
    private ManagedGameLaunchContext? _managedLaunch;
    private EvidenceCursor? _lastCursor;
    private long _authorizationGeneration;
    private DateTimeOffset _lastHeartbeatLoggedAtUtc = DateTimeOffset.MinValue;

    // Active launch leases owned by the coordinator until the session is revoked.
    private WindowsTrustedExecutableLaunchLease? _activeExecutableLease;
    private ManagedReplayArtifactLease? _activeArtifactLease;
    private SuspendedGameProcessLease? _activeSuspendedLease;
    private CancellationTokenSource? _activeMonitoringCts;
    private Task? _activeMonitoringTask;
    private CancellationTokenSource? _authorizationCts;
    private bool _disposed;

    public GameSessionCoordinator(
        TimeProvider timeProvider,
        GameIntegrationOptions options,
        ILogger<GameSessionCoordinator> logger,
        IManagedLaunchPreparer preparer,
        IManagedReplayArtifactStager artifactStager,
        ISuspendedProcessPlatform suspendedPlatform,
        IManagedLaunchCorrelationRegistrar correlationRegistrar,
        IThreadResumePlatform threadResumePlatform,
        IGameProcessIdentityObserver processIdentityObserver,
        IGuardedMemoryReaderFactory memoryReaderFactory,
        IGameProcessModuleBaseAddressResolver moduleBaseAddressResolver,
        IOffsetTableReader offsetTableReader,
        MemoryScanDiscoverer scanDiscoverer,
        MemoryScanEngine scanEngine,
        IBlitzReplayLifecycleFeed lifecycleFeed)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _artifactStager = artifactStager ?? throw new ArgumentNullException(nameof(artifactStager));
        _suspendedPlatform = suspendedPlatform ?? throw new ArgumentNullException(nameof(suspendedPlatform));
        _correlationRegistrar = correlationRegistrar ?? throw new ArgumentNullException(nameof(correlationRegistrar));
        _threadResumePlatform = threadResumePlatform ?? throw new ArgumentNullException(nameof(threadResumePlatform));
        _processIdentityObserver = processIdentityObserver ?? throw new ArgumentNullException(nameof(processIdentityObserver));
        _memoryReaderFactory = memoryReaderFactory ?? throw new ArgumentNullException(nameof(memoryReaderFactory));
        _moduleBaseAddressResolver = moduleBaseAddressResolver ?? throw new ArgumentNullException(nameof(moduleBaseAddressResolver));
        _offsetTableReader = offsetTableReader ?? throw new ArgumentNullException(nameof(offsetTableReader));
        _scanDiscoverer = scanDiscoverer ?? throw new ArgumentNullException(nameof(scanDiscoverer));
        _scanEngine = scanEngine ?? throw new ArgumentNullException(nameof(scanEngine));
        _lifecycleFeed = lifecycleFeed ?? throw new ArgumentNullException(nameof(lifecycleFeed));
    }

    /// <summary>
    /// Records an adapter-generated launch correlation. Callers outside
    /// GameIntegration cannot provide or inspect this value.
    /// </summary>
    internal void RecordManagedLaunch(ManagedGameLaunchContext launch)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(GameSessionCoordinator));

        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.LaunchCorrelation);
        if (launch.SourceSequenceBaseline < 0
            || launch.LifecycleBaselineCapturedAtUtc <= DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(launch),
                "A managed launch requires a timestamped, non-negative cursor baseline.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(GameSessionCoordinator));
            Revoke();
            _managedLaunch = launch;
            _lastCursor = null;
            _snapshot = CreateSnapshot(
                GameSessionVerificationState.Unknown,
                gamePresent: false,
                expiresAtUtc: null,
                "launch.awaiting_evidence");
        }
    }

    /// <summary>
    /// Replaces the current evidence atomically. Evidence from separate
    /// process instances, monitor generations, or launch correlations is never
    /// accumulated into an authorization decision.
    /// </summary>
    internal void ApplyEvidence(GameSessionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Evaluate(evidence);
        }
    }

    internal void ReportMonitorFailure()
    {
        lock (_gate)
        {
            Deny("evidence.monitor_unhealthy");
        }
    }

    private void ReportMonitorFailure(
        ManagedGameLaunchContext launch,
        CancellationToken monitorToken)
    {
        lock (_gate)
        {
            if (!IsCurrentMonitorLocked(launch, monitorToken))
            {
                return;
            }

            Deny("evidence.monitor_unhealthy");
        }
    }

    private void ApplyMonitorEvidence(
        ManagedGameLaunchContext launch,
        GameSessionEvidence evidence,
        CancellationToken monitorToken)
    {
        lock (_gate)
        {
            if (!IsCurrentMonitorLocked(launch, monitorToken))
            {
                return;
            }

            Evaluate(evidence);
        }
    }

    private bool IsCurrentMonitorLocked(
        ManagedGameLaunchContext launch,
        CancellationToken monitorToken) =>
        !_disposed
        && !monitorToken.IsCancellationRequested
        && ReferenceEquals(_managedLaunch, launch)
        && _activeMonitoringCts is not null
        && _activeMonitoringCts.Token == monitorToken;

    /// <summary>
    /// Liveness heartbeat for an already-verified managed launch. The native
    /// log goes quiet during replay playback (no new Start markers arrive), so
    /// without this the authorization would expire at
    /// <see cref="GameIntegrationOptions.OfflineReplayEvidenceLifetime"/> and
    /// the managed game would be terminated mid-battle (observed 2026-08-04:
    /// a 281s replay was killed ~120s after verification, "window closed
    /// unexpectedly", no crash). Extends the expiry while the verified process
    /// identity stays healthy; any process/window/identity anomaly still
    /// revokes immediately through the monitor's terminal-failure path.
    ///
    /// Trade-off: this substitutes process liveness for fresh replay markers
    /// during playback. It is bounded to the managed-launch pipeline, which
    /// only ever produces offline replay sessions (offline session gate + argv
    /// replay), so a live, identity-matched process of the trusted executable
    /// cannot be an online session. The authorization still fails closed on
    /// process death, window loss, identity drift, replay-stop markers, and
    /// coordinator revocation (e.g. the next managed launch).
    /// </summary>
    internal void RefreshVerifiedEvidence(
        ManagedGameLaunchContext launch,
        GameProcessEvidence processEvidence,
        CancellationToken monitorToken)
    {
        lock (_gate)
        {
            if (_disposed
                || monitorToken.IsCancellationRequested
                || !ReferenceEquals(_managedLaunch, launch)
                || _snapshot.State != GameSessionVerificationState.OfflineReplayVerified)
            {
                return;
            }

            if (!IsProcessIdentityValid(processEvidence))
            {
                Deny("process.identity_mismatch");
                return;
            }

            DateTimeOffset expiresAtUtc =
                _timeProvider.GetUtcNow() + _options.OfflineReplayEvidenceLifetime;
            if (_authorization is not null)
            {
                _authorization = _authorization with { ExpiresAtUtc = expiresAtUtc };
            }

            _snapshot = CreateSnapshot(
                GameSessionVerificationState.OfflineReplayVerified,
                gamePresent: true,
                expiresAtUtc,
                "session.offline_replay_verified");

            // Throttled diagnostics: a heartbeat line every beat would spam;
            // one per 30s shows the expiry rolling without noise.
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_logger.IsEnabled(LogLevel.Debug)
                && now - _lastHeartbeatLoggedAtUtc >= TimeSpan.FromSeconds(30))
            {
                _lastHeartbeatLoggedAtUtc = now;
                _logger.LogDebug(
                    "Heartbeat: verified launch {Correlation} expiry extended to {ExpiresAtUtc:O}.",
                    launch.LaunchCorrelation,
                    expiresAtUtc);
            }
        }
    }

    public ValueTask<GameSessionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            return ValueTask.FromResult(_snapshot);
        }
    }

    public async ValueTask<OperationResult<GameReplayLaunchOutcome>> LaunchAsync(
        GameReplayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed)
        {
            return OperationResult.Failure<GameReplayLaunchOutcome>(
                new ApplicationError("game.session_disposed", "The game session coordinator is disposed.", Retryable: false));
        }

        using CancellationTokenSource launchCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        try
        {
            return await LaunchCoreAsync(request, launchCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string detail = exception.InnerException is not null
                ? $"{exception.GetType().Name}: {exception.Message} | Inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}"
                : $"{exception.GetType().Name}: {exception.Message}";
            return OperationResult.Failure<GameReplayLaunchOutcome>(
                new ApplicationError(
                    "game.launch.unexpected_failure",
                    detail,
                    Retryable: true));
        }
    }

    private async ValueTask<OperationResult<GameReplayLaunchOutcome>> LaunchCoreAsync(
        GameReplayLaunchRequest request,
        CancellationToken cancellationToken)
    {

        LogLaunchStage("prepare", "started");
        // ── 1. Prepare: identity, correlation, lifecycle baseline ──
        OperationResult<ManagedLaunchPreparation> prepResult;
        try
        {
            prepResult = await _preparer.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLaunchStage("prepare", "threw", exception.GetType().Name);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepResult.IsSuccess)
        {
            LogLaunchStage("prepare", "failed", prepResult.Error?.Code);
            return LaunchFailure(prepResult.Error!);
        }

        ManagedLaunchPreparation preparation = prepResult.Value!;
        LogLaunchStage("prepare", "completed");
        cancellationToken.ThrowIfCancellationRequested();

        WindowsTrustedExecutableLaunchLease? executableLease = null;
        ManagedReplayArtifactLease? artifactLease = null;
        SuspendedGameProcessLease? suspendedLease = null;
        WindowsTrustedExecutableLaunchLease? handedOffExe = null;
        ManagedReplayArtifactLease? handedOffArtifact = null;
        DetachedLaunchLeases replacedLeases = default;
        bool replacedLeasesDetached = false;
        bool previousLaunchWasVerified = false;
        bool launchHandoffCommitted = false;
        ManagedGameLaunchContext? committedLaunchContext = null;
        CancellationTokenSource? previousMonitoringCts = null;
        CancellationTokenSource? monitoringCts = null;
        string currentStage = "prepare";

        try
        {
            currentStage = "executable_lease";
            LogLaunchStage("executable_lease", "started");
            // ── 2. Acquire executable lease ──
            OperationResult<WindowsTrustedExecutableLaunchLease> exeLeaseResult =
                await WindowsTrustedExecutableLaunchLease.AcquireAsync(
                    preparation.TrustedIdentity,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!exeLeaseResult.IsSuccess)
            {
                LogLaunchStage("executable_lease", "failed", exeLeaseResult.Error?.Code);
                return LaunchFailure(exeLeaseResult.Error!);
            }

            executableLease = exeLeaseResult.Value!;
            LogLaunchStage("executable_lease", "completed");
            cancellationToken.ThrowIfCancellationRequested();

            currentStage = "artifact_staging";
            LogLaunchStage("artifact_staging", "started");
            // ── 3. Stage artifact ──
            OperationResult<ManagedReplayArtifactLease> artifactResult =
                await _artifactStager.StageAsync(
                    request.SourceArtifactId,
                    cancellationToken).ConfigureAwait(false);
            if (!artifactResult.IsSuccess)
            {
                LogLaunchStage("artifact_staging", "failed", artifactResult.Error?.Code);
                await executableLease.DisposeAsync().ConfigureAwait(false);
                executableLease = null;
                return LaunchFailure(artifactResult.Error!);
            }

            artifactLease = artifactResult.Value!;
            LogLaunchStage("artifact_staging", "completed");
            cancellationToken.ThrowIfCancellationRequested();

            currentStage = "suspended_process";
            LogLaunchStage("suspended_process", "started");
            // ── 4. Create suspended process ──
            OperationResult<SuspendedGameProcessLease> suspendedResult =
                await _suspendedPlatform.CreateAsync(
                    executableLease,
                    artifactLease,
                    cancellationToken).ConfigureAwait(false);
            if (!suspendedResult.IsSuccess)
            {
                LogLaunchStage("suspended_process", "failed", suspendedResult.Error?.Code);
                await artifactLease.DisposeAsync().ConfigureAwait(false);
                await executableLease.DisposeAsync().ConfigureAwait(false);
                artifactLease = null;
                executableLease = null;
                return LaunchFailure(suspendedResult.Error!);
            }

            suspendedLease = suspendedResult.Value!;
            LogLaunchStage("suspended_process", "completed");
            cancellationToken.ThrowIfCancellationRequested();

            currentStage = "correlation";
            LogLaunchStage("correlation", "started");
            // ── 5. Register correlation ──
            OperationResult<ManagedGameLaunchContext> correlationResult =
                _correlationRegistrar.Register(preparation, suspendedLease);
            if (!correlationResult.IsSuccess)
            {
                LogLaunchStage("correlation", "failed", correlationResult.Error?.Code);
                await suspendedLease.DisposeAsync().ConfigureAwait(false);
                // SuspendedGameProcessLease owns the input leases until handoff
                // and disposed them above; clear the aliases so finally cannot
                // dispose the same leases a second time.
                suspendedLease = null;
                artifactLease = null;
                executableLease = null;
                return LaunchFailure(correlationResult.Error!);
            }

            ManagedGameLaunchContext launchContext = correlationResult.Value!;
            LogLaunchStage("correlation", "completed");
            cancellationToken.ThrowIfCancellationRequested();

            currentStage = "resume";
            LogLaunchStage("resume", "started");
            // ── 6. Resume the child thread (must happen BEFORE HandOffLeases
            //     so that resume failures terminate the suspended child) ──
            OperationResult<ThreadResumeOutcome> resumeResult =
                _threadResumePlatform.Resume(suspendedLease.ThreadHandle!);
            if (!resumeResult.IsSuccess)
            {
                LogLaunchStage("resume", "failed", resumeResult.Error?.Code);
                // Resume failed; child is still suspended. Disposal terminates it
                // because HandOffLeases has not been called.
                await suspendedLease.DisposeAsync().ConfigureAwait(false);
                // SuspendedGameProcessLease still owns the input leases because
                // handoff has not happened; avoid a second disposal in finally.
                suspendedLease = null;
                artifactLease = null;
                executableLease = null;
                return LaunchFailure(resumeResult.Error!);
            }

            LogLaunchStage("resume", "completed");
            cancellationToken.ThrowIfCancellationRequested();

            // ── 7–8. Atomically commit the handoff under the coordinator lock.
            // If disposal/cancellation wins this linearization point, the
            // suspended lease has not been handed off and finally will terminate
            // the child. Once committed, disposal may release handles but the
            // child remains a valid launched process.
            CancellationToken monitoringToken;
            Task? monitoringTask = null;
            bool disposedDuringLaunch;
            lock (_gate)
            {
                disposedDuringLaunch = _disposed || cancellationToken.IsCancellationRequested;
                if (!disposedDuringLaunch)
                {
                    // Capture the prior state before RecordManagedLaunch replaces
                    // it with awaiting_evidence. A verified prior launch remains
                    // alive by design; an unverified prior launch must be terminated
                    // before its handed-off lease is detached.
                    previousLaunchWasVerified =
                        _snapshot.State == GameSessionVerificationState.OfflineReplayVerified;

                    // Validate and record the new generation before handing off
                    // ownership. If this throws, the child is still owned by the
                    // local suspended lease and finally terminates it.
                    RecordManagedLaunch(launchContext);
                    if (!previousLaunchWasVerified)
                    {
                        _activeSuspendedLease?.TryTerminateAfterHandOff();
                    }

                    previousMonitoringCts = _activeMonitoringCts;
                    (handedOffExe, handedOffArtifact) = suspendedLease.HandOffLeases();
                    replacedLeases = DetachLaunchLeasesLocked();
                    replacedLeasesDetached = true;

                    _activeSuspendedLease = suspendedLease;
                    _activeExecutableLease = handedOffExe;
                    _activeArtifactLease = handedOffArtifact;
                    launchHandoffCommitted = true;
                    committedLaunchContext = launchContext;

                    monitoringCts = new CancellationTokenSource();
                    _activeMonitoringCts = monitoringCts;
                    monitoringToken = monitoringCts.Token;
                    // Register the monitor before releasing the same
                    // linearization lock that publishes the new launch. This
                    // prevents revocation from detaching the launch in the gap
                    // between lease publication and monitor registration.
                    monitoringTask = StartMonitoringLifecycle(
                        launchContext,
                        monitoringCts,
                        monitoringToken);
                    _activeMonitoringTask = monitoringTask;
                }
                else
                {
                    monitoringToken = default;
                }
            }

            QueueMonitorStop(previousMonitoringCts);

            if (disposedDuringLaunch)
            {
                // The suspended lease still owns both input leases because the
                // handoff did not occur. Clear aliases before finally disposes
                // that owner, preventing duplicate cleanup.
                artifactLease = null;
                executableLease = null;
                return OperationResult.Failure<GameReplayLaunchOutcome>(
                    new ApplicationError("game.session_disposed", "The game session coordinator was disposed during launch.", Retryable: false));
            }

            suspendedLease = null;
            executableLease = null;
            artifactLease = null;
            handedOffExe = null;
            handedOffArtifact = null;

            LogLaunchStage("handoff", "completed");

            return OperationResult.Success(
                new GameReplayLaunchOutcome(_timeProvider.GetUtcNow()));
        }
        catch (Exception exception)
        {
            LogLaunchStage(currentStage, "threw", exception.GetType().Name);
            if (launchHandoffCommitted)
            {
                DetachedLaunchLeases failedLaunchLeases;
                CancellationTokenSource? failedMonitoringCts = null;
                Task? failedMonitoringTask = null;
                lock (_gate)
                {
                    failedLaunchLeases = ReferenceEquals(_activeSuspendedLease, suspendedLease)
                        ? DetachLaunchLeasesLocked()
                        : default;
                    if (failedLaunchLeases.Suspended is not null)
                    {
                        failedLaunchLeases.Suspended.TryTerminateAfterHandOff();
                    }

                    if (ReferenceEquals(_activeMonitoringCts, monitoringCts))
                    {
                        failedMonitoringCts = _activeMonitoringCts;
                        failedMonitoringTask = _activeMonitoringTask;
                        _activeMonitoringCts = null;
                        _activeMonitoringTask = null;
                    }

                    if (ReferenceEquals(_managedLaunch, committedLaunchContext))
                    {
                        _managedLaunch = null;
                        _lastCursor = null;
                    }
                }

                await StopMonitoringAsync(failedMonitoringCts, failedMonitoringTask)
                    .ConfigureAwait(false);
                await DisposeDetachedLaunchLeasesAsync(failedLaunchLeases)
                    .ConfigureAwait(false);
                // Detachment transferred these objects to the failed-cleanup
                // path. Clear every local alias before finally runs.
                suspendedLease = null;
                artifactLease = null;
                executableLease = null;
                handedOffExe = null;
                handedOffArtifact = null;
            }

            throw;
        }
        finally
        {
            if (suspendedLease is not null)
            {
                await suspendedLease.DisposeAsync().ConfigureAwait(false);
            }

            if (handedOffArtifact is not null)
            {
                await handedOffArtifact.DisposeAsync().ConfigureAwait(false);
            }

            if (handedOffExe is not null)
            {
                await handedOffExe.DisposeAsync().ConfigureAwait(false);
            }

            if (artifactLease is not null)
            {
                await artifactLease.DisposeAsync().ConfigureAwait(false);
            }

            if (executableLease is not null)
            {
                await executableLease.DisposeAsync().ConfigureAwait(false);
            }

            if (replacedLeasesDetached)
            {
                await DisposeDetachedLaunchLeasesAsync(replacedLeases).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<GameMemoryObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Capture an immutable snapshot of the authorization under the lock,
        // then release it before performing slow memory reads. The linked
        // authorization token revokes an in-flight observation immediately when
        // replay evidence stops, expires, or changes identity.
        AuthorizedObservation? auth;
        CancellationToken authorizationToken;
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified
                || _authorization is null
                || _authorizationCts is null)
            {
                return UnknownObservation();
            }

            auth = _authorization;
            authorizationToken = _authorizationCts.Token;
        }

        using CancellationTokenSource observationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        return await ReadMemoryAsync(auth, observationCancellation.Token).ConfigureAwait(false);
    }

    private async ValueTask<GameMemoryObservation> ReadMemoryAsync(
        AuthorizedObservation auth,
        CancellationToken cancellationToken)
    {
        // No offset table loaded — authorized but no offsets configured.
        OffsetTable? table = auth.OffsetTable;
        if (table is null)
        {
            return IsObservationAuthorizationCurrent(auth, cancellationToken)
                ? AvailableObservation()
                : UnknownObservation();
        }

        // Collect known fields (non-zero offsets).
        List<OffsetField> knownFields = [];
        foreach (OffsetField field in table.Fields)
        {
            // Discovery candidates must never authorize telemetry reads. Only
            // explicitly verified fields may cross the runtime observation path.
            if (field.Offset != 0 && field.Status == OffsetFieldStatus.Verified)
            {
                knownFields.Add(field);
            }
        }

        if (knownFields.Count == 0)
        {
            return IsObservationAuthorizationCurrent(auth, cancellationToken)
                ? AvailableObservation()
                : UnknownObservation();
        }

        nint baseAddress = _moduleBaseAddressResolver.Resolve(auth.ProcessId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (baseAddress == nint.Zero)
        {
            // Authorization remains valid for a retry, but no memory read was
            // possible for this observation. Do not report a misleading
            // "available" result with all fields null.
            return UnknownObservation();
        }

        // Create the guarded memory reader.
        AuthorizedMemoryObservation obs = new(
            auth.ProcessId,
            auth.ProcessStartIdentity,
            auth.CanonicalExecutablePath,
            auth.ProductVersion,
            auth.ExecutableSha256,
            auth.ExpiresAtUtc,
            auth.ReadGate)
        {
            Generation = auth.Generation,
        };

        OperationResult<IAuthorizedMemoryReader> readerResult =
            await _memoryReaderFactory.CreateAsync(obs, cancellationToken)
                .ConfigureAwait(false);
        if (!readerResult.IsSuccess)
        {
            return IsObservationAuthorizationCurrent(auth, cancellationToken)
                ? AvailableObservation()
                : UnknownObservation();
        }

        IAuthorizedMemoryReader reader = readerResult.Value!;

        double? replayTime = null;
        int? playerHP = null;
        float? px = null, py = null, pz = null, yaw = null, pitch = null;
        int? aliveTankCount = null;

        foreach (OffsetField field in knownFields)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int size = field.FieldType switch
            {
                OffsetFieldType.DoubleField => 8,
                OffsetFieldType.FloatField => 4,
                OffsetFieldType.Int32Field => 4,
                _ => 0,
            };
            if (size == 0)
            {
                continue;
            }

            nint absoluteAddress = baseAddress + (nint)field.Offset;
            OperationResult<byte[]> readResult =
                await reader.ReadAsync(absoluteAddress, size, cancellationToken)
                    .ConfigureAwait(false);
            if (!readResult.IsSuccess)
            {
                continue;
            }

            byte[] bytes = readResult.Value!;
            switch (field.Name)
            {
                case "replayTime":
                    replayTime = BitConverter.ToDouble(bytes, 0);
                    break;
                case "playerHP":
                    playerHP = BitConverter.ToInt32(bytes, 0);
                    break;
                case "playerPositionX":
                    px = BitConverter.ToSingle(bytes, 0);
                    break;
                case "playerPositionY":
                    py = BitConverter.ToSingle(bytes, 0);
                    break;
                case "playerPositionZ":
                    pz = BitConverter.ToSingle(bytes, 0);
                    break;
                case "playerYaw":
                    yaw = BitConverter.ToSingle(bytes, 0);
                    break;
                case "cameraPitch":
                    pitch = BitConverter.ToSingle(bytes, 0);
                    break;
                case "aliveTankCount":
                    aliveTankCount = BitConverter.ToInt32(bytes, 0);
                    break;
            }
        }

        // A reader failure may race with lifecycle revocation after the last
        // field read. Never turn that revoked authorization into an Available
        // observation merely because the loop has no more fields to process.
        if (!IsObservationAuthorizationCurrent(auth, cancellationToken))
        {
            return UnknownObservation();
        }

        return new GameMemoryObservation(
            GameMemoryObservationAvailability.Available,
            _timeProvider.GetUtcNow(),
            replayTime, playerHP, px, py, pz, yaw, pitch, aliveTankCount);
    }

    private GameMemoryObservation UnknownObservation() =>
        new(
            GameMemoryObservationAvailability.Unknown,
            _timeProvider.GetUtcNow(),
            null, null, null, null, null, null, null, null);

    private GameMemoryObservation AvailableObservation() =>
        new(
            GameMemoryObservationAvailability.Available,
            _timeProvider.GetUtcNow(),
            null, null, null, null, null, null, null, null);

    private void Evaluate(GameSessionEvidence evidence)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (!evidence.GamePresent)
        {
            // Losing process presence is terminal for the managed launch. A
            // handed-off child must not survive a negative presence signal.
            RevokeSession(terminateProcess: true);
            _snapshot = CreateSnapshot(
                GameSessionVerificationState.GameAbsent,
                gamePresent: false,
                expiresAtUtc: null,
                "game.absent");
            return;
        }

        if (evidence.Process is null || evidence.Lifecycle is null)
        {
            SetUnverified("evidence.incomplete");
            return;
        }

        GameProcessEvidence process = evidence.Process;
        ReplayLifecycleEvidence lifecycle = evidence.Lifecycle;

        if (!process.IsAlive)
        {
            Deny("process.exited");
            return;
        }

        if (!evidence.MonitorHealthy)
        {
            Deny("evidence.monitor_unhealthy");
            return;
        }

        if (lifecycle.State is ReplayLifecycleState.OfflineReplayStopped
            or ReplayLifecycleState.OnlineBattle)
        {
            Deny("evidence.lifecycle_denied");
            return;
        }

        if (lifecycle.ObservedAtUtc > now
            || now - lifecycle.ObservedAtUtc > _options.OfflineReplayEvidenceLifetime)
        {
            // Stale evidence immediately ends authorization and the managed
            // launch; retaining an unobserved child would violate fail-closed
            // lifecycle ownership.
            RevokeSession(terminateProcess: true);
            _snapshot = CreateSnapshot(
                GameSessionVerificationState.EvidenceStale,
                gamePresent: true,
                expiresAtUtc: lifecycle.ObservedAtUtc + _options.OfflineReplayEvidenceLifetime,
                "evidence.stale");
            return;
        }

        if (!IsProcessIdentityValid(process))
        {
            Deny("process.identity_mismatch");
            return;
        }

        if (_authorization is not null
            && (_authorization.ProcessId != process.ProcessId
                || _authorization.ProcessStartIdentity != process.ProcessStartIdentity
                || !string.Equals(
                    _authorization.CanonicalExecutablePath,
                    process.ObservedCanonicalExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    _authorization.ProductVersion,
                    process.ObservedProductVersion,
                    StringComparison.Ordinal)
                || _authorization.ExecutableSha256 != process.ObservedExecutableSha256))
        {
            Deny("process.identity_changed");
            return;
        }

        if (!IsCursorValid(lifecycle))
        {
            Deny("evidence.cursor_invalid");
            return;
        }

        if (lifecycle.State != ReplayLifecycleState.OfflineReplayStarted
            || !evidence.ReplayUiConfirmed
            || _managedLaunch is null
            || lifecycle.ProcessId != process.ProcessId
            || lifecycle.ProcessStartIdentity != process.ProcessStartIdentity
            || !string.Equals(
                lifecycle.LaunchCorrelation,
                _managedLaunch.LaunchCorrelation,
                StringComparison.Ordinal))
        {
            SetUnverified("evidence.offline_replay_unverified");
            return;
        }

        DateTimeOffset expiresAtUtc =
            lifecycle.ObservedAtUtc + _options.OfflineReplayEvidenceLifetime;
        _lastCursor = new EvidenceCursor(
            lifecycle.SourceIdentity,
            lifecycle.SourceGeneration,
            lifecycle.SourceSequence,
            lifecycle.SourceByteOffset);

        OffsetTable? offsetTable = LoadOffsetTable(process);
        // The scanner resolves the trusted module base just before each scan;
        // this remains independent from runtime offset promotion.

        if (_authorization is null)
        {
            _authorizationCts = new CancellationTokenSource();
        }

        AuthorizationReadGate readGate = _authorization?.ReadGate ?? new AuthorizationReadGate();
        _authorization = new AuthorizedObservation(
            ++_authorizationGeneration,
            process.ProcessId,
            process.ProcessStartIdentity,
            process.ObservedCanonicalExecutablePath,
            process.ObservedProductVersion,
            process.ObservedExecutableSha256,
            expiresAtUtc,
            offsetTable,
            readGate);
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.OfflineReplayVerified,
            gamePresent: true,
            expiresAtUtc,
            "session.offline_replay_verified");
    }

    private bool IsCursorValid(ReplayLifecycleEvidence lifecycle)
    {
        if (_managedLaunch is null
            || lifecycle.Source != ReplayEvidenceSource.BlitzNativeLog
            || lifecycle.Provenance != LifecycleMarkerProvenance.Live
            || lifecycle.SourceGeneration <= 0
            || lifecycle.SourceSequence <= _managedLaunch.SourceSequenceBaseline
            || lifecycle.SourceByteOffset <= 0)
        {
            return false;
        }

        if (_lastCursor is not null)
        {
            return string.Equals(
                       lifecycle.SourceIdentity,
                       _lastCursor.SourceIdentity,
                       StringComparison.Ordinal)
                   && lifecycle.SourceGeneration == _lastCursor.SourceGeneration
                   && lifecycle.SourceSequence > _lastCursor.SourceSequence
                   && lifecycle.SourceByteOffset > _lastCursor.SourceByteOffset;
        }

        if (_managedLaunch.TryGetSourceBaseline(
                lifecycle.SourceIdentity,
                out LifecycleSourceCursor? sourceBaseline)
            && sourceBaseline is not null)
        {
            return lifecycle.SourceGeneration == sourceBaseline.Generation
                && lifecycle.SourceByteOffset > sourceBaseline.LastByteOffset;
        }

        // A genuinely new log source can appear only after the reconciled
        // launch baseline. The feed classifies it Live only when the prior
        // enumeration was complete and healthy; reincarnations use a later
        // generation and remain historical.
        return lifecycle.SourceGeneration == 1
            && lifecycle.SourceTimestampUtc.HasValue
            && lifecycle.SourceTimestampUtc >= _managedLaunch.LifecycleBaselineCapturedAtUtc
            && lifecycle.SourceTimestampUtc <= lifecycle.ObservedAtUtc;
    }

    private bool IsProcessIdentityValid(GameProcessEvidence process) =>
        _managedLaunch is not null
        && process.ProcessId == _managedLaunch.ProcessId
        && process.ProcessStartIdentity == _managedLaunch.ProcessStartIdentity
        && process.WindowHandle != 0
        && process.WindowOwnerProcessId == process.ProcessId
        && string.Equals(
            _managedLaunch.TrustedGameIdentity.ExecutablePath,
            process.ObservedCanonicalExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            _managedLaunch.TrustedGameIdentity.ProductVersion,
            process.ObservedProductVersion,
            StringComparison.Ordinal)
        && _managedLaunch.TrustedGameIdentity.ExecutableSha256
            == process.ObservedExecutableSha256;

    internal static GameProcessEvidence? CreateObservedProcessEvidence(
        ManagedGameLaunchContext launch,
        GameProcessObservationResult observation)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(observation);

        ObservedGameProcessIdentity? identity = observation.Identity;
        if (observation.Status != GameProcessObservationStatus.Available
            || identity is null
            || identity.WindowHandle == 0
            || identity.ProcessId != launch.ProcessId
            || identity.ProcessStartIdentity != launch.ProcessStartIdentity
            || !string.Equals(
                identity.CanonicalExecutablePath,
                launch.TrustedGameIdentity.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                identity.ProductVersion,
                launch.TrustedGameIdentity.ProductVersion,
                StringComparison.Ordinal)
            || identity.ExecutableSha256 != launch.TrustedGameIdentity.ExecutableSha256)
        {
            return null;
        }

        return new GameProcessEvidence(
            identity.ProcessId,
            identity.ProcessStartIdentity,
            IsAlive: true,
            identity.CanonicalExecutablePath,
            identity.ProductVersion,
            identity.ExecutableSha256,
            identity.WindowHandle,
            WindowOwnerProcessId: identity.ProcessId);
    }

    internal static bool IsWindowObservationTerminalFailure(
        bool correlatedEvidenceObserved,
        GameProcessObservationStatus status,
        bool exactWindowObserved) =>
        !exactWindowObserved
        && (correlatedEvidenceObserved
            || status is GameProcessObservationStatus.Unsupported
                or GameProcessObservationStatus.Ambiguous
                or GameProcessObservationStatus.Available);

    private void ExpireAuthorizationIfNeeded()
    {
        if (_authorization is null
            || _authorization.ExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            return;
        }

        RevokeSession(terminateProcess: true);
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.EvidenceStale,
            gamePresent: true,
            expiresAtUtc: _snapshot.EvidenceExpiresAtUtc,
            "evidence.expired");
    }

    private void SetUnverified(string reasonCode)
    {
        // Incomplete evidence before the first verification is recoverable:
        // retain the managed launch correlation so a later valid marker can
        // authorize it. Once a launch was verified, becoming unverified is
        // terminal and must terminate the handed-off child.
        if (_snapshot.State == GameSessionVerificationState.OfflineReplayVerified)
        {
            RevokeSession(terminateProcess: true);
        }
        else
        {
            Revoke();
        }

        _snapshot = CreateSnapshot(
            GameSessionVerificationState.GamePresentUnverified,
            gamePresent: true,
            expiresAtUtc: null,
            reasonCode);
    }

    private void Deny(string reasonCode)
    {
        // Denial is terminal for the managed launch. Do not infer whether the
        // previous snapshot was verified: identity changes, stop markers, and
        // monitor failures must all terminate a handed-off child.
        RevokeSession(terminateProcess: true);
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.Denied,
            gamePresent: true,
            expiresAtUtc: null,
            reasonCode);
    }

    private void Revoke()
    {
        _authorization?.ReadGate.Revoke();
        _authorization = null;
        _authorizationCts?.Cancel();
        // Do not dispose this CTS here. Scan setup creates its linked source
        // under _gate; retaining the canceled source avoids a dispose/register
        // race for a scan that has just crossed the authorization gate.
        _authorizationCts = null;
        _scanEngine.DiscardAllSessions();
    }

    private void RevokeSession(bool terminateProcess = false)
    {
        Revoke();

        CancellationTokenSource? monitoringCts = _activeMonitoringCts;
        Task? monitoringTask = _activeMonitoringTask;
        _activeMonitoringCts = null;
        _activeMonitoringTask = null;
        _managedLaunch = null;
        _lastCursor = null;

        // A handed-off launch is intentionally kept alive only after it has
        // verified as an offline replay. Any unverified launch is fail-closed:
        // request termination before detaching the lease, then dispose it after
        // releasing the coordinator lock.
        if (terminateProcess
            || _snapshot.State != GameSessionVerificationState.OfflineReplayVerified)
        {
            _activeSuspendedLease?.TryTerminateAfterHandOff();
        }

        DetachedLaunchLeases leases = DetachLaunchLeasesLocked();
        QueueMonitorStop(monitoringCts);
        QueueDetachedLaunchLeaseDisposal(leases);
    }

    private DetachedLaunchLeases DetachLaunchLeasesLocked() =>
        new(
            Interlocked.Exchange(ref _activeSuspendedLease, null),
            Interlocked.Exchange(ref _activeArtifactLease, null),
            Interlocked.Exchange(ref _activeExecutableLease, null));

    private static void QueueMonitorStop(CancellationTokenSource? monitoringCts)
    {
        if (monitoringCts is null)
        {
            return;
        }

        // Always perform cancellation asynchronously. RevokeSession and
        // replacement can call this while holding _gate; synchronous CTS
        // callbacks must not re-enter that lock. The monitor task owns its CTS
        // disposal in its finally block, so repeated stop requests are safe.
        _ = Task.Run(() =>
        {
            try
            {
                monitoringCts.Cancel();
            }
            catch
            {
                // Monitor shutdown is best effort; state is already invalidated.
            }
        });
    }

    private static async ValueTask StopMonitoringAsync(
        CancellationTokenSource? monitoringCts,
        Task? monitoringTask)
    {
        try
        {
            monitoringCts?.Cancel();
            if (monitoringTask is not null
                && Task.CurrentId != monitoringTask.Id)
            {
                await monitoringTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Monitor shutdown is best effort; state is already invalidated.
        }
    }

    private static void QueueDetachedLaunchLeaseDisposal(DetachedLaunchLeases leases)
    {
        if (leases.IsEmpty)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeDetachedLaunchLeasesAsync(leases).ConfigureAwait(false);
            }
            catch
            {
                // Lease disposal is best effort here; the owning state transition
                // must not synchronously block while holding _gate.
            }
        });
    }

    private static async ValueTask DisposeDetachedLaunchLeasesAsync(DetachedLaunchLeases leases)
    {
        // Disposal order: suspended first (closes handles; verified handoffs
        // leave the child alive, while an earlier termination request makes an
        // unverified handoff terminate), then artifact (deletes staging file),
        // then executable (releases file lock).
        if (leases.Suspended is not null)
        {
            await leases.Suspended.DisposeAsync().ConfigureAwait(false);
        }

        if (leases.Artifact is not null)
        {
            await leases.Artifact.DisposeAsync().ConfigureAwait(false);
        }

        if (leases.Executable is not null)
        {
            await leases.Executable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private OffsetTable? LoadOffsetTable(GameProcessEvidence process)
    {
        if (_managedLaunch is null)
        {
            return null;
        }

        string? sha256 = process.ObservedExecutableSha256.Value;
        if (string.IsNullOrEmpty(sha256) || sha256.Length != 64)
        {
            return null;
        }

        try
        {
            OperationResult<OffsetTable?> result = _offsetTableReader.Load(
                _managedLaunch.TrustedGameIdentity.ProductVersion,
                sha256,
                CancellationToken.None);
            return result.IsSuccess ? result.Value : null;
        }
        catch
        {
            // Offset loading failure is non-fatal — ObserveAsync
            // returns Available with all-nulls when no table is loaded.
            return null;
        }
    }

    private GameSessionSnapshot CreateSnapshot(
        GameSessionVerificationState state,
        bool gamePresent,
        DateTimeOffset? expiresAtUtc,
        string reasonCode) =>
        new(
            state,
            gamePresent,
            _timeProvider.GetUtcNow(),
            expiresAtUtc,
            reasonCode);

    private sealed record AuthorizedObservation(
        long Generation,
        int ProcessId,
        long ProcessStartIdentity,
        string CanonicalExecutablePath,
        string ProductVersion,
        ContentHash ExecutableSha256,
        DateTimeOffset ExpiresAtUtc,
        OffsetTable? OffsetTable,
        AuthorizationReadGate ReadGate);

    private sealed record EvidenceCursor(
        string SourceIdentity,
        long SourceGeneration,
        long SourceSequence,
        long SourceByteOffset);

    private readonly record struct DetachedLaunchLeases(
        SuspendedGameProcessLease? Suspended,
        ManagedReplayArtifactLease? Artifact,
        WindowsTrustedExecutableLaunchLease? Executable)
    {
        internal bool IsEmpty => Suspended is null && Artifact is null && Executable is null;
    }

    /// <summary>
    /// Disposes all active launch leases. Verified handed-off children remain
    /// alive; unverified handed-off children are termination-requested before
    /// detachment and retried during lease disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource? monitoringCts;
        Task? monitoringTask;
        DetachedLaunchLeases leases;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Linearize disposal with launch commit and evidence application.
            // A launch cannot observe a live coordinator and then install leases
            // after this transition.
            _disposed = true;
            monitoringCts = _activeMonitoringCts;
            monitoringTask = _activeMonitoringTask;
            _activeMonitoringCts = null;
            _activeMonitoringTask = null;
            Revoke();

            // A handed-off child is retained only after correlated offline replay
            // evidence. Dispose must terminate any unverified child before its
            // identity-bound lease is detached.
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified)
            {
                _activeSuspendedLease?.TryTerminateAfterHandOff();
            }
            leases = DetachLaunchLeasesLocked();
        }

        _lifetimeCts.Cancel();
        await StopMonitoringAsync(monitoringCts, monitoringTask).ConfigureAwait(false);
        await DisposeDetachedLaunchLeasesAsync(leases).ConfigureAwait(false);

        // Do not dispose the lifetime CTS here: LaunchAsync may have passed its
        // initial disposed check and still be creating a linked token source.
        // It is bounded to this coordinator and cancellation remains the important
        // lifetime operation.
    }

    /// <summary>
    /// Synchronous disposal for DI container compatibility. Delegates to
    /// DisposeAsync and blocks — safe because lease cleanup is local file
    /// and handle operations with no risk of deadlock.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask<OperationResult<string>> CreateSnapshotAsync(
        MemorySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return OperationResult.Failure<string>(
                new ApplicationError("discover.gate_not_satisfied", "Gate not satisfied."));

        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<string> result = await Task.Run(
                () => _scanEngine.CreateSnapshot(
                    observation!,
                    baseAddr,
                    new MemoryScanEngine.SnapshotFilter(
                        request.ValueSize,
                        request.MinAddress,
                        request.MaxAddress,
                        request.FloatMin,
                        request.FloatMax,
                        request.IntMin,
                        request.IntMax,
                        request.LongMin,
                        request.LongMax,
                        request.UIntMin,
                        request.UIntMax,
                        request.ValueKind,
                        request.Alignment,
                        request.RegionSelection,
                        request.MaxBytes),
                    scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation!, authorizationToken))
            {
                if (result.IsSuccess && result.Value is not null)
                {
                    _scanEngine.DiscardSession(result.Value);
                }

                return GateCheck<string>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<string>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<MemoryCompareResult>> CompareAsync(
        string sessionId,
        string compareMode,
        int maxCandidates,
        CancellationToken cancellationToken,
        bool advanceBaseline = false,
        double? deltaTarget = null,
        double? deltaTolerance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return OperationResult.Failure<MemoryCompareResult>(
                new ApplicationError("discover.gate_not_satisfied", "Gate not satisfied."));

        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryScanEngine.CompareResult> result = await Task.Run(
                () => _scanEngine.Compare(
                    observation!,
                    baseAddr,
                    sessionId,
                    compareMode ?? "changed",
                    maxCandidates,
                    advanceBaseline,
                    deltaTarget,
                    deltaTolerance,
                    scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation!, authorizationToken))
            {
                _scanEngine.DiscardSession(sessionId);
                return GateCheck<MemoryCompareResult>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            return result.IsSuccess
                ? OperationResult.Success(new MemoryCompareResult(
                    result.Value!.CompletedAtUtc,
                    result.Value.PreviousCount,
                    result.Value.CurrentCount,
                    result.Value.ChangedCount,
                    result.Value.UnchangedCount,
                    result.Value.IncreasedCount,
                    result.Value.DecreasedCount,
                    result.Value.Candidates,
                    result.Value.Truncated,
                    result.Value.ComparedAgainstRollingBaseline,
                    result.Value.RetainedCount))
                : OperationResult.Failure<MemoryCompareResult>(result.Error!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryCompareResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public void DiscardSession(string sessionId) => _scanEngine.DiscardSession(sessionId);

    public async ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
        MemoryNeighborhoodRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryScanResult> result = await Task.Run(
                () => _scanDiscoverer.ScanNeighborhood(observation!, baseAddr, request, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            return IsScanAuthorizationCurrent(observation!, authorizationToken)
                ? result
                : GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
        MemoryScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        MemoryScanRequest typedRequest = request with
        {
            ValueKind = ResolveValueKind(request.FieldType),
        };
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryScanResult> result = await Task.Run(
                () => _scanDiscoverer.Scan(observation!, baseAddr, typedRequest, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            return IsScanAuthorizationCurrent(observation!, authorizationToken)
                ? result
                : GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<MemoryScanResult>> ScanPatternAsync(
        MemoryScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryScanResult> result = await Task.Run(
                () => _scanDiscoverer.Scan(observation!, baseAddr, request, scanCancellation.Token, "aob"),
                scanCancellation.Token).ConfigureAwait(false);
            return IsScanAuthorizationCurrent(observation!, authorizationToken)
                ? result
                : GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<MemoryReadResult>> ReadAddressesAsync(
        MemoryReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Addresses is null
            || request.Addresses.Count is < 1 or > MaximumReadAddresses
            || request.ValueSize is not (4 or 8)
            || request.Addresses.Any(static address => address <= 0))
        {
            return OperationResult.Failure<MemoryReadResult>(
                new ApplicationError(
                    "discover.invalid_options",
                    "The read request must contain between 1 and " + MaximumReadAddresses
                        + " positive addresses and a value size of 4 or 8."));
        }

        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<MemoryReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        _ = baseAddress;
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryReadResult> result = await Task.Run(
                () => ReadAddressesCoreAsync(observation!, request, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            return IsScanAuthorizationCurrent(observation!, authorizationToken)
                ? result
                : GateCheck<MemoryReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    private async Task<OperationResult<MemoryReadResult>> ReadAddressesCoreAsync(
        AuthorizedMemoryObservation observation,
        MemoryReadRequest request,
        CancellationToken cancellationToken)
    {
        OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
            .CreateAsync(observation, cancellationToken)
            .ConfigureAwait(false);
        if (!readerResult.IsSuccess)
        {
            return OperationResult.Failure<MemoryReadResult>(
                new ApplicationError(
                    "discover.read_unavailable",
                    "The guarded memory reader is unavailable."));
        }

        IAuthorizedMemoryReader reader = readerResult.Value!;
        // One process lease for the whole batch (the monitor loop re-reads up
        // to 2000 staged addresses every few seconds; a per-address lease
        // would open and revalidate that many handles per round).
        OperationResult<IReadOnlyList<MemoryReadItem>> batch = await reader
            .ReadBatchAsync(
                [.. request.Addresses.Select(static address => (nint)address)],
                request.ValueSize,
                cancellationToken)
            .ConfigureAwait(false);
        if (!batch.IsSuccess || batch.Value is null)
        {
            return OperationResult.Failure<MemoryReadResult>(
                new ApplicationError(
                    "discover.read_failed",
                    "The batch memory read failed."));
        }

        List<MemoryReadItem> items = new(batch.Value.Count);
        foreach (MemoryReadItem item in batch.Value)
        {
            if (!item.ReadOk || item.ObservedValue is null)
            {
                items.Add(new MemoryReadItem(
                    item.AbsoluteAddress,
                    ReadOk: false,
                    null,
                    "unreadable"));
                continue;
            }

            byte[] bytes = item.ObservedValue;
            items.Add(new MemoryReadItem(
                item.AbsoluteAddress,
                ReadOk: true,
                bytes,
                FormatReadValue(bytes, request.ValueKind)));
        }

        return OperationResult.Success(new MemoryReadResult(_timeProvider.GetUtcNow(), items));
    }

    private static string FormatReadValue(byte[] bytes, MemoryValueKind kind) => kind switch
    {
        MemoryValueKind.FloatValue when bytes.Length == 4 =>
            BitConverter.ToSingle(bytes).ToString("R", CultureInfo.InvariantCulture),
        MemoryValueKind.DoubleValue when bytes.Length == 8 =>
            BitConverter.ToDouble(bytes).ToString("R", CultureInfo.InvariantCulture),
        MemoryValueKind.Int32Value when bytes.Length == 4 =>
            BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.UInt32Value when bytes.Length == 4 =>
            BitConverter.ToUInt32(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.Int64Value when bytes.Length == 8 =>
            BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
        MemoryValueKind.UInt64Value when bytes.Length == 8 =>
            BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(bytes),
    };

    public async ValueTask<OperationResult<MemoryPointerChainResult>> ResolvePointerChainAsync(
        MemoryPointerChainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization(cancellationToken);
        if (!ok)
            return GateCheck<MemoryPointerChainResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            OperationResult<MemoryPointerChainResult> result = await Task.Run(
                () => _scanDiscoverer.ResolvePointerChain(observation!, baseAddr, request, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
            return IsScanAuthorizationCurrent(observation!, authorizationToken)
                ? result
                : GateCheck<MemoryPointerChainResult>("discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryPointerChainResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    private (AuthorizedMemoryObservation? Observation, long BaseAddress, CancellationToken AuthorizationToken, bool Ok)
        GetScanAuthorization(CancellationToken cancellationToken)
    {
        AuthorizedObservation auth;
        CancellationToken authorizationToken;
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified
                || _authorization is null
                || _authorizationCts is null)
            {
                return (null, 0, default, false);
            }

            auth = _authorization;
            authorizationToken = _authorizationCts.Token;
        }

        // Do not hold _gate while Windows enumerates the target process module.
        // A transient zero result fails this request closed; the next request can
        // retry without blocking lifecycle evidence or revocation.
        using CancellationTokenSource resolutionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        nint baseAddress = _moduleBaseAddressResolver.Resolve(
            auth.ProcessId,
            resolutionCancellation.Token);
        cancellationToken.ThrowIfCancellationRequested();
        authorizationToken.ThrowIfCancellationRequested();
        if (baseAddress == nint.Zero)
        {
            return (null, 0, default, false);
        }

        return (
            new AuthorizedMemoryObservation(
                auth.ProcessId,
                auth.ProcessStartIdentity,
                auth.CanonicalExecutablePath,
                auth.ProductVersion,
                auth.ExecutableSha256,
                auth.ExpiresAtUtc,
                auth.ReadGate)
            {
                Generation = auth.Generation,
            },
            baseAddress.ToInt64(),
            authorizationToken,
            true);
    }

    private bool IsScanAuthorizationCurrent(
        AuthorizedMemoryObservation observation,
        CancellationToken authorizationToken)
    {
        authorizationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            authorizationToken.ThrowIfCancellationRequested();
            return _snapshot.State == GameSessionVerificationState.OfflineReplayVerified
                && _authorization is not null
                && _authorization.Generation == observation.Generation
                && ReferenceEquals(_authorization.ReadGate, observation.ReadGate);
        }
    }

    private bool IsObservationAuthorizationCurrent(
        AuthorizedObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            cancellationToken.ThrowIfCancellationRequested();
            return _snapshot.State == GameSessionVerificationState.OfflineReplayVerified
                && _authorization is not null
                && ReferenceEquals(_authorization, observation);
        }
    }

    private static MemoryValueKind ResolveValueKind(string fieldType) =>
        fieldType switch
        {
            "Float" => MemoryValueKind.FloatValue,
            "Double" => MemoryValueKind.DoubleValue,
            "Int32" => MemoryValueKind.Int32Value,
            "UInt32" => MemoryValueKind.UInt32Value,
            "Int64" => MemoryValueKind.Int64Value,
            "UInt64" => MemoryValueKind.UInt64Value,
            _ => MemoryValueKind.Bytes,
        };

    private static OperationResult<T> GateCheck<T>(string errorCode, string message)
        where T : class =>
        OperationResult.Failure<T>(new ApplicationError(errorCode, message));

    private Task StartMonitoringLifecycle(
        ManagedGameLaunchContext launch,
        CancellationTokenSource monitoringCts,
        CancellationToken token)
    {
        return Task.Run(async () =>
        {
            CancellationTokenSource? evidenceTimeoutCts = null;
            ITimer? evidenceTimeoutTimer = null;
            bool correlatedEvidenceObserved = false;
            ReplayLifecycleEvidence? pendingLifecycleEvidence = null;
            try
            {
                evidenceTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                evidenceTimeoutTimer = _timeProvider.CreateTimer(
                    static state => CancelEvidenceTimeout((CancellationTokenSource)state!),
                    evidenceTimeoutCts,
                    _options.LifecycleEvidenceTimeout,
                    Timeout.InfiniteTimeSpan);

                long currentSequence = launch.SourceSequenceBaseline;
                while (!token.IsCancellationRequested)
                {
                    CancellationToken readToken = correlatedEvidenceObserved
                        ? token
                        : evidenceTimeoutCts.Token;
                    LifecycleFeedReadResult result = await _lifecycleFeed
                        .ReadAfterAsync(currentSequence, readToken)
                        .AsTask()
                        .WaitAsync(readToken)
                        .ConfigureAwait(false);
                    if (!correlatedEvidenceObserved
                        && evidenceTimeoutCts!.IsCancellationRequested
                        && !token.IsCancellationRequested)
                    {
                        HandleLifecycleEvidenceTimeout(launch, launch.ProcessId, token);
                        return;
                    }

                    if (result.HistoryGap || result.Health != LifecycleFeedHealth.Healthy)
                    {
                        ReportMonitorFailure(launch, token);
                        return;
                    }

                    currentSequence = result.LatestSequence;

                    foreach (LifecycleFeedEvent ev in result.Events)
                    {
                        if (ev.MarkerKind == ReplayLogMarkerKind.OfflineReplayStarted
                            && ev.Cursor is not null)
                        {
                            pendingLifecycleEvidence = new ReplayLifecycleEvidence(
                                ReplayLifecycleState.OfflineReplayStarted,
                                ev.ObservedAtUtc,
                                ev.SourceTimestampUtc,
                                ReplayEvidenceSource.BlitzNativeLog,
                                ev.Cursor.SourceId.Value,
                                ev.Cursor.Generation,
                                ev.Sequence,
                                ev.Cursor.LastByteOffset,
                                ev.Provenance ?? LifecycleMarkerProvenance.Historical,
                                launch.ProcessId,
                                launch.ProcessStartIdentity,
                                launch.LaunchCorrelation);
                        }
                        else if (ev.MarkerKind == ReplayLogMarkerKind.OfflineReplayStopped)
                        {
                            // A replay stop is a terminal lifecycle event, not
                            // a transient monitor condition. Revoke immediately
                            // so no scan can continue during the evidence grace
                            // period after playback has ended.
                            if (!token.IsCancellationRequested)
                            {
                                ReportMonitorFailure(launch, token);
                            }
                            return;
                        }
                    }

                    if (pendingLifecycleEvidence is not null || correlatedEvidenceObserved)
                    {
                        GameProcessObservationResult processObservation =
                            await _processIdentityObserver
                                .ObserveAsync(launch.ProcessId, readToken)
                                .ConfigureAwait(false);
                        GameProcessEvidence? processEvidence =
                            CreateObservedProcessEvidence(launch, processObservation);

                        if (IsWindowObservationTerminalFailure(
                            correlatedEvidenceObserved,
                            processObservation.Status,
                            processEvidence is not null))
                        {
                            ReportMonitorFailure(launch, token);
                            return;
                        }

                        if (!correlatedEvidenceObserved
                            && pendingLifecycleEvidence is not null
                            && processEvidence is not null)
                        {
                            ApplyMonitorEvidence(
                                launch,
                                new GameSessionEvidence(
                                    GamePresent: true,
                                    MonitorHealthy: true,
                                    ReplayUiConfirmed: true,
                                    processEvidence,
                                    pendingLifecycleEvidence),
                                token);

                            lock (_gate)
                            {
                                correlatedEvidenceObserved =
                                    IsCurrentMonitorLocked(launch, token)
                                    && _snapshot.State == GameSessionVerificationState.OfflineReplayVerified;
                            }
                            if (correlatedEvidenceObserved)
                            {
                                pendingLifecycleEvidence = null;
                                evidenceTimeoutTimer?.Change(
                                    Timeout.InfiniteTimeSpan,
                                    Timeout.InfiniteTimeSpan);
                                LogLaunchStage("lifecycle_evidence", "verified");
                            }
                        }
                        else if (correlatedEvidenceObserved
                            && processEvidence is not null)
                        {
                            // Liveness heartbeat: the native log goes silent
                            // during playback, so a live verified game must
                            // keep its authorization fresh instead of expiring
                            // at OfflineReplayEvidenceLifetime mid-battle.
                            RefreshVerifiedEvidence(launch, processEvidence, token);
                        }
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(500),
                        _timeProvider,
                        correlatedEvidenceObserved ? token : evidenceTimeoutCts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (!token.IsCancellationRequested
                    && !correlatedEvidenceObserved
                    && evidenceTimeoutCts?.IsCancellationRequested == true)
            {
                HandleLifecycleEvidenceTimeout(launch, launch.ProcessId, token);
            }
            catch (ObjectDisposedException)
            {
                // A concurrent revoke/dispose can win before the monitor task
                // starts. The launch is already invalidated in that case.
            }
            catch (OperationCanceledException)
            {
                // Expected on revocation, disposal, or a caller cancellation.
            }
            catch
            {
                // An unexpected feed failure invalidates the evidence that
                // authorized memory reads. Expected revocation/disposal is
                // represented by cancellation and must not re-enter the revoke path.
                if (!token.IsCancellationRequested)
                {
                    ReportMonitorFailure(launch, token);
                }
            }
            finally
            {
                evidenceTimeoutTimer?.Dispose();
                evidenceTimeoutCts?.Dispose();
                monitoringCts.Dispose();
            }
        }, CancellationToken.None);
    }

    private static void CancelEvidenceTimeout(CancellationTokenSource timeoutSource)
    {
        try
        {
            timeoutSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Coordinator revocation may dispose the timeout source while the
            // provider is dispatching its callback. The launch is already stale.
        }
        catch (Exception)
        {
            // A feed cancellation callback must not fault the provider's timer
            // dispatch thread. The launch will be invalidated by its monitor.
        }
    }

    private void HandleLifecycleEvidenceTimeout(
        ManagedGameLaunchContext launch,
        int processId,
        CancellationToken monitorToken)
    {
        lock (_gate)
        {
            if (!IsCurrentMonitorLocked(launch, monitorToken)
                || _snapshot.State == GameSessionVerificationState.OfflineReplayVerified)
            {
                return;
            }

            // Keep the coordinator lock while the lease performs its bounded
            // termination wait. Replacement launches and revocation cannot
            // detach/dispose this lease between capture and termination, which
            // prevents the timed-out child becoming an orphan.
            SuspendedGameProcessLease? processLease = _activeSuspendedLease is { ProcessId: var activePid }
                && activePid == processId
                ? _activeSuspendedLease
                : null;
            bool terminated = processLease?.TryTerminateAfterHandOff() == true;

            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(3140, "ManagedLaunchLifecycleEvidenceTimeout"),
                    "Managed replay launch timed out waiting for correlated lifecycle evidence; processId={ProcessId}, processTerminated={ProcessTerminated}, timeoutSeconds={TimeoutSeconds}.",
                    processId,
                    terminated,
                    _options.LifecycleEvidenceTimeout.TotalSeconds);
            }
            Deny("launch.lifecycle_evidence_timeout");
        }
    }

    private void LogLaunchStage(
        string stage,
        string outcome,
        string? errorCode = null)
    {
        if (errorCode is null)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    new EventId(3135, "ManagedLaunchStage"),
                    "Managed replay launch stage={Stage}, outcome={Outcome}.",
                    stage,
                    outcome);
            }
        }
        else if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                new EventId(3136, "ManagedLaunchStageFailed"),
                "Managed replay launch stage={Stage}, outcome={Outcome}, errorCode={ErrorCode}.",
                stage,
                outcome,
                errorCode);
        }
    }

    private static OperationResult<GameReplayLaunchOutcome> LaunchFailure(
        ApplicationError error) =>
        OperationResult.Failure<GameReplayLaunchOutcome>(error);
}
