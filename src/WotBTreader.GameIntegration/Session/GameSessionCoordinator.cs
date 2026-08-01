using System.Diagnostics;
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
    ReplayEvidenceSource Source,
    string SourceIdentity,
    long SourceGeneration,
    long SourceSequence,
    int ProcessId,
    long ProcessStartIdentity,
    string LaunchCorrelation);

internal sealed record GameSessionEvidence(
    bool GamePresent,
    bool MonitorHealthy,
    bool ReplayUiConfirmed,
    GameProcessEvidence? Process,
    ReplayLifecycleEvidence? Lifecycle);

internal sealed record ManagedGameLaunchContext(
    string LaunchCorrelation,
    InstalledGameIdentity TrustedGameIdentity,
    string LifecycleSourceIdentity,
    long SourceGeneration,
    long SourceSequenceBaseline);

/// <summary>
/// Owns the evidence-backed offline state and orchestrates managed replay
/// launches through the M2 suspended-process pipeline. Public consumers
/// receive only safe snapshots and observations; authorization and process
/// identity never leave this adapter.
/// </summary>
internal sealed class GameSessionCoordinator : IGameSessionState,
    IGameReplayLauncher, IGameMemoryObserver, IGameMemoryScanner, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IManagedLaunchPreparer _preparer;
    private readonly IManagedReplayArtifactStager _artifactStager;
    private readonly ISuspendedProcessPlatform _suspendedPlatform;
    private readonly IManagedLaunchCorrelationRegistrar _correlationRegistrar;
    private readonly IThreadResumePlatform _threadResumePlatform;
    private readonly IGuardedMemoryReaderFactory _memoryReaderFactory;
    private readonly IOffsetTableReader _offsetTableReader;
    private readonly MemoryScanDiscoverer _scanDiscoverer;
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

    // Active launch leases owned by the coordinator until the session is revoked.
    private WindowsTrustedExecutableLaunchLease? _activeExecutableLease;
    private ManagedReplayArtifactLease? _activeArtifactLease;
    private SuspendedGameProcessLease? _activeSuspendedLease;
    private CancellationTokenSource? _activeMonitoringCts;
    private CancellationTokenSource? _authorizationCts;
    private bool _disposed;

    public GameSessionCoordinator(
        TimeProvider timeProvider,
        IManagedLaunchPreparer preparer,
        IManagedReplayArtifactStager artifactStager,
        ISuspendedProcessPlatform suspendedPlatform,
        IManagedLaunchCorrelationRegistrar correlationRegistrar,
        IThreadResumePlatform threadResumePlatform,
        IGuardedMemoryReaderFactory memoryReaderFactory,
        IOffsetTableReader offsetTableReader,
        MemoryScanDiscoverer scanDiscoverer,
        MemoryScanEngine scanEngine,
        IBlitzReplayLifecycleFeed lifecycleFeed)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
        _artifactStager = artifactStager ?? throw new ArgumentNullException(nameof(artifactStager));
        _suspendedPlatform = suspendedPlatform ?? throw new ArgumentNullException(nameof(suspendedPlatform));
        _correlationRegistrar = correlationRegistrar ?? throw new ArgumentNullException(nameof(correlationRegistrar));
        _threadResumePlatform = threadResumePlatform ?? throw new ArgumentNullException(nameof(threadResumePlatform));
        _memoryReaderFactory = memoryReaderFactory ?? throw new ArgumentNullException(nameof(memoryReaderFactory));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.LifecycleSourceIdentity);
        if (launch.SourceGeneration <= 0 || launch.SourceSequenceBaseline < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(launch),
                "A managed launch requires a positive source generation and non-negative cursor baseline.");
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

        // ── 1. Prepare: identity, correlation, lifecycle baseline ──
        OperationResult<ManagedLaunchPreparation> prepResult =
            await _preparer.PrepareAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepResult.IsSuccess)
        {
            return LaunchFailure(prepResult.Error!);
        }

        ManagedLaunchPreparation preparation = prepResult.Value!;
        cancellationToken.ThrowIfCancellationRequested();

        WindowsTrustedExecutableLaunchLease? executableLease = null;
        ManagedReplayArtifactLease? artifactLease = null;
        SuspendedGameProcessLease? suspendedLease = null;
        WindowsTrustedExecutableLaunchLease? handedOffExe = null;
        ManagedReplayArtifactLease? handedOffArtifact = null;
        DetachedLaunchLeases replacedLeases = default;
        bool replacedLeasesDetached = false;

        try
        {
            // ── 2. Acquire executable lease ──
            OperationResult<WindowsTrustedExecutableLaunchLease> exeLeaseResult =
                await WindowsTrustedExecutableLaunchLease.AcquireAsync(
                    preparation.TrustedIdentity,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!exeLeaseResult.IsSuccess)
            {
                return LaunchFailure(exeLeaseResult.Error!);
            }

            executableLease = exeLeaseResult.Value!;
            cancellationToken.ThrowIfCancellationRequested();

            // ── 3. Stage artifact ──
            OperationResult<ManagedReplayArtifactLease> artifactResult =
                await _artifactStager.StageAsync(
                    request.SourceArtifactId,
                    cancellationToken).ConfigureAwait(false);
            if (!artifactResult.IsSuccess)
            {
                await executableLease.DisposeAsync().ConfigureAwait(false);
                return LaunchFailure(artifactResult.Error!);
            }

            artifactLease = artifactResult.Value!;
            cancellationToken.ThrowIfCancellationRequested();

            // ── 4. Create suspended process ──
            OperationResult<SuspendedGameProcessLease> suspendedResult =
                await _suspendedPlatform.CreateAsync(
                    executableLease,
                    artifactLease,
                    cancellationToken).ConfigureAwait(false);
            if (!suspendedResult.IsSuccess)
            {
                await artifactLease.DisposeAsync().ConfigureAwait(false);
                await executableLease.DisposeAsync().ConfigureAwait(false);
                return LaunchFailure(suspendedResult.Error!);
            }

            suspendedLease = suspendedResult.Value!;
            cancellationToken.ThrowIfCancellationRequested();

            // ── 5. Register correlation ──
            OperationResult<ManagedGameLaunchContext> correlationResult =
                _correlationRegistrar.Register(preparation, suspendedLease);
            if (!correlationResult.IsSuccess)
            {
                await suspendedLease.DisposeAsync().ConfigureAwait(false);
                await artifactLease.DisposeAsync().ConfigureAwait(false);
                await executableLease.DisposeAsync().ConfigureAwait(false);
                return LaunchFailure(correlationResult.Error!);
            }

            ManagedGameLaunchContext launchContext = correlationResult.Value!;
            cancellationToken.ThrowIfCancellationRequested();

            // ── 6. Resume the child thread (must happen BEFORE HandOffLeases
            //     so that resume failures terminate the suspended child) ──
            OperationResult<ThreadResumeOutcome> resumeResult =
                _threadResumePlatform.Resume(suspendedLease.ThreadHandle!);
            if (!resumeResult.IsSuccess)
            {
                // Resume failed; child is still suspended. Disposal terminates it
                // because HandOffLeases has not been called.
                await suspendedLease.DisposeAsync().ConfigureAwait(false);
                await artifactLease.DisposeAsync().ConfigureAwait(false);
                await executableLease.DisposeAsync().ConfigureAwait(false);
                return LaunchFailure(resumeResult.Error!);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ── 7–8. Atomically commit the handoff under the coordinator lock.
            // If disposal/cancellation wins this linearization point, the
            // suspended lease has not been handed off and finally will terminate
            // the child. Once committed, disposal may release handles but the
            // child remains a valid launched process.
            int childPid = suspendedLease.ProcessId;
            CancellationToken monitoringToken;
            bool disposedDuringLaunch;
            lock (_gate)
            {
                disposedDuringLaunch = _disposed || cancellationToken.IsCancellationRequested;
                if (!disposedDuringLaunch)
                {
                    // Validate and record the new generation before handing off
                    // ownership. If this throws, the child is still owned by the
                    // local suspended lease and finally terminates it.
                    RecordManagedLaunch(launchContext);
                    (handedOffExe, handedOffArtifact) = suspendedLease.HandOffLeases();
                    replacedLeases = DetachLaunchLeasesLocked();
                    replacedLeasesDetached = true;

                    _activeSuspendedLease = suspendedLease;
                    _activeExecutableLease = handedOffExe;
                    _activeArtifactLease = handedOffArtifact;

                    _activeMonitoringCts?.Cancel();
                    _activeMonitoringCts?.Dispose();
                    _activeMonitoringCts = new CancellationTokenSource();
                    monitoringToken = _activeMonitoringCts.Token;
                }
                else
                {
                    monitoringToken = default;
                }
            }

            if (disposedDuringLaunch)
            {
                return OperationResult.Failure<GameReplayLaunchOutcome>(
                    new ApplicationError("game.session_disposed", "The game session coordinator was disposed during launch.", Retryable: false));
            }

            suspendedLease = null;
            executableLease = null;
            artifactLease = null;
            handedOffExe = null;
            handedOffArtifact = null;

            StartMonitoringLifecycle(launchContext, childPid, monitoringToken);

            return OperationResult.Success(
                new GameReplayLaunchOutcome(_timeProvider.GetUtcNow()));
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
        // then release it before performing slow memory reads.
        AuthorizedObservation? auth;
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified
                || _authorization is null)
            {
                return UnknownObservation();
            }

            auth = _authorization;
        }

        return await ReadMemoryAsync(auth, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameMemoryObservation> ReadMemoryAsync(
        AuthorizedObservation auth,
        CancellationToken cancellationToken)
    {
        // No offset table loaded — authorized but no offsets configured.
        OffsetTable? table = auth.OffsetTable;
        if (table is null)
        {
            return AvailableObservation();
        }

        // Collect known fields (non-zero offsets).
        List<OffsetField> knownFields = [];
        foreach (OffsetField field in table.Fields)
        {
            if (field.Offset != 0)
            {
                knownFields.Add(field);
            }
        }

        if (knownFields.Count == 0)
        {
            return AvailableObservation();
        }

        // Base address is required to compute absolute addresses.
        if (auth.BaseAddress == nint.Zero)
        {
            return AvailableObservation();
        }

        // Create the guarded memory reader.
        AuthorizedMemoryObservation obs = new(
            auth.ProcessId,
            auth.ProcessStartIdentity,
            auth.CanonicalExecutablePath,
            auth.ProductVersion,
            auth.ExecutableSha256,
            auth.ExpiresAtUtc);

        OperationResult<IAuthorizedMemoryReader> readerResult =
            await _memoryReaderFactory.CreateAsync(obs, cancellationToken)
                .ConfigureAwait(false);
        if (!readerResult.IsSuccess)
        {
            return AvailableObservation();
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

            nint absoluteAddress = auth.BaseAddress + (nint)field.Offset;
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
            RevokeSession();
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
            || now - lifecycle.ObservedAtUtc > EvidenceLifetime)
        {
            Revoke();
            _snapshot = CreateSnapshot(
                GameSessionVerificationState.EvidenceStale,
                gamePresent: true,
                expiresAtUtc: lifecycle.ObservedAtUtc + EvidenceLifetime,
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

        DateTimeOffset expiresAtUtc = lifecycle.ObservedAtUtc + EvidenceLifetime;
        _lastCursor = new EvidenceCursor(
            lifecycle.SourceIdentity,
            lifecycle.SourceGeneration,
            lifecycle.SourceSequence);

        OffsetTable? offsetTable = LoadOffsetTable(process);
        nint baseAddress = offsetTable is not null && HasKnownOffsets(offsetTable)
            ? ResolveBaseAddress(process.ProcessId)
            : nint.Zero;

        if (_authorization is null)
        {
            _authorizationCts = new CancellationTokenSource();
        }

        _authorization = new AuthorizedObservation(
            ++_authorizationGeneration,
            process.ProcessId,
            process.ProcessStartIdentity,
            process.ObservedCanonicalExecutablePath,
            process.ObservedProductVersion,
            process.ObservedExecutableSha256,
            expiresAtUtc,
            baseAddress,
            offsetTable);
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
            || !string.Equals(
                lifecycle.SourceIdentity,
                _managedLaunch.LifecycleSourceIdentity,
                StringComparison.Ordinal)
            || lifecycle.SourceGeneration != _managedLaunch.SourceGeneration
            || lifecycle.SourceSequence <= _managedLaunch.SourceSequenceBaseline)
        {
            return false;
        }

        if (_lastCursor is null)
        {
            return true;
        }

        return string.Equals(
                   lifecycle.SourceIdentity,
                   _lastCursor.SourceIdentity,
                   StringComparison.Ordinal)
               && lifecycle.SourceGeneration == _lastCursor.SourceGeneration
               && lifecycle.SourceSequence > _lastCursor.SourceSequence;
    }

    private bool IsProcessIdentityValid(GameProcessEvidence process) =>
        _managedLaunch is not null
        && process.ProcessId > 0
        && process.ProcessStartIdentity > 0
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

    private void ExpireAuthorizationIfNeeded()
    {
        if (_authorization is null
            || _authorization.ExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            return;
        }

        Revoke();
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.EvidenceStale,
            gamePresent: true,
            expiresAtUtc: _snapshot.EvidenceExpiresAtUtc,
            "evidence.expired");
    }

    private void SetUnverified(string reasonCode)
    {
        Revoke();
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.GamePresentUnverified,
            gamePresent: true,
            expiresAtUtc: null,
            reasonCode);
    }

    private void Deny(string reasonCode)
    {
        RevokeSession();
        _snapshot = CreateSnapshot(
            GameSessionVerificationState.Denied,
            gamePresent: true,
            expiresAtUtc: null,
            reasonCode);
    }

    private void Revoke()
    {
        _authorization = null;
        _authorizationCts?.Cancel();
        // Do not dispose this CTS here. Scan setup creates its linked source
        // under _gate; retaining the canceled source avoids a dispose/register
        // race for a scan that has just crossed the authorization gate.
        _authorizationCts = null;
        _scanEngine.DiscardAllSessions();
    }

    private void RevokeSession()
    {
        Revoke();
        _activeMonitoringCts?.Cancel();
        _activeMonitoringCts?.Dispose();
        _activeMonitoringCts = null;
        _managedLaunch = null;
        _lastCursor = null;

        DetachedLaunchLeases leases = DetachLaunchLeasesLocked();
        QueueDetachedLaunchLeaseDisposal(leases);
    }

    private DetachedLaunchLeases DetachLaunchLeasesLocked() =>
        new(
            Interlocked.Exchange(ref _activeSuspendedLease, null),
            Interlocked.Exchange(ref _activeArtifactLease, null),
            Interlocked.Exchange(ref _activeExecutableLease, null));

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
        // Disposal order: suspended first (closes handles; HandOffLeases
        // prevents child termination), then artifact (deletes staging file),
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

    private static bool HasKnownOffsets(OffsetTable table)
    {
        // Candidate offsets are discovery hypotheses only. Runtime reads require
        // an explicitly verified field; a valid table hash alone is insufficient.
        foreach (OffsetField field in table.Fields)
        {
            if (field.Offset != 0 && field.Status == OffsetFieldStatus.Verified)
            {
                return true;
            }
        }

        return false;
    }

    private static nint ResolveBaseAddress(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.MainModule?.BaseAddress ?? nint.Zero;
        }
        catch
        {
            return nint.Zero;
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
        nint BaseAddress,
        OffsetTable? OffsetTable);

    private sealed record EvidenceCursor(
        string SourceIdentity,
        long SourceGeneration,
        long SourceSequence);

    private readonly record struct DetachedLaunchLeases(
        SuspendedGameProcessLease? Suspended,
        ManagedReplayArtifactLease? Artifact,
        WindowsTrustedExecutableLaunchLease? Executable)
    {
        internal bool IsEmpty => Suspended is null && Artifact is null && Executable is null;
    }

    /// <summary>
    /// Disposes all active launch leases. After HandOffLeases, the child
    /// process is not terminated; only handles and staging files are released.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
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
            _lifetimeCts.Cancel();
            Revoke();
            _activeMonitoringCts?.Cancel();
            _activeMonitoringCts?.Dispose();
            _activeMonitoringCts = null;
        }

        SuspendedGameProcessLease? suspended = Interlocked.Exchange(ref _activeSuspendedLease, null);
        if (suspended is not null)
        {
            await suspended.DisposeAsync().ConfigureAwait(false);
        }

        ManagedReplayArtifactLease? artifact = Interlocked.Exchange(ref _activeArtifactLease, null);
        if (artifact is not null)
        {
            await artifact.DisposeAsync().ConfigureAwait(false);
        }

        WindowsTrustedExecutableLaunchLease? executable = Interlocked.Exchange(ref _activeExecutableLease, null);
        if (executable is not null)
        {
            await executable.DisposeAsync().ConfigureAwait(false);
        }

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
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
        if (!ok)
            return OperationResult.Failure<string>(
                new ApplicationError("discover.gate_not_satisfied", "Gate not satisfied."));

        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            return await Task.Run(
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
                        request.RegionSelection),
                    scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
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
        bool advanceBaseline = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
        if (!ok)
            return OperationResult.Failure<MemoryCompareResult>(
                new ApplicationError("discover.gate_not_satisfied", "Gate not satisfied."));

        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            return await Task.Run(() =>
            {
                OperationResult<MemoryScanEngine.CompareResult> result = _scanEngine.Compare(
                    observation!,
                    baseAddr,
                    sessionId,
                    compareMode ?? "changed",
                    maxCandidates,
                    advanceBaseline,
                    scanCancellation.Token);
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
            }, scanCancellation.Token).ConfigureAwait(false);
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
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
        if (!ok)
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            return await Task.Run(
                () => _scanDiscoverer.ScanNeighborhood(observation!, baseAddr, request, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
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
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
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
            return await Task.Run(
                () => _scanDiscoverer.Scan(observation!, baseAddr, typedRequest, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
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
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
        if (!ok)
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            return await Task.Run(
                () => _scanDiscoverer.Scan(observation!, baseAddr, request, scanCancellation.Token, "aob"),
                scanCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryScanResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    public async ValueTask<OperationResult<MemoryPointerChainResult>> ResolvePointerChainAsync(
        MemoryPointerChainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        (AuthorizedMemoryObservation? observation, long baseAddr, CancellationToken authorizationToken, bool ok) = GetScanAuthorization();
        if (!ok)
            return GateCheck<MemoryPointerChainResult>("discover.gate_not_satisfied",
                "The offline-session gate is not satisfied.");
        using CancellationTokenSource scanCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorizationToken);
        try
        {
            return await Task.Run(
                () => _scanDiscoverer.ResolvePointerChain(observation!, baseAddr, request, scanCancellation.Token),
                scanCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GateCheck<MemoryPointerChainResult>("discover.gate_not_satisfied",
                "The offline-session gate is no longer satisfied.");
        }
    }

    private (AuthorizedMemoryObservation? Observation, long BaseAddress, CancellationToken AuthorizationToken, bool Ok)
        GetScanAuthorization()
    {
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified
                || _authorization is null
                || _authorization.BaseAddress == nint.Zero
                || _authorizationCts is null)
            {
                return (null, 0, default, false);
            }

            AuthorizedObservation auth = _authorization;
            return (
                new AuthorizedMemoryObservation(
                    auth.ProcessId,
                    auth.ProcessStartIdentity,
                    auth.CanonicalExecutablePath,
                    auth.ProductVersion,
                    auth.ExecutableSha256,
                    auth.ExpiresAtUtc),
                auth.BaseAddress.ToInt64(),
                _authorizationCts.Token,
                true);
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

    private void StartMonitoringLifecycle(
        ManagedGameLaunchContext launch,
        int processId,
        CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                long currentSequence = launch.SourceSequenceBaseline;
                while (!token.IsCancellationRequested)
                {
                    LifecycleFeedReadResult result = await _lifecycleFeed
                        .ReadAfterAsync(currentSequence, token)
                        .ConfigureAwait(false);
                    currentSequence = result.LatestSequence;

                    foreach (LifecycleFeedEvent ev in result.Events)
                    {
                        if (ev.MarkerKind == ReplayLogMarkerKind.OfflineReplayStarted
                            && ev.Cursor is not null)
                        {
                            long startIdentity;
                            try
                            {
                                using Process p = Process.GetProcessById(processId);
                                startIdentity = p.StartTime.ToFileTimeUtc();
                            }
                            catch
                            {
                                // Process exited — stop monitoring.
                                return;
                            }

                            var processEvidence = new GameProcessEvidence(
                                processId,
                                startIdentity,
                                IsAlive: true,
                                launch.TrustedGameIdentity.ExecutablePath,
                                launch.TrustedGameIdentity.ProductVersion,
                                launch.TrustedGameIdentity.ExecutableSha256,
                                WindowHandle: 1,
                                WindowOwnerProcessId: processId);

                            var lifecycleEvidence = new ReplayLifecycleEvidence(
                                ReplayLifecycleState.OfflineReplayStarted,
                                ev.ObservedAtUtc,
                                ReplayEvidenceSource.BlitzNativeLog,
                                ev.Cursor.SourceId.Value,
                                ev.Cursor.Generation,
                                ev.Sequence,
                                processId,
                                startIdentity,
                                launch.LaunchCorrelation);

                            ApplyEvidence(new GameSessionEvidence(
                                GamePresent: true,
                                MonitorHealthy: true,
                                ReplayUiConfirmed: true,
                                processEvidence,
                                lifecycleEvidence));
                        }
                        else if (ev.MarkerKind == ReplayLogMarkerKind.OfflineReplayStopped)
                        {
                            return;
                        }
                    }

                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on revocation or disposal.
            }
            catch
            {
                // Process exit or feed failure — stop silently.
            }
        }, CancellationToken.None);
    }

    private static OperationResult<GameReplayLaunchOutcome> LaunchFailure(
        ApplicationError error) =>
        OperationResult.Failure<GameReplayLaunchOutcome>(error);
}
