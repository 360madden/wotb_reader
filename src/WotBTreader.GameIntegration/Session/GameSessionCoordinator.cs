using WotBTreader.Application.Game;
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
/// Owns the evidence-backed offline state. Public consumers receive only safe
/// snapshots and observations; authorization and process identity never leave
/// this adapter.
/// </summary>
internal sealed class GameSessionCoordinator(
    TimeProvider timeProvider)
    : IGameSessionState, IGameReplayLauncher, IGameMemoryObserver
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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

    public ValueTask<OperationResult<GameReplayLaunchOutcome>> LaunchAsync(
        GameReplayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        return ValueTask.FromResult(
            OperationResult.Failure<GameReplayLaunchOutcome>(
                new ApplicationError(
                    "game.launch.unavailable",
                    "Managed replay launch is not available.",
                    Retryable: false)));
    }

    public ValueTask<GameMemoryObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ExpireAuthorizationIfNeeded();
            return ValueTask.FromResult(new GameMemoryObservation(
                GameMemoryObservationAvailability.Unknown,
                _timeProvider.GetUtcNow(),
                ReplayTimeSeconds: null,
                PlayerHitPoints: null,
                PlayerPositionX: null,
                PlayerPositionY: null,
                PlayerPositionZ: null,
                PlayerYaw: null,
                CameraPitch: null,
                AliveTankCount: null));
        }
    }

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
        _authorization = new AuthorizedObservation(
            ++_authorizationGeneration,
            process.ProcessId,
            process.ProcessStartIdentity,
            process.ObservedCanonicalExecutablePath,
            process.ObservedProductVersion,
            process.ObservedExecutableSha256,
            expiresAtUtc);
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
        DateTimeOffset ExpiresAtUtc);

    private sealed record EvidenceCursor(
        string SourceIdentity,
        long SourceGeneration,
        long SourceSequence);
}
