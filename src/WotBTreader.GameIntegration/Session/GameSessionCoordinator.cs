using System.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;

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
        MemoryScanDiscoverer scanDiscoverer)
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
    }

    /// <summary>
    /// Records an adapter-generated launch correlation. Callers outside
    /// GameIntegration cannot provide or inspect this value.
    /// </summary>
    internal void RecordManagedLaunch(ManagedGameLaunchContext launch)
    {
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

        try
        {
            return await LaunchCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Failure<GameReplayLaunchOutcome>(
                new ApplicationError(
                    "game.launch.unexpected_failure",
                    $"An unexpected error occurred during launch orchestration: {exception.GetType().Name}",
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

        WindowsTrustedExecutableLaunchLease executableLease = exeLeaseResult.Value!;

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

        ManagedReplayArtifactLease artifactLease = artifactResult.Value!;

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

        SuspendedGameProcessLease suspendedLease = suspendedResult.Value!;

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

        // ── 7. Hand off leases (commits: child survives disposal) ──
        (WindowsTrustedExecutableLaunchLease handedOffExe,
            ManagedReplayArtifactLease handedOffArtifact) =
            suspendedLease.HandOffLeases();

        // ── 8. Record managed launch under lock ──
        lock (_gate)
        {
            DisposeLaunchLeases();
            RecordManagedLaunch(launchContext);

            // The suspended lease committed via HandOffLeases so disposal
            // will only close handles without terminating the child process.
            _activeSuspendedLease = suspendedLease;
            _activeExecutableLease = handedOffExe;
            _activeArtifactLease = handedOffArtifact;
        }

        return OperationResult.Success(
            new GameReplayLaunchOutcome(_timeProvider.GetUtcNow()));
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

    private void Revoke() => _authorization = null;

    private void RevokeSession()
    {
        Revoke();
        _managedLaunch = null;
        _lastCursor = null;
        DisposeLaunchLeases();
    }

    private void DisposeLaunchLeases()
    {
        // Disposal order: suspended first (closes handles; HandOffLeases
        // prevents child termination), then artifact (deletes staging file),
        // then executable (releases file lock).
        SuspendedGameProcessLease? suspended = Interlocked.Exchange(ref _activeSuspendedLease, null);
        if (suspended is not null)
        {
            suspended.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ManagedReplayArtifactLease? artifact = Interlocked.Exchange(ref _activeArtifactLease, null);
        if (artifact is not null)
        {
            artifact.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        WindowsTrustedExecutableLaunchLease? executable = Interlocked.Exchange(ref _activeExecutableLease, null);
        if (executable is not null)
        {
            executable.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        foreach (OffsetField field in table.Fields)
        {
            if (field.Offset != 0)
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
        _disposed = true;

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

    public async ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
        MemoryScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int processId;
        long baseAddress;
        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            if (_snapshot.State != GameSessionVerificationState.OfflineReplayVerified
                || _authorization is null)
            {
                return OperationResult.Failure<MemoryScanResult>(
                    new ApplicationError(
                        "discover.gate_not_satisfied",
                        "The offline-session gate is not satisfied. Launch a replay first."));
            }

            if (_authorization.BaseAddress == nint.Zero)
            {
                return OperationResult.Failure<MemoryScanResult>(
                    new ApplicationError(
                        "discover.no_base_address",
                        "The base address of the game process is not available."));
            }

            processId = _authorization.ProcessId;
            baseAddress = _authorization.BaseAddress;
        }

        return await Task.Run(
            () => _scanDiscoverer.Scan(processId, baseAddress, request),
            cancellationToken).ConfigureAwait(false);
    }

    private static OperationResult<GameReplayLaunchOutcome> LaunchFailure(
        ApplicationError error) =>
        OperationResult.Failure<GameReplayLaunchOutcome>(error);
}
