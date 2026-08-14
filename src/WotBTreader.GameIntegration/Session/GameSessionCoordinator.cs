using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
using WotBTreader.GameIntegration.Discovery;
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
    private readonly IReplayClockSource _replayClockSource;

    /// <summary>
    /// Upper bound on replay-clock uncertainty before
    /// <c>SameDecodedClockProven</c> may be claimed for an entity-position
    /// read. 2 s is well below the poll's 750 ms read cadence and the battle
    /// duration, while comfortably above the sub-second gate/log anchor error.
    /// </summary>
    private static readonly TimeSpan SameDecodedClockUncertaintyLimit =
        TimeSpan.FromSeconds(2);
    private readonly IThreadResumePlatform _threadResumePlatform;
    private readonly IGameProcessIdentityObserver _processIdentityObserver;
    private readonly IGuardedMemoryReaderFactory _memoryReaderFactory;
    private readonly IGameProcessModuleBaseAddressResolver _moduleBaseAddressResolver;
    private readonly IOffsetTableReader _offsetTableReader;
    private readonly IMemoryScanDiscoverer _scanDiscoverer;
    private readonly IInstructionSnapshotRunner _instructionSnapshotRunner;
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
        IReplayClockSource replayClockSource,
        IThreadResumePlatform threadResumePlatform,
        IGameProcessIdentityObserver processIdentityObserver,
        IGuardedMemoryReaderFactory memoryReaderFactory,
        IGameProcessModuleBaseAddressResolver moduleBaseAddressResolver,
        IOffsetTableReader offsetTableReader,
        IMemoryScanDiscoverer scanDiscoverer,
        MemoryScanEngine scanEngine,
        IBlitzReplayLifecycleFeed lifecycleFeed,
        IInstructionSnapshotRunner instructionSnapshotRunner)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _artifactStager = artifactStager ?? throw new ArgumentNullException(nameof(artifactStager));
        _suspendedPlatform = suspendedPlatform ?? throw new ArgumentNullException(nameof(suspendedPlatform));
        _correlationRegistrar = correlationRegistrar ?? throw new ArgumentNullException(nameof(correlationRegistrar));
        _replayClockSource = replayClockSource ?? throw new ArgumentNullException(nameof(replayClockSource));
        _threadResumePlatform = threadResumePlatform ?? throw new ArgumentNullException(nameof(threadResumePlatform));
        _processIdentityObserver = processIdentityObserver ?? throw new ArgumentNullException(nameof(processIdentityObserver));
        _memoryReaderFactory = memoryReaderFactory ?? throw new ArgumentNullException(nameof(memoryReaderFactory));
        _moduleBaseAddressResolver = moduleBaseAddressResolver ?? throw new ArgumentNullException(nameof(moduleBaseAddressResolver));
        _offsetTableReader = offsetTableReader ?? throw new ArgumentNullException(nameof(offsetTableReader));
        _scanDiscoverer = scanDiscoverer ?? throw new ArgumentNullException(nameof(scanDiscoverer));
        _scanEngine = scanEngine ?? throw new ArgumentNullException(nameof(scanEngine));
        _lifecycleFeed = lifecycleFeed ?? throw new ArgumentNullException(nameof(lifecycleFeed));
        _instructionSnapshotRunner = instructionSnapshotRunner
            ?? throw new ArgumentNullException(nameof(instructionSnapshotRunner));
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

    private void ReportReplayCompleted(
        ManagedGameLaunchContext launch,
        CancellationToken monitorToken)
    {
        lock (_gate)
        {
            if (!IsCurrentMonitorLocked(launch, monitorToken))
            {
                return;
            }

            // Distinguishable terminal reason: tooling can tell "the replay
            // finished normally" (results screen observed) from a broken
            // monitor (evidence.monitor_unhealthy) or an expired lease
            // (EvidenceStale when no completion marker ever arrived).
            Deny("evidence.replay_completed");
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
            || request.Addresses.Any(static address => address <= 0)
            || !ValueSizeMatchesKind(request.ValueKind, request.ValueSize))
        {
            return OperationResult.Failure<MemoryReadResult>(
                new ApplicationError(
                    "discover.invalid_options",
                    "The read request must contain between 1 and " + MaximumReadAddresses
                        + " positive addresses, a value size of 4 or 8, and a value kind"
                        + " whose width matches the requested size."));
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

    /// <summary>
    /// Attests same-decoded-clock alignment for an entity-position read from
    /// the battle session's replay-clock segments: the clock source must have
    /// at least one anchor, the snapshot must not be stale, and the reported
    /// uncertainty must be within <see cref="SameDecodedClockUncertaintyLimit"/>.
    /// A null session id never claims the flag.
    /// </summary>
    private async ValueTask<bool> IsSameDecodedClockAsync(
        BattleSessionId? battleSessionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (battleSessionId is null)
        {
            return false;
        }

        OperationResult<ReplayClockSnapshot> snapshot = await _replayClockSource
            .GetSnapshotAsync(battleSessionId.Value, observedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.IsSuccess || snapshot.Value is null)
        {
            return false;
        }

        if (snapshot.Value.Quality == ReplayClockQuality.Stale ||
            snapshot.Value.Uncertainty is null ||
            snapshot.Value.Uncertainty > SameDecodedClockUncertaintyLimit)
        {
            return false;
        }

        return true;
    }

    public async ValueTask<OperationResult<EntityPositionReadResult>> ReadEntityPositionAsync(
        EntityPositionReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<EntityPositionReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return IsScanAuthorizationCurrent(observation, authorizationToken)
                    ? OperationResult.Success(new EntityPositionReadResult(
                        _timeProvider.GetUtcNow(),
                        observation.ProductVersion,
                        Type10EntityPositionStatus.UnsupportedBuild,
                        request.EntityId,
                        null,
                        null,
                        null,
                        null,
                        "build-identity",
                        Attempts: 0,
                        NodesVisited: 0,
                        ModuleRooted: false,
                        EntityIdentityRevalidated: false,
                        ConsistentDoubleRead: false,
                        HardwareAtomicReadProven: false,
                        SameDecodedClockProven: false))
                    : GateCheck<EntityPositionReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<EntityPositionReadResult>(
                    new ApplicationError(
                        "discover.entity_position.read_unavailable",
                        "The guarded entity-position reader is unavailable."));
            }

            OperationResult<Type10EntityPositionResult> resolveResult =
                await readerResult.Value.ResolveEntityPositionAsync(
                    (nint)baseAddress,
                    request.EntityId,
                    layout,
                    readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<EntityPositionReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (!resolveResult.IsSuccess || resolveResult.Value is null)
            {
                return OperationResult.Failure<EntityPositionReadResult>(
                    resolveResult.Error ?? new ApplicationError(
                        "discover.entity_position.read_failed",
                        "The entity-position read failed."));
            }

            Type10EntityPositionResult resolved = resolveResult.Value;
            bool sameDecodedClockProven = await IsSameDecodedClockAsync(
                request.BattleSessionId,
                _timeProvider.GetUtcNow(),
                readCancellation.Token).ConfigureAwait(false);
            return OperationResult.Success(new EntityPositionReadResult(
                _timeProvider.GetUtcNow(),
                layout.GameVersion,
                resolved.Status,
                resolved.EntityId,
                resolved.X,
                resolved.Y,
                resolved.Z,
                resolved.EntitySource,
                resolved.FailureStage,
                resolved.Attempts,
                resolved.NodesVisited,
                resolved.ModuleRooted,
                resolved.EntityIdentityRevalidated,
                resolved.ConsistentDoubleRead,
                resolved.HardwareAtomicReadProven,
                SameDecodedClockProven: sameDecodedClockProven));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<EntityPositionReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<EntityRecordRegionReadResult>> ReadEntityRegionAsync(
        EntityRecordRegionReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RegionLength < 1 ||
            request.RegionLength > EntityRecordRegionReadRequest.MaxLength)
        {
            return OperationResult.Failure<EntityRecordRegionReadResult>(
                new ApplicationError(
                    "discover.entity_region.invalid_length",
                    "The region length must be within 1..4096 bytes."));
        }

        if (request.AvatarCandidateIndex is int avatarIndex &&
            (avatarIndex < 0 || avatarIndex >= EntityRecordRegionReadRequest.MaxAvatarCandidates))
        {
            return OperationResult.Failure<EntityRecordRegionReadResult>(
                new ApplicationError(
                    "discover.entity_region.invalid_avatar_candidate",
                    $"The avatar-stats candidate index must be within 0..{EntityRecordRegionReadRequest.MaxAvatarCandidates - 1}."));
        }

        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<EntityRecordRegionReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return IsScanAuthorizationCurrent(observation, authorizationToken)
                    ? OperationResult.Success(new EntityRecordRegionReadResult(
                        _timeProvider.GetUtcNow(),
                        observation.ProductVersion,
                        Type10EntityPositionStatus.UnsupportedBuild,
                        request.EntityId,
                        null,
                        null,
                        "build-identity",
                        Attempts: 0,
                        NodesVisited: 0,
                        ModuleRooted: false,
                        EntityIdentityRevalidated: false,
                        ConsistentDoubleRead: false,
                        SameDecodedClockProven: false))
                    : GateCheck<EntityRecordRegionReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<EntityRecordRegionReadResult>(
                    new ApplicationError(
                        "discover.entity_region.read_unavailable",
                        "The guarded entity-region reader is unavailable."));
            }

            // Resolve the entity's ring-record address under the same lease
            // (the address stays coordinator-owned; only bytes leave). The
            // avatar-stats anchor SKIPS the entity-ID resolver entirely: it
            // ignores EntityId and scans for the entity-Avatar vftable
            // instead (the L3 damage-dealt family is on that object, not the
            // entity record).
            Type10EntityPositionAddressResult? resolved = null;
            if (request.RegionAnchor != EntityRecordRegionAnchor.AvatarStats)
            {
                OperationResult<Type10EntityPositionAddressResult> resolveResult =
                    await readerResult.Value.ResolveEntityPositionAddressAsync(
                        (nint)baseAddress,
                        request.EntityId,
                        layout,
                        readCancellation.Token).ConfigureAwait(false);
                if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                {
                    return GateCheck<EntityRecordRegionReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
                }

                if (!resolveResult.IsSuccess || resolveResult.Value is null)
                {
                    return OperationResult.Failure<EntityRecordRegionReadResult>(
                        resolveResult.Error ?? new ApplicationError(
                            "discover.entity_region.resolve_failed",
                            "The entity ring-record address resolution failed."));
                }

                resolved = resolveResult.Value;
                if (resolved.Status != Type10EntityPositionStatus.Resolved ||
                    resolved.RecordAddress is null)
                {
                    return OperationResult.Success(new EntityRecordRegionReadResult(
                        _timeProvider.GetUtcNow(),
                        layout.GameVersion,
                        resolved.Status,
                        request.EntityId,
                        null,
                        null,
                        resolved.FailureStage,
                        resolved.Attempts,
                        resolved.NodesVisited,
                        resolved.ModuleRooted,
                        EntityIdentityRevalidated: false,
                        ConsistentDoubleRead: false,
                        SameDecodedClockProven: false));
                }
            }

            // Result metadata for the chosen anchor (entity path vs avatar
            // scan) — consumed by the success return below.
            string? anchorFailureStage = resolved?.FailureStage;
            int anchorAttempts = resolved?.Attempts ?? 0;
            int anchorNodesVisited = resolved?.NodesVisited ?? 0;
            bool anchorModuleRooted = resolved?.ModuleRooted ?? true;

            // Replay-clock label + same-decoded-clock attestation (one call).
            double? replayTimeSeconds = null;
            bool sameDecodedClockProven = false;
            if (request.BattleSessionId is not null)
            {
                OperationResult<ReplayClockSnapshot> clock = await _replayClockSource
                    .GetSnapshotAsync(
                        request.BattleSessionId.Value,
                        _timeProvider.GetUtcNow(),
                        readCancellation.Token)
                    .ConfigureAwait(false);
                if (clock.IsSuccess && clock.Value is not null &&
                    clock.Value.Quality != ReplayClockQuality.Stale &&
                    clock.Value.Uncertainty is not null &&
                    clock.Value.Uncertainty <= SameDecodedClockUncertaintyLimit)
                {
                    sameDecodedClockProven = true;
                    replayTimeSeconds = clock.Value.EstimatedReplayTime.TotalSeconds;
                }
            }

            // Anchor the dump: the movement ring record (the resolver's
            // target), the per-entity tank record at [entity+0x3C], or the
            // entity base record itself. The coordinator performs the
            // tank-record dereference itself under the same guarded lease;
            // only the resulting bytes leave. The avatar-stats anchor is
            // DIFFERENT: it ignores EntityId and runs the gated vftable AOB
            // scan for the entity-factory Avatar object (the L3 damage-dealt
            // family — the camera chain's avatarAddress is
            // AvatarControllerReplay, NOT this object; see the L3 plan).
            uint? regionBaseAddress;
            int avatarCandidateCount = 0;
            if (request.RegionAnchor == EntityRecordRegionAnchor.AvatarStats)
            {
                AvatarStatsResolution avatar = await ResolveAvatarStatsAddressAsync(
                    readerResult.Value,
                    observation!,
                    baseAddress,
                    request.AvatarCandidateIndex,
                    readCancellation.Token).ConfigureAwait(false);
                if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                {
                    return GateCheck<EntityRecordRegionReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
                }
                avatarCandidateCount = avatar.CandidateCount;
                anchorFailureStage = avatar.FailureStage;
                anchorAttempts = avatar.Attempts;
                anchorNodesVisited = 0;
                anchorModuleRooted = true;
                if (avatar.Address is not uint avatarAddress)
                {
                    return OperationResult.Success(new EntityRecordRegionReadResult(
                        _timeProvider.GetUtcNow(),
                        layout.GameVersion,
                        avatar.Status,
                        request.EntityId,
                        null,
                        null,
                        avatar.FailureStage,
                        avatar.Attempts,
                        0,
                        true,
                        EntityIdentityRevalidated: false,
                        ConsistentDoubleRead: false,
                        SameDecodedClockProven: sameDecodedClockProven,
                        AvatarCandidateCount: avatarCandidateCount));
                }
                regionBaseAddress = avatarAddress + EntityRecordRegionReadRequest.AvatarStatsQuadOffset;
            }
            else
            {
                regionBaseAddress = request.RegionAnchor switch
                {
                    EntityRecordRegionAnchor.RingRecord => resolved!.RecordAddress,
                    EntityRecordRegionAnchor.EntityTankRecord => await ResolveTankRecordAddressAsync(
                        readerResult.Value,
                        resolved!.EntityAddress,
                        readCancellation.Token).ConfigureAwait(false),
                    // The entity base record itself: the statically-verified
                    // HP fields ([entity+0xB8] int16 current health, +0xBA
                    // alive byte, +0x11E healing int16) live on this record,
                    // not the tank record at [entity+0x3C].
                    EntityRecordRegionAnchor.EntityBase => resolved!.EntityAddress,
                    _ => null,
                };
                if (regionBaseAddress is null)
                {
                    return OperationResult.Failure<EntityRecordRegionReadResult>(
                        new ApplicationError(
                            "discover.entity_region.tank_record_unresolved",
                            "The region anchor could not be resolved (missing entity base or invalid tank-record pointer).",
                            Retryable: false));
                }
            }

            OperationResult<byte[]> regionResult = await readerResult.Value.ReadAsync(
                (nint)regionBaseAddress.Value,
                request.RegionLength,
                readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<EntityRecordRegionReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (!regionResult.IsSuccess || regionResult.Value is null)
            {
                return OperationResult.Failure<EntityRecordRegionReadResult>(
                    regionResult.Error ?? new ApplicationError(
                        "discover.entity_region.read_failed",
                        "The entity region read failed."));
            }

            return OperationResult.Success(new EntityRecordRegionReadResult(
                _timeProvider.GetUtcNow(),
                layout.GameVersion,
                Type10EntityPositionStatus.Resolved,
                request.EntityId,
                replayTimeSeconds,
                regionResult.Value,
                anchorFailureStage,
                anchorAttempts,
                anchorNodesVisited,
                anchorModuleRooted,
                // The address path does not double-collect position bytes, so
                // these evidence flags are not claimable from a region dump.
                EntityIdentityRevalidated: false,
                ConsistentDoubleRead: false,
                SameDecodedClockProven: sameDecodedClockProven,
                AvatarCandidateCount: avatarCandidateCount));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<EntityRecordRegionReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    /// <summary>
    /// Reads bounded regions of up to <see cref="EntityRegionsReadRequest.MaxEntities"/>
    /// entities in one round trip with ONE replay-clock attestation for the
    /// batch (the per-frame live read surface design,
    /// docs/operations/batch-entity-read-design.md). Per-entity statuses are
    /// authoritative: an unresolved entity fails only itself; the retryable
    /// pre-battle phase (<c>ReplaySessionInactive</c>) fails the WHOLE batch
    /// — phase is global, a frame cannot be half-timed. Read discipline:
    /// gate + build identity first (no reads on a gate violation), resolve
    /// ALL addresses, read ALL regions, then ONE post-read clock snapshot
    /// that bounds the batch.
    /// </summary>
    public async ValueTask<OperationResult<EntityRegionsReadResult>> ReadEntityRegionsAsync(
        EntityRegionsReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Entities is null ||
            request.Entities.Count is < 1 or > EntityRegionsReadRequest.MaxEntities)
        {
            return OperationResult.Failure<EntityRegionsReadResult>(
                new ApplicationError(
                    "discover.entity_regions.invalid_request",
                    $"The batch must contain 1..{EntityRegionsReadRequest.MaxEntities} entities."));
        }

        int totalBytes = 0;
        foreach (EntityRegionReadRequestItem entity in request.Entities)
        {
            if (entity.RegionLength < 1 ||
                entity.RegionLength > EntityRecordRegionReadRequest.MaxLength ||
                entity.RegionAnchor is < EntityRecordRegionAnchor.RingRecord or > EntityRecordRegionAnchor.EntityBase)
            {
                return OperationResult.Failure<EntityRegionsReadResult>(
                    new ApplicationError(
                        "discover.entity_regions.invalid_request",
                        "Each entity region must be 1..4096 bytes with a known region anchor."));
            }

            if (entity.EntityBaseRegionLength is int entityBaseLength &&
                (entityBaseLength < 1 || entityBaseLength > EntityRecordRegionReadRequest.MaxLength))
            {
                return OperationResult.Failure<EntityRegionsReadResult>(
                    new ApplicationError(
                        "discover.entity_regions.invalid_request",
                        "The entity-base region length must be 1..4096 bytes when supplied."));
            }

            totalBytes += entity.RegionLength + (entity.EntityBaseRegionLength ?? 0);
        }

        if (totalBytes > EntityRegionsReadRequest.MaxTotalBytes)
        {
            return OperationResult.Failure<EntityRegionsReadResult>(
                new ApplicationError(
                    "discover.entity_regions.invalid_request",
                    "The total region bytes exceed the 16 KB batch bound."));
        }

        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<EntityRegionsReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return IsScanAuthorizationCurrent(observation, authorizationToken)
                    ? OperationResult.Success(BuildBatchResult(
                        layout,
                        request.Entities,
                        Type10EntityPositionStatus.UnsupportedBuild,
                        replayTimeSeconds: null,
                        sameDecodedClockProven: false))
                    : GateCheck<EntityRegionsReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<EntityRegionsReadResult>(
                    new ApplicationError(
                        "discover.entity_regions.read_unavailable",
                        "The guarded entity-region reader is unavailable."));
            }

            // Phase 1: resolve ALL entity addresses under the same lease.

            return await ReadEntityRegionsCoreAsync(
                request,
                observation,
                baseAddress,
                readerResult.Value,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<EntityRegionsReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<EntityRosterReadResult>> EnumerateEntitiesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<EntityRosterReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return OperationResult.Success(new EntityRosterReadResult(
                    _timeProvider.GetUtcNow(),
                    layout.GameVersion,
                    Type10EntityPositionStatus.UnsupportedBuild,
                    "build-identity",
                    CandidatesSeen: 0,
                    FilteredOut: 0,
                    ModuleRooted: false,
                    TraversalLimited: false,
                    EntityIds: []));
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<EntityRosterReadResult>(
                    new ApplicationError(
                        "discover.entity_roster.read_unavailable",
                        "The guarded roster reader is unavailable."));
            }

            return await EnumerateEntitiesCoreAsync(
                observation,
                baseAddress,
                readerResult.Value,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<EntityRosterReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    private async ValueTask<OperationResult<EntityRegionsReadResult>> ReadEntityRegionsCoreAsync(
        EntityRegionsReadRequest request,
        AuthorizedMemoryObservation observation,
        long baseAddress,
        IAuthorizedMemoryReader reader,
        Type10EntityPositionLayout layout,
        CancellationToken authorizationToken,
        CancellationToken cancellationToken)
    {
        // An unresolved entity fails only itself; the retryable
        // pre-battle phase fails the whole batch. The batch pass window
        // (first resolve -> last read) is the item-7 verification
        // window measurement.
        DateTimeOffset batchStartedAt = _timeProvider.GetUtcNow();
        var results = new EntityRegionReadResultItem[request.Entities.Count];
        var resolvedItems =
            new List<(int Index, EntityRegionReadRequestItem Item, Type10EntityPositionAddressResult Address)>(
                request.Entities.Count);
        bool inactive = false;
        for (int i = 0; i < request.Entities.Count; i++)
        {
            EntityRegionReadRequestItem entity = request.Entities[i];
            OperationResult<Type10EntityPositionAddressResult> resolveResult =
                await reader.ResolveEntityPositionAddressAsync(
                    (nint)baseAddress,
                    entity.EntityId,
                    layout,
                    cancellationToken).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<EntityRegionsReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (resolveResult.IsSuccess && resolveResult.Value is not null)
            {
                Type10EntityPositionAddressResult resolved = resolveResult.Value;
                if (resolved.Status == Type10EntityPositionStatus.ReplaySessionInactive)
                {
                    inactive = true;
                    break;
                }

                if (resolved.Status == Type10EntityPositionStatus.Resolved &&
                    resolved.RecordAddress is not null)
                {
                    resolvedItems.Add((i, entity, resolved));
                    continue;
                }

                results[i] = new EntityRegionReadResultItem(
                    entity.EntityId,
                    resolved.Status,
                    ReplayTimeSeconds: null,
                    RegionBytes: null,
                    resolved.FailureStage,
                    resolved.Attempts,
                    resolved.NodesVisited,
                    resolved.ModuleRooted,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false);
            }
            else
            {
                results[i] = new EntityRegionReadResultItem(
                    entity.EntityId,
                    Type10EntityPositionStatus.ReadFailed,
                    ReplayTimeSeconds: null,
                    RegionBytes: null,
                    FailureStage: resolveResult.Error?.Code ?? "resolve-failed",
                    Attempts: 0,
                    NodesVisited: 0,
                    ModuleRooted: false,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false);
            }
        }

        if (inactive)
        {
            return OperationResult.Success(BuildBatchResult(
                layout,
                request.Entities,
                Type10EntityPositionStatus.ReplaySessionInactive,
                replayTimeSeconds: null,
                sameDecodedClockProven: false));
        }

        // Phase 2: read ALL regions.
        foreach ((int index, EntityRegionReadRequestItem entity, Type10EntityPositionAddressResult resolved) in resolvedItems)
        {
            uint? regionBaseAddress = entity.RegionAnchor switch
            {
                EntityRecordRegionAnchor.RingRecord => resolved.RecordAddress,
                EntityRecordRegionAnchor.EntityTankRecord => await ResolveTankRecordAddressAsync(
                    reader,
                    resolved.EntityAddress,
                    cancellationToken).ConfigureAwait(false),
                EntityRecordRegionAnchor.EntityBase => resolved.EntityAddress,
                _ => null,
            };

            if (regionBaseAddress is null)
            {
                results[index] = new EntityRegionReadResultItem(
                    entity.EntityId,
                    Type10EntityPositionStatus.ReadFailed,
                    ReplayTimeSeconds: null,
                    RegionBytes: null,
                    FailureStage: "region-anchor",
                    resolved.Attempts,
                    resolved.NodesVisited,
                    resolved.ModuleRooted,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false);
                continue;
            }

            // Branch B (item 7): the region span is read TWICE per attempt
            // with an UnstableSnapshot-style retry (the resolver template).
            // The record bytes are the stability witness — for ring records
            // the leading time field is inside the span, so any ring advance
            // or mid-write changes the bytes and retries. The retry is
            // bounded by layout.MaxAttempts and fail-closed: an exhausted
            // region is an item failure (stage region-unstable-snapshot),
            // never a silent single read. Branch B step 2 exposes the
            // delivered pair as ConsistentDoubleRead and reports whether a
            // mismatched attempt was observed before the stable pair won.
            byte[]? regionBytes = null;
            string? regionFailureStage = null;
            int regionReadAttempts = 0;
            bool regionTearObserved = false;
            int maxRegionAttempts = Math.Max(1, layout.MaxAttempts);
            for (int attempt = 1; attempt <= maxRegionAttempts && regionBytes is null; attempt++)
            {
                regionReadAttempts = attempt;
                OperationResult<byte[]> firstRegionRead = await reader.ReadAsync(
                    (nint)regionBaseAddress.Value,
                    entity.RegionLength,
                    cancellationToken).ConfigureAwait(false);
                if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                {
                    return GateCheck<EntityRegionsReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
                }

                if (!firstRegionRead.IsSuccess || firstRegionRead.Value is null)
                {
                    regionFailureStage = "region-read";
                    break;
                }

                OperationResult<byte[]> secondRegionRead = await reader.ReadAsync(
                    (nint)regionBaseAddress.Value,
                    entity.RegionLength,
                    cancellationToken).ConfigureAwait(false);
                if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                {
                    return GateCheck<EntityRegionsReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
                }

                if (!secondRegionRead.IsSuccess || secondRegionRead.Value is null)
                {
                    regionFailureStage = "region-read";
                    break;
                }

                if (firstRegionRead.Value.AsSpan().SequenceEqual(secondRegionRead.Value))
                {
                    regionBytes = firstRegionRead.Value;
                }
                else
                {
                    regionTearObserved = true;
                    if (attempt == maxRegionAttempts)
                    {
                        regionFailureStage = "region-unstable-snapshot";
                    }
                }
            }

            if (regionBytes is null)
            {
                results[index] = new EntityRegionReadResultItem(
                    entity.EntityId,
                    Type10EntityPositionStatus.ReadFailed,
                    ReplayTimeSeconds: null,
                    RegionBytes: null,
                    FailureStage: regionFailureStage ?? "region-read",
                    resolved.Attempts,
                    resolved.NodesVisited,
                    resolved.ModuleRooted,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    RegionReadAttempts: regionReadAttempts,
                    RegionTearObserved: regionTearObserved);
                continue;
            }

            // L1 additive: when the item asked for an entity-base region,
            // read it at the RESOLVED entity address under the same lease
            // (no second resolve, no second clock snapshot — the ONE batch
            // attestation still labels the whole item). A failed entity-base
            // read fails only the health fields, never the ring region.
            byte[]? entityBaseBytes = null;
            string? entityBaseFailureStage = null;
            int entityBaseAttempts = 0;
            bool entityBaseTearObserved = false;
            if (entity.EntityBaseRegionLength is int entityBaseLength)
            {
                if (resolved.EntityAddress is not uint entityAddress)
                {
                    entityBaseFailureStage = "entity-base-anchor";
                }
                else
                {
                    // Branch B (item 7): the entity-base span gets the same
                    // double-read discipline; a mid-write tear retries, and
                    // exhaustion fails only the health fields.
                    int maxEntityBaseAttempts = Math.Max(1, layout.MaxAttempts);
                    for (int attempt = 1; attempt <= maxEntityBaseAttempts && entityBaseBytes is null; attempt++)
                    {
                        entityBaseAttempts = attempt;
                        OperationResult<byte[]> entityBaseFirst = await reader.ReadAsync(
                            (nint)entityAddress,
                            entityBaseLength,
                            cancellationToken).ConfigureAwait(false);
                        if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                        {
                            return GateCheck<EntityRegionsReadResult>(
                                "discover.gate_not_satisfied",
                                "The offline-session gate is no longer satisfied.");
                        }

                        if (!entityBaseFirst.IsSuccess || entityBaseFirst.Value is null)
                        {
                            entityBaseFailureStage = "entity-base-read";
                            break;
                        }

                        OperationResult<byte[]> entityBaseSecond = await reader.ReadAsync(
                            (nint)entityAddress,
                            entityBaseLength,
                            cancellationToken).ConfigureAwait(false);
                        if (!IsScanAuthorizationCurrent(observation, authorizationToken))
                        {
                            return GateCheck<EntityRegionsReadResult>(
                                "discover.gate_not_satisfied",
                                "The offline-session gate is no longer satisfied.");
                        }

                        if (!entityBaseSecond.IsSuccess || entityBaseSecond.Value is null)
                        {
                            entityBaseFailureStage = "entity-base-read";
                            break;
                        }

                        if (entityBaseFirst.Value.AsSpan().SequenceEqual(entityBaseSecond.Value))
                        {
                            entityBaseBytes = entityBaseFirst.Value;
                        }
                        else
                        {
                            entityBaseTearObserved = true;
                            if (attempt == maxEntityBaseAttempts)
                            {
                                entityBaseFailureStage = "entity-base-unstable-snapshot";
                            }
                        }
                    }
                }
            }

            results[index] = new EntityRegionReadResultItem(
                entity.EntityId,
                Type10EntityPositionStatus.Resolved,
                ReplayTimeSeconds: null,
                RegionBytes: regionBytes,
                FailureStage: null,
                resolved.Attempts,
                resolved.NodesVisited,
                resolved.ModuleRooted,
                EntityIdentityRevalidated: false,
                ConsistentDoubleRead: true,
                EntityBaseRegionBytes: entityBaseBytes,
                EntityBaseFailureStage: entityBaseFailureStage,
                EntityBaseAttempts: entityBaseAttempts,
                RegionReadAttempts: regionReadAttempts,
                RegionTearObserved: regionTearObserved,
                EntityBaseTearObserved: entityBaseTearObserved);
        }

        // Phase 3: ONE replay-clock label + same-decoded-clock
        // attestation for the whole batch (post-read snapshot bounds the
        // batch). Per-entity time mirrors carry the batch label; only
        // the batch attestation is load-bearing. The snapshot moment is
        // measured so the label-vs-read gap is quantifiable.
        DateTimeOffset batchEndedAt = _timeProvider.GetUtcNow();
        double? replayTimeSeconds = null;
        bool sameDecodedClockProven = false;
        DateTimeOffset? clockSnapshotAt = null;
        if (request.BattleSessionId is not null)
        {
            clockSnapshotAt = _timeProvider.GetUtcNow();
            OperationResult<ReplayClockSnapshot> clock = await _replayClockSource
                .GetSnapshotAsync(
                    request.BattleSessionId.Value,
                    clockSnapshotAt.Value,
                    cancellationToken)
                .ConfigureAwait(false);
            if (clock.IsSuccess && clock.Value is not null &&
                clock.Value.Quality != ReplayClockQuality.Stale &&
                clock.Value.Uncertainty is not null &&
                clock.Value.Uncertainty <= SameDecodedClockUncertaintyLimit)
            {
                sameDecodedClockProven = true;
                replayTimeSeconds = clock.Value.EstimatedReplayTime.TotalSeconds;
            }
        }

        return OperationResult.Success(new EntityRegionsReadResult(
            _timeProvider.GetUtcNow(),
            layout.GameVersion,
            Type10EntityPositionStatus.Resolved,
            replayTimeSeconds,
            sameDecodedClockProven,
            results
                .Select(item => item with { ReplayTimeSeconds = replayTimeSeconds })
                .ToList(),
            new EntityRegionsReadMeasurement(
                batchStartedAt,
                batchEndedAt,
                clockSnapshotAt)));

    }

    private async ValueTask<OperationResult<EntityRosterReadResult>> EnumerateEntitiesCoreAsync(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        IAuthorizedMemoryReader reader,
        Type10EntityPositionLayout layout,
        CancellationToken authorizationToken,
        CancellationToken cancellationToken)
    {

        OperationResult<EntityRosterResult> enumerateResult = await reader
            .EnumerateEntitiesAsync(
                (nint)baseAddress,
                layout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsScanAuthorizationCurrent(observation, authorizationToken))
        {
            return GateCheck<EntityRosterReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }

        if (!enumerateResult.IsSuccess || enumerateResult.Value is null)
        {
            return OperationResult.Failure<EntityRosterReadResult>(
                enumerateResult.Error ?? new ApplicationError(
                    "discover.entity_roster.read_failed",
                    "The roster enumeration read failed."));
        }

        EntityRosterResult roster = enumerateResult.Value;
        // Privacy boundary: addresses stay inside the coordinator — the
        // result carries ids ONLY (plus the filter-precision counters
        // the live rehearsal cross-checks against the decoded roster).
        return OperationResult.Success(new EntityRosterReadResult(
            _timeProvider.GetUtcNow(),
            layout.GameVersion,
            roster.Status,
            roster.FailureStage,
            roster.CandidatesSeen,
            roster.FilteredOut,
            roster.ModuleRooted,
            roster.TraversalLimited,
            roster.Entities?.Select(entry => entry.EntityId).ToList() ?? []));

    }

    public async ValueTask<OperationResult<LiveFrameReadResult>> ReadLiveFrameAsync(
        LiveFrameReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<LiveFrameReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return OperationResult.Success(new LiveFrameReadResult(
                    _timeProvider.GetUtcNow(),
                    layout.GameVersion,
                    Type10EntityPositionStatus.UnsupportedBuild,
                    "build-identity",
                    ReplayTimeSeconds: null,
                    SameDecodedClockProven: false,
                    Camera: null,
                    Tanks: [],
                    RosterCandidatesSeen: 0,
                    RosterFilteredOut: 0));
            }

            Type10CameraPoseLayout cameraLayout = Type10CameraPoseLayout.WotBlitz1119010;

            // The frame's read-pass window (item-7 budget): anchor scan start
            // through camera-pose read end, with the ONE G2 snapshot moment
            // carried by the batch. Honest wall-clock spans, not claims.
            DateTimeOffset frameStartedAt = _timeProvider.GetUtcNow();

            // Camera anchor first: the anchor scan never touches the guarded
            // reader, so it runs before the single lease is opened (a missing
            // anchor must not justify opening one). The frame still serves
            // roster + batch when the anchor is missing; the camera simply
            // reports AnchorNotFound.
            (long avatarAddress, uint matchedAvatarRva) = await FindAvatarAnchorAsync(
                observation,
                baseAddress,
                cameraLayout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<LiveFrameReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            // ONE guarded reader lease for the whole frame: roster, batch,
            // and camera share it, so the frame is one coherent read window.
            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<LiveFrameReadResult>(
                    new ApplicationError(
                        "discover.live_frame.read_unavailable",
                        "The guarded live-frame reader is unavailable."));
            }

            IAuthorizedMemoryReader reader = readerResult.Value;

            // 1. Enumerate the avatar-family roster (the live counterpart to
            //    the decoded participants table). A roster that cannot be
            //    established fails the whole frame — the frame is nothing
            //    without its entities.
            OperationResult<EntityRosterReadResult> rosterResult = await EnumerateEntitiesCoreAsync(
                observation,
                baseAddress,
                reader,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<LiveFrameReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (!rosterResult.IsSuccess || rosterResult.Value is null)
            {
                return OperationResult.Failure<LiveFrameReadResult>(
                    rosterResult.Error ?? new ApplicationError(
                        "discover.live_frame.roster_failed",
                        "The roster enumeration failed."));
            }

            EntityRosterReadResult roster = rosterResult.Value;
            if (roster.Status != Type10EntityPositionStatus.Resolved)
            {
                return OperationResult.Success(new LiveFrameReadResult(
                    _timeProvider.GetUtcNow(),
                    layout.GameVersion,
                    roster.Status,
                    roster.FailureStage,
                    ReplayTimeSeconds: null,
                    SameDecodedClockProven: false,
                    Camera: null,
                    Tanks: [],
                    RosterCandidatesSeen: roster.CandidatesSeen,
                    RosterFilteredOut: roster.FilteredOut));
            }

            // 2. Batch-read every roster entity's ring record AND its
            //    entity-base region under the same authorization (ring
            //    record must reach hull yaw at +0x30, so 0x40 bytes;
            //    entity-base must reach max-health int16 at +0x11C, so
            //    0x120 bytes). Both read under ONE resolve and ONE G2
            //    clock attestation when a battle session id is supplied.
            var batchItems = roster.EntityIds
                .Select(entityId => new EntityRegionReadRequestItem(
                    entityId,
                    RegionLength: 0x40,
                    EntityRecordRegionAnchor.RingRecord,
                    EntityBaseRegionLength: 0x120))
                .ToList();
            OperationResult<EntityRegionsReadResult> batchResult = await ReadEntityRegionsCoreAsync(
                new EntityRegionsReadRequest(batchItems, request.BattleSessionId),
                observation,
                baseAddress,
                reader,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<LiveFrameReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (!batchResult.IsSuccess || batchResult.Value is null)
            {
                return OperationResult.Failure<LiveFrameReadResult>(
                    batchResult.Error ?? new ApplicationError(
                        "discover.live_frame.batch_failed",
                        "The roster batch read failed."));
            }

            EntityRegionsReadResult batch = batchResult.Value;
            if (batch.Status != Type10EntityPositionStatus.Resolved)
            {
                return OperationResult.Success(new LiveFrameReadResult(
                    _timeProvider.GetUtcNow(),
                    layout.GameVersion,
                    batch.Status,
                    batch.Status switch
                    {
                        Type10EntityPositionStatus.UnsupportedBuild => "build-identity",
                        Type10EntityPositionStatus.ReplaySessionInactive => "pre-battle-inactive",
                        _ => "batch",
                    },
                    batch.ReplayTimeSeconds,
                    batch.SameDecodedClockProven,
                    Camera: null,
                    Tanks: [],
                    RosterCandidatesSeen: roster.CandidatesSeen,
                    RosterFilteredOut: roster.FilteredOut));
            }

            // 3. Camera pose (CAM-001 chain) — independent of the entity
            //    maps; its wall-clock proximity to the batch is bounded by
            //    the batch read-pass window (the frame's timing budget).
            OperationResult<CameraPoseReadResult> cameraResult = avatarAddress == 0
                ? OperationResult.Success(CameraAnchorNotFound(cameraLayout))
                : await ReadCameraPoseCoreAsync(
                    observation,
                    baseAddress,
                    reader,
                    avatarAddress,
                    matchedAvatarRva,
                    cameraLayout,
                    authorizationToken,
                    readCancellation.Token).ConfigureAwait(false);
            CameraPoseReadResult? camera = cameraResult.IsSuccess ? cameraResult.Value : null;
            DateTimeOffset frameEndedAt = _timeProvider.GetUtcNow();

            // 4. Assemble: decode position (+0x10) and hull yaw (+0x30)
            //    from each resolved ring-record region, plus live health
            //    from the entity-base region (L1). Health fields stay honest
            //    nulls when the entity-base read failed or decoded invalid.
            //    Per-tank statuses are authoritative; a region that resolved
            //    but failed to decode its position is a per-tank failure,
            //    not a frame failure.
            var tanks = new List<LiveFrameTankState>(batch.Regions.Count);
            foreach (EntityRegionReadResultItem region in batch.Regions)
            {
                float? x = null;
                float? y = null;
                float? z = null;
                float? yaw = null;
                string? failureStage = region.FailureStage;
                if (region.Status == Type10EntityPositionStatus.Resolved &&
                    region.RegionBytes is not null)
                {
                    (float X, float Y, float Z)? position =
                        RingRecordRegion.TryReadPosition(region.RegionBytes);
                    if (position is null)
                    {
                        failureStage = "region-position-decode";
                    }
                    else
                    {
                        (x, y, z) = position.Value;
                        yaw = RingRecordRegion.TryReadYaw(region.RegionBytes);
                    }
                }

                float? hpCurrent = null;
                float? hpMax = null;
                bool? alive = null;
                string? hpFailureStage = null;
                if (region.EntityBaseRegionBytes is not null)
                {
                    hpCurrent = EntityBaseRegion.TryReadHpCurrent(region.EntityBaseRegionBytes);
                    hpMax = EntityBaseRegion.TryReadHpMax(region.EntityBaseRegionBytes);
                    alive = EntityBaseRegion.TryReadAlive(region.EntityBaseRegionBytes);
                    if (hpCurrent is null || hpMax is null)
                    {
                        hpFailureStage = "region-hp-decode";
                    }
                }
                else
                {
                    // The entity-base read was requested (the frame always
                    // does) but failed: surface WHY health is honest-null.
                    hpFailureStage = region.EntityBaseFailureStage;
                }

                tanks.Add(new LiveFrameTankState(
                    region.EntityId,
                    region.Status,
                    x,
                    y,
                    z,
                    yaw,
                    hpCurrent,
                    hpMax,
                    alive,
                    failureStage,
                    region.ModuleRooted,
                    hpFailureStage));
            }

            // 4b. Own damage-dealt consumption (G2, OD-RECOVERY-097 published
            //     chain): when the request names the own entity id, read the
            //     own Avatar's battle-stats dword0 — the gated vftable scan
            //     + [avatar+0x118] quad read, the same seam the L3 sessions
            //     live-proved (OD-RECOVERY-095/096) — and attach it to that
            //     roster row. Honest and fail-closed: a scan/read failure
            //     leaves the row's DamageDealt null (unknown), never guessed;
            //     the frame still succeeds. Only the OWN row can carry it
            //     (the avatar-stats quad is the own player's counter); other
            //     rows stay null.
            if (request.OwnEntityId is { } ownEntityId)
            {
                long? ownDamageDealt = await ReadOwnDamageDealtAsync(
                    observation,
                    baseAddress,
                    reader,
                    authorizationToken,
                    readCancellation.Token).ConfigureAwait(false);
                if (ownDamageDealt is not null)
                {
                    for (int i = 0; i < tanks.Count; i++)
                    {
                        if (tanks[i].EntityId == ownEntityId)
                        {
                            LiveFrameTankState tank = tanks[i];
                            tanks[i] = tank with { DamageDealt = ownDamageDealt };
                            break;
                        }
                    }
                }
            }

            return OperationResult.Success(new LiveFrameReadResult(
                _timeProvider.GetUtcNow(),
                layout.GameVersion,
                Type10EntityPositionStatus.Resolved,
                FailureStage: null,
                batch.ReplayTimeSeconds,
                batch.SameDecodedClockProven,
                camera,
                tanks,
                roster.CandidatesSeen,
                roster.FilteredOut,
                new LiveFrameReadMeasurement(
                    frameStartedAt,
                    frameEndedAt,
                    batch.Measurement?.ClockSnapshotAtUtc)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<LiveFrameReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    /// <summary>
    /// Builds a whole-batch result where every requested entity carries the
    /// same gate-level status (build mismatch, inactive phase) and no bytes.
    /// </summary>
    private EntityRegionsReadResult BuildBatchResult(
        Type10EntityPositionLayout layout,
        IReadOnlyList<EntityRegionReadRequestItem> entities,
        Type10EntityPositionStatus status,
        double? replayTimeSeconds,
        bool sameDecodedClockProven) => new(
        _timeProvider.GetUtcNow(),
        layout.GameVersion,
        status,
        replayTimeSeconds,
        sameDecodedClockProven,
        entities
            .Select(entity => new EntityRegionReadResultItem(
                entity.EntityId,
                status,
                replayTimeSeconds,
                RegionBytes: null,
                FailureStage: status switch
                {
                    Type10EntityPositionStatus.UnsupportedBuild => "build-identity",
                    Type10EntityPositionStatus.ReplaySessionInactive => "pre-battle-inactive",
                    _ => null,
                },
                Attempts: 0,
                NodesVisited: 0,
                ModuleRooted: false,
                EntityIdentityRevalidated: false,
                ConsistentDoubleRead: false))
            .ToList());

    /// <summary>
    /// Reads the tank-record pointer at <c>[entity + 0x3C]</c> through the
    /// same guarded reader/lease and validates it before any region read.
    /// The pointer value is coordinator-owned and never returned; only bytes
    /// read FROM it may leave. Fails closed on a missing entity base, a
    /// short/invalid pointer read, or a non-plausible pointer (null,
    /// misaligned, or outside the x86 process range).
    /// </summary>
    private static async ValueTask<uint?> ResolveTankRecordAddressAsync(
        IAuthorizedMemoryReader reader,
        uint? entityAddress,
        CancellationToken cancellationToken)
    {
        if (entityAddress is not uint entity)
        {
            return null;
        }

        ulong pointerAddress = (ulong)entity + EntityRecordRegionReadRequest.EntityTankRecordOffset;
        if (pointerAddress > uint.MaxValue)
        {
            return null;
        }

        OperationResult<byte[]> pointerRead = await reader.ReadAsync(
            (nint)pointerAddress,
            sizeof(uint),
            cancellationToken).ConfigureAwait(false);
        if (!pointerRead.IsSuccess || pointerRead.Value is null ||
            pointerRead.Value.Length != sizeof(uint))
        {
            return null;
        }

        uint tankRecord = BinaryPrimitives.ReadUInt32LittleEndian(pointerRead.Value);
        if (tankRecord == 0 || (tankRecord & 0x3) != 0)
        {
            return null;
        }

        return tankRecord;
    }

    /// <summary>
    /// Read the own Avatar's battle-stats dword0 (cumulative own
    /// damage-dealt, the G2 published chain: vftableScan
    /// <c>0x032752a4</c> -&gt; recordOffset 280). Honest and fail-closed:
    /// any failure (scan not found, identity mismatch, read error) returns
    /// null — the caller keeps the row's DamageDealt unknown, never
    /// fabricates a value. One guarded scan + one 4-byte read under the
    /// frame's existing lease.
    /// </summary>
    private async ValueTask<long?> ReadOwnDamageDealtAsync(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        IAuthorizedMemoryReader reader,
        CancellationToken authorizationToken,
        CancellationToken cancellationToken)
    {
        AvatarStatsResolution avatar = await ResolveAvatarStatsAddressAsync(
            reader,
            observation,
            baseAddress,
            null,
            cancellationToken).ConfigureAwait(false);
        if (avatar.Address is not uint avatarAddress)
        {
            return null;
        }

        OperationResult<byte[]> quadResult = await reader.ReadAsync(
            (nint)(avatarAddress + EntityRecordRegionReadRequest.AvatarStatsQuadOffset),
            sizeof(uint),
            cancellationToken).ConfigureAwait(false);
        if (!quadResult.IsSuccess || quadResult.Value is null
            || quadResult.Value.Length < sizeof(uint))
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(quadResult.Value);
    }

    private sealed record AvatarStatsResolution(
        uint? Address,
        Type10EntityPositionStatus Status,
        string? FailureStage,
        int Attempts,
        int CandidateCount);

    /// <summary>
    /// Entity-Avatar vftable RVA (L3 static finding, hash-bound
    /// <c>1cda5c31…</c>, 11.19.0.10): the entity-factory Avatar (case 1,
    /// 0x128 bytes, vftable <c>0x36752a4</c>) carries the contiguous uint32
    /// battle-stats block at <c>+0x118/+0x11c/+0x120/+0x124</c>. This is NOT
    /// the camera chain's AvatarControllerReplay anchor (RVA
    /// <c>0x03277e8c</c>) — reusing that anchor would read a different
    /// object (see docs/operations/l3-damage-dealt-avatar-family-plan.md,
    /// reachability section, corrected 2026-08-12).
    /// </summary>
    private const uint EntityAvatarVftableRva = 0x032752a4;

    /// <summary>
    /// The avatar-stats anchor resolution: gated vftable AOB scan for the
    /// entity-Avatar (<c>moduleBase + <see cref="EntityAvatarVftableRva"/></c>,
    /// same guarded scan the camera chain uses — MaxCandidates 4, alignment
    /// 4), identity re-gate on the chosen candidate's vftable dword, and the
    /// battle-stats base (candidate + quad offset, applied by the caller).
    /// Fail-closed: no candidate → <see cref="Type10EntityPositionStatus.AvatarAnchorNotFound"/>;
    /// identity mismatch → <see cref="Type10EntityPositionStatus.AvatarIdentityMismatch"/>.
    /// </summary>
    private async ValueTask<AvatarStatsResolution> ResolveAvatarStatsAddressAsync(
        IAuthorizedMemoryReader reader,
        AuthorizedMemoryObservation observation,
        long baseAddress,
        int? candidateIndex,
        CancellationToken cancellationToken)
    {
        uint expectedVftable = (uint)(baseAddress + EntityAvatarVftableRva);
        byte[] expected = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, expectedVftable);
        MemoryScanRequest request = new(
            FieldName: "avatar-stats-vftable",
            FieldType: "Bytes",
            ExpectedValue: expected,
            ToleranceMask: null,
            MaxCandidates: EntityRecordRegionReadRequest.MaxAvatarCandidates,
            MinRegionSize: 4096,
            Alignment: 4);
        OperationResult<MemoryScanResult> scanResult = await Task.Run(
            () => _scanDiscoverer.Scan(
                observation,
                baseAddress,
                request,
                cancellationToken,
                "aob"),
            cancellationToken).ConfigureAwait(false);
        if (!scanResult.IsSuccess || scanResult.Value is null)
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.ReadFailed,
                "avatar-scan",
                1,
                0);
        }

        IReadOnlyList<MemoryScanCandidate> candidates = scanResult.Value.Candidates;
        if (candidates.Count == 0)
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.AvatarAnchorNotFound,
                "avatar-scan-not-found",
                1,
                0);
        }

        int index = candidateIndex ?? 0;
        if (index < 0 || index >= candidates.Count)
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.AvatarAnchorNotFound,
                "avatar-candidate-out-of-range",
                1,
                candidates.Count);
        }

        long candidateAddress = candidates[index].AbsoluteAddress;
        if (candidateAddress < 0 || candidateAddress > uint.MaxValue)
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.ReadFailed,
                "avatar-address-out-of-range",
                1,
                candidates.Count);
        }

        // Identity re-gate: the AOB scan matched the exact vftable dword,
        // but the discipline re-reads the object's vftable under the same
        // guarded lease before trusting the stats quad (never read the
        // counter off an unauthenticated object).
        OperationResult<byte[]> vftableRead = await reader.ReadAsync(
            (nint)candidateAddress,
            sizeof(uint),
            cancellationToken).ConfigureAwait(false);
        if (!vftableRead.IsSuccess || vftableRead.Value is null ||
            vftableRead.Value.Length != sizeof(uint))
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.ReadFailed,
                "avatar-identity-read",
                1,
                candidates.Count);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(vftableRead.Value) != expectedVftable)
        {
            return new AvatarStatsResolution(
                null,
                Type10EntityPositionStatus.AvatarIdentityMismatch,
                "avatar-identity-mismatch",
                1,
                candidates.Count);
        }

        return new AvatarStatsResolution(
            (uint)candidateAddress,
            Type10EntityPositionStatus.Resolved,
            null,
            1,
            candidates.Count);
    }

    /// <summary>
    /// Reads the live GameCamera pose through the CAM-001 fixed member-path
    /// (avatar vftable anchor → BattleResources → camera controller →
    /// GameCamera) with an identity gate on every hop. The anchor scan is
    /// base-relative (runtime module base + avatar vftable RVA) so it works
    /// regardless of ASLR; the chain is deliberately gate-free with respect
    /// to the session-controller vftable, which flips between launches
    /// (CAM-003). The pose region is read twice byte-identically before any
    /// field is parsed.
    /// </summary>
    public async ValueTask<OperationResult<CameraPoseReadResult>> ReadCameraPoseAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<CameraPoseReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10CameraPoseLayout layout = Type10CameraPoseLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return IsScanAuthorizationCurrent(observation, authorizationToken)
                    ? OperationResult.Failure<CameraPoseReadResult>(
                        new ApplicationError(
                            "discover.camera_pose.unsupported_build",
                            "The running executable does not match the version-pinned camera layout."))
                    : GateCheck<CameraPoseReadResult>(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is no longer satisfied.");
            }

            // 1. Anchor: find the avatar object by its vftable dword = runtime
            //    module base + avatar RVA (replay variant first; the live
            //    variant is the fallback for live-mode sessions). The guarded
            //    reader is created only after the anchor exists, so a missing
            //    anchor never opens a process lease.
            (long avatarAddress, uint matchedAvatarRva) = await FindAvatarAnchorAsync(
                observation,
                baseAddress,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<CameraPoseReadResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (avatarAddress == 0)
            {
                return OperationResult.Success(CameraAnchorNotFound(layout));
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<CameraPoseReadResult>(
                    new ApplicationError(
                        "discover.camera_pose.read_unavailable",
                        "The guarded camera-pose reader is unavailable."));
            }

            IAuthorizedMemoryReader reader = readerResult.Value;

            return await ReadCameraPoseCoreAsync(
                observation,
                baseAddress,
                readerResult.Value,
                avatarAddress,
                matchedAvatarRva,
                layout,
                authorizationToken,
                readCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<CameraPoseReadResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    private async ValueTask<OperationResult<CameraPoseReadResult>> ReadCameraPoseCoreAsync(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        IAuthorizedMemoryReader reader,
        long avatarAddress,
        uint matchedAvatarRva,
        Type10CameraPoseLayout layout,
        CancellationToken authorizationToken,
        CancellationToken cancellationToken)
    {

        // 2. [avatar + AvatarBattleResourcesOffset] → battle resources.
        OperationResult<uint> battleResources = await ReadUInt32PointerAsync(
            reader,
            avatarAddress + layout.AvatarBattleResourcesOffset,
            cancellationToken).ConfigureAwait(false);
        if (!battleResources.IsSuccess)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "avatar-battle-resources",
                matchedAvatarRva, false, false));
        }

        // 3. [br + CameraControllerOffset] → camera controller; gate its
        //    vftable against the replay (or live) variant.
        OperationResult<uint> cameraAddress = await ReadUInt32PointerAsync(
            reader,
            battleResources.Value + layout.CameraControllerOffset,
            cancellationToken).ConfigureAwait(false);
        if (!cameraAddress.IsSuccess || cameraAddress.Value == 0)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "camera-controller",
                matchedAvatarRva, false, false));
        }

        OperationResult<uint> cameraVftable = await ReadUInt32PointerAsync(
            reader,
            cameraAddress.Value,
            cancellationToken).ConfigureAwait(false);
        uint cameraVftableRva = (uint)(cameraVftable.Value - (uint)baseAddress);
        bool cameraIdentity = cameraVftable.IsSuccess
            && (cameraVftableRva == layout.CameraReplayVftableRva
                || cameraVftableRva == layout.CameraLiveVftableRva);
        if (!cameraIdentity)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "camera-vftable",
                matchedAvatarRva, false, false));
        }

        // 4. [camera + CameraStateOffset] → GameCamera; gate its vftable.
        OperationResult<uint> cameraStateAddress = await ReadUInt32PointerAsync(
            reader,
            cameraAddress.Value + layout.CameraStateOffset,
            cancellationToken).ConfigureAwait(false);
        if (!cameraStateAddress.IsSuccess || cameraStateAddress.Value == 0)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "camera-state",
                matchedAvatarRva, cameraIdentity, false));
        }

        OperationResult<uint> cameraStateVftable = await ReadUInt32PointerAsync(
            reader,
            cameraStateAddress.Value,
            cancellationToken).ConfigureAwait(false);
        bool cameraStateIdentity = cameraStateVftable.IsSuccess
            && cameraStateVftable.Value - (uint)baseAddress == layout.CameraStateVftableRva;
        if (!cameraStateIdentity)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "camera-state-vftable",
                matchedAvatarRva, cameraIdentity, false));
        }

        // 5. Pose region: [GameCamera + PositionOffset, PoseRegionLength)
        //    read twice, byte-identical, before parsing any field.
        nint poseAddress = (nint)(cameraStateAddress.Value + layout.PositionOffset);
        OperationResult<byte[]> firstRead = await reader.ReadAsync(
            poseAddress,
            layout.PoseRegionLength,
            cancellationToken).ConfigureAwait(false);
        if (!firstRead.IsSuccess || firstRead.Value is null ||
            firstRead.Value.Length != layout.PoseRegionLength)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "pose-region",
                matchedAvatarRva, cameraIdentity, cameraStateIdentity));
        }

        OperationResult<byte[]> secondRead = await reader.ReadAsync(
            poseAddress,
            layout.PoseRegionLength,
            cancellationToken).ConfigureAwait(false);
        bool consistentDoubleRead = secondRead.IsSuccess
            && secondRead.Value is not null
            && secondRead.Value.AsSpan().SequenceEqual(firstRead.Value);
        if (!consistentDoubleRead)
        {
            return OperationResult.Success(CameraChainBroken(layout, avatarAddress, "pose-double-read",
                matchedAvatarRva, cameraIdentity, cameraStateIdentity));
        }

        byte[] pose = firstRead.Value;
        int posOffset = 0;
        int yawCosOffset = (int)(layout.YawCosOffset - layout.PositionOffset);
        int yawSinOffset = (int)(layout.YawSinOffset - layout.PositionOffset);
        int pitchOffset = (int)(layout.PitchOffset - layout.PositionOffset);
        int basisOffset = (int)(layout.BasisOffset - layout.PositionOffset);

        float x = BitConverter.ToSingle(pose, posOffset);
        float y = BitConverter.ToSingle(pose, posOffset + sizeof(float));
        float z = BitConverter.ToSingle(pose, posOffset + 2 * sizeof(float));
        float yawCos = BitConverter.ToSingle(pose, yawCosOffset);
        float yawSin = BitConverter.ToSingle(pose, yawSinOffset);
        float pitch = BitConverter.ToSingle(pose, pitchOffset);
        float yaw = (float)Math.Atan2(yawSin, yawCos);

        // The view-basis region (+0x80..0xB0) is a row-major stride-4 3x4
        // view matrix (CAM-001 v7b, verified 2026-08-11 on real dumps):
        // row0 at +0x80 (indices 0..2), pad at index 3 (+0x8C), row1 at
        // +0x90 (indices 4..6), pad at index 7 (+0x9C), row2 at +0xA0
        // (indices 8..10), pad at index 11 (+0xAC). Read all 12 floats so
        // row2.z (+0xA8) is covered, then expose the three rows contiguously
        // for the W2S consumption seam (forward = -row1, up = row2).
        float[] basisRaw = new float[12];
        for (int i = 0; i < 12; i++)
        {
            basisRaw[i] = BitConverter.ToSingle(pose, basisOffset + i * sizeof(float));
        }

        float[] basis = new float[9];
        for (int i = 0; i < 3; i++)
        {
            basis[i] = basisRaw[i];
            basis[3 + i] = basisRaw[4 + i];
            basis[6 + i] = basisRaw[8 + i];
        }

        return OperationResult.Success(new CameraPoseReadResult(
            _timeProvider.GetUtcNow(),
            layout.GameVersion,
            CameraPoseStatus.Resolved,
            null,
            avatarAddress,
            cameraAddress.Value,
            cameraStateAddress.Value,
            x, y, z, yaw, pitch, basis,
            AvatarIdentityVerified: true,
            CameraIdentityVerified: true,
            CameraStateIdentityVerified: true,
            ConsistentDoubleRead: true,
            ModuleRooted: true));

    }

    private CameraPoseReadResult CameraChainBroken(
        Type10CameraPoseLayout layout,
        long avatarAddress,
        string failureStage,
        uint matchedAvatarRva,
        bool cameraIdentity,
        bool cameraStateIdentity) => new(
        _timeProvider.GetUtcNow(),
        layout.GameVersion,
        CameraPoseStatus.ChainBroken,
        failureStage,
        avatarAddress,
        0, 0,
        0f, 0f, 0f, 0f, 0f, [],
        AvatarIdentityVerified: matchedAvatarRva != 0,
        CameraIdentityVerified: cameraIdentity,
        CameraStateIdentityVerified: cameraStateIdentity,
        ConsistentDoubleRead: false,
        ModuleRooted: true);

    private async ValueTask<(long AvatarAddress, uint MatchedAvatarRva)> FindAvatarAnchorAsync(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        Type10CameraPoseLayout layout,
        CancellationToken authorizationToken,
        CancellationToken cancellationToken)
    {
        long avatarAddress = 0;
        uint matchedAvatarRva = 0;
        foreach (uint avatarRva in new[]
        {
            layout.AvatarVftableReplayRva,
            layout.AvatarVftableLiveRva,
        })
        {
            OperationResult<MemoryScanResult> scanResult = await ScanForCameraAnchorAsync(
                observation,
                baseAddress,
                (uint)(baseAddress + avatarRva),
                layout,
                cancellationToken).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return (0, 0);
            }

            if (scanResult.IsSuccess && scanResult.Value is { Candidates.Count: > 0 })
            {
                avatarAddress = scanResult.Value.Candidates[0].AbsoluteAddress;
                matchedAvatarRva = avatarRva;
                break;
            }
        }

        return (avatarAddress, matchedAvatarRva);
    }

    private CameraPoseReadResult CameraAnchorNotFound(Type10CameraPoseLayout layout) => new(
        _timeProvider.GetUtcNow(),
        layout.GameVersion,
        CameraPoseStatus.AnchorNotFound,
        "avatar-vftable-anchor",
        0, 0, 0,
        0f, 0f, 0f, 0f, 0f, [],
        AvatarIdentityVerified: false,
        CameraIdentityVerified: false,
        CameraStateIdentityVerified: false,
        ConsistentDoubleRead: false,
        ModuleRooted: true);

    private async ValueTask<OperationResult<MemoryScanResult>> ScanForCameraAnchorAsync(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        uint expectedDword,
        Type10CameraPoseLayout layout,
        CancellationToken cancellationToken)
    {
        byte[] expected = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, expectedDword);
        MemoryScanRequest request = new(
            FieldName: "avatar-vftable",
            FieldType: "Bytes",
            ExpectedValue: expected,
            ToleranceMask: null,
            MaxCandidates: layout.MaxCandidates,
            MinRegionSize: layout.MinRegionSize,
            Alignment: 4);
        return await Task.Run(
            () => _scanDiscoverer.Scan(observation, baseAddress, request, cancellationToken, "aob"),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<OperationResult<uint>> ReadUInt32PointerAsync(
        IAuthorizedMemoryReader reader,
        long address,
        CancellationToken cancellationToken)
    {
        if (address < 0 || address > uint.MaxValue)
        {
            return OperationResult.Failure<uint>(
                new ApplicationError("discover.camera_pose.invalid_address", "The camera chain pointer is out of range."));
        }

        OperationResult<byte[]> read = await reader.ReadAsync(
            (nint)address,
            sizeof(uint),
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is null || read.Value.Length != sizeof(uint))
        {
            return OperationResult.Failure<uint>(read.Error ?? new ApplicationError(
                "discover.camera_pose.read_failed", "The camera chain read failed."));
        }

        return OperationResult.Success(BinaryPrimitives.ReadUInt32LittleEndian(read.Value));
    }

    public async ValueTask<OperationResult<EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
        EntityPositionAddressRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<EntityPositionAddressResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        using CancellationTokenSource readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            if (!string.Equals(
                observation!.ProductVersion,
                layout.GameVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.ExecutableSha256.Value,
                layout.ExecutableSha256,
                StringComparison.Ordinal))
            {
                return OperationResult.Failure<EntityPositionAddressResult>(
                    new ApplicationError(
                        "discover.entity_position.address_unsupported_build",
                        "The running build does not match the exact-build layout; " +
                        "the position page cannot be resolved for interceptor arming."));
            }

            OperationResult<IAuthorizedMemoryReader> readerResult = await _memoryReaderFactory
                .CreateAsync(observation, readCancellation.Token)
                .ConfigureAwait(false);
            if (!readerResult.IsSuccess || readerResult.Value is null)
            {
                return OperationResult.Failure<EntityPositionAddressResult>(
                    new ApplicationError(
                        "discover.entity_position.read_unavailable",
                        "The guarded entity-position reader is unavailable."));
            }

            OperationResult<Type10EntityPositionAddressResult> resolveResult =
                await readerResult.Value.ResolveEntityPositionAddressAsync(
                    (nint)baseAddress,
                    request.EntityId,
                    layout,
                    readCancellation.Token).ConfigureAwait(false);
            if (!IsScanAuthorizationCurrent(observation, authorizationToken))
            {
                return GateCheck<EntityPositionAddressResult>(
                    "discover.gate_not_satisfied",
                    "The offline-session gate is no longer satisfied.");
            }

            if (!resolveResult.IsSuccess || resolveResult.Value is null)
            {
                return OperationResult.Failure<EntityPositionAddressResult>(
                    resolveResult.Error ?? new ApplicationError(
                        "discover.entity_position.address_read_failed",
                        "The entity-position address read failed."));
            }

            Type10EntityPositionAddressResult resolved = resolveResult.Value;
            return OperationResult.Success(new EntityPositionAddressResult(
                resolved.Status,
                resolved.RecordAddress,
                resolved.PageAddress,
                resolved.FailureStage,
                resolved.Attempts,
                resolved.NodesVisited,
                resolved.ModuleRooted));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<EntityPositionAddressResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<InstructionSnapshotResult>> CaptureInstructionSnapshotAsync(
        InstructionSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DurationMilliseconds is < 1_000 or > 5_000
            || request.MaxHits is < 1 or > 64)
        {
            return OperationResult.Failure<InstructionSnapshotResult>(
                new ApplicationError(
                    "discover.instruction_snapshot.invalid_options",
                    "Duration must be at most five seconds and max hits at most 64."));
        }

        (AuthorizedMemoryObservation? observation, long baseAddress, CancellationToken authorizationToken, bool ok) =
            GetScanAuthorization(cancellationToken);
        if (!ok)
        {
            return GateCheck<InstructionSnapshotResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        }

        _ = baseAddress;
        using CancellationTokenSource captureCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        InstructionSnapshotRunnerOutcome outcome;
        try
        {
            outcome = await _instructionSnapshotRunner.RunAsync(
                new InstructionSnapshotExecutionRequest(
                    observation!.ProcessId,
                    observation.ProcessStartIdentity,
                    observation.CanonicalExecutablePath,
                    observation.ProductVersion,
                    observation.ExecutableSha256,
                    request.DurationMilliseconds,
                    request.MaxHits),
                captureCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<InstructionSnapshotResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }

        if (!outcome.CleanupProven)
        {
            lock (_gate)
            {
                if (_managedLaunch is not null
                    && _managedLaunch.ProcessId == observation!.ProcessId
                    && _managedLaunch.ProcessStartIdentity == observation.ProcessStartIdentity
                    && string.Equals(
                        _managedLaunch.TrustedGameIdentity.ExecutablePath,
                        observation.CanonicalExecutablePath,
                        StringComparison.OrdinalIgnoreCase)
                    && _managedLaunch.TrustedGameIdentity.ExecutableSha256
                        == observation.ExecutableSha256)
                {
                    Deny("discover.instruction_snapshot.cleanup_unproven");
                }
            }

            return OperationResult.Failure<InstructionSnapshotResult>(
                outcome.Error ?? new ApplicationError(
                    "discover.instruction_snapshot.cleanup_unproven",
                    "The helper could not prove exact debug-register cleanup."));
        }

        bool authorizationCurrent;
        try
        {
            authorizationCurrent = IsScanAuthorizationCurrent(observation!, authorizationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<InstructionSnapshotResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }

        if (!authorizationCurrent)
        {
            return GateCheck<InstructionSnapshotResult>(
                "discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }

        return outcome.IsSuccess && outcome.Result is not null
            ? OperationResult.Success(outcome.Result)
            : OperationResult.Failure<InstructionSnapshotResult>(
                outcome.Error ?? new ApplicationError(
                    "discover.instruction_snapshot.failed",
                    "The instruction snapshot capture failed."));
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

    private static bool ValueSizeMatchesKind(MemoryValueKind kind, int valueSize) =>
        kind switch
        {
            MemoryValueKind.FloatValue or MemoryValueKind.Int32Value or MemoryValueKind.UInt32Value
                => valueSize == 4,
            MemoryValueKind.DoubleValue or MemoryValueKind.Int64Value or MemoryValueKind.UInt64Value
                => valueSize == 8,
            _ => true, // Bytes: any width is a valid raw read.
        };

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
                            // period after playback has ended. The reason is
                            // distinct so callers observe completion rather
                            // than a monitor fault.
                            if (!token.IsCancellationRequested)
                            {
                                ReportReplayCompleted(launch, token);
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
