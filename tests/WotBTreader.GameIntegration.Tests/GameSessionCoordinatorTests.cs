using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Session;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameSessionCoordinatorTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private const string LaunchCorrelation = "adapter-generated-correlation";

    [TestMethod]
    public async Task InitialState_IsUnknown()
    {
        var (coordinator, _) = CreateCoordinator();

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(GameSessionVerificationState.Unknown, snapshot.State);
        Assert.IsFalse(snapshot.GamePresent);
    }

    [TestMethod]
    public async Task CompleteFreshBoundEvidence_VerifiesOfflineReplay()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence());

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(
            GameSessionVerificationState.OfflineReplayVerified,
            snapshot.State);
        Assert.AreEqual(StartTime.AddSeconds(15), snapshot.EvidenceExpiresAtUtc);
    }

    [TestMethod]
    public async Task EvidenceWithoutARealWindow_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with { WindowHandle = 0 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public void ObservedProcessEvidence_UsesTheRealEligibleWindow()
    {
        ManagedGameLaunchContext launch = CreateManagedLaunch();
        var identity = new ObservedGameProcessIdentity(
            launch.ProcessId,
            launch.ProcessStartIdentity,
            WindowHandle: 9876,
            launch.TrustedGameIdentity.ExecutablePath,
            new ExecutableFileIdentity(1, 2),
            launch.TrustedGameIdentity.ProductVersion,
            launch.TrustedGameIdentity.ExecutableSha256);

        GameProcessEvidence? evidence =
            GameSessionCoordinator.CreateObservedProcessEvidence(
                launch,
                new GameProcessObservationResult(
                    GameProcessObservationStatus.Available,
                    identity));

        Assert.IsNotNull(evidence);
        Assert.AreEqual(9876, evidence.WindowHandle);
        Assert.AreEqual(launch.ProcessId, evidence.WindowOwnerProcessId);
    }

    [TestMethod]
    public void WindowObservationPolicy_WaitsBeforeVerificationAndFailsAfterLoss()
    {
        // Before a correlated marker arrives, an absent window is not terminal:
        // the client may still be materializing its window. Other statuses fail
        // closed immediately.
        Assert.IsFalse(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.Absent,
            exactWindowObserved: false));
        Assert.IsFalse(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.QueryFailed,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.Ambiguous,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.Unsupported,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.Available,
            exactWindowObserved: false));

        // Once a correlated marker was observed, ANY loss of the exact window
        // is terminal: absence, ambiguity, query failure, unsupported, or a
        // different available window all revoke immediately.
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.Absent,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.Ambiguous,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.QueryFailed,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.Unsupported,
            exactWindowObserved: false));
        Assert.IsTrue(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.Available,
            exactWindowObserved: false));

        // Holding the exact window is never terminal, before or after
        // verification.
        Assert.IsFalse(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: false,
            GameProcessObservationStatus.Available,
            exactWindowObserved: true));
        Assert.IsFalse(GameSessionCoordinator.IsWindowObservationTerminalFailure(
            correlatedEvidenceObserved: true,
            GameProcessObservationStatus.Available,
            exactWindowObserved: true));
    }

    [TestMethod]
    public async Task PostVerificationWindowLoss_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        // The exact window disappears after verification: the observed process
        // is otherwise identical, but the window handle is gone and the owner
        // can no longer be proven.
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with { WindowHandle = 0 },
            Lifecycle = CreateValidLifecycle() with
            {
                SourceSequence = 12,
                SourceByteOffset = 102,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task PostVerificationWindowOwnerChange_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        // The window is still present but its owner PID no longer matches the
        // managed process, which can only mean the window was replaced.
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with { WindowOwnerProcessId = 9999 },
            Lifecycle = CreateValidLifecycle() with
            {
                SourceSequence = 12,
                SourceByteOffset = 102,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task CallerCannotSupplyMissingManagedLaunchCorrelation()
    {
        var (coordinator, _) = CreateCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence());

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
    }

    [TestMethod]
    public async Task IncompleteEvidence_IsUnverified()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(new GameSessionEvidence(
            GamePresent: true,
            MonitorHealthy: true,
            ReplayUiConfirmed: true,
            Process: CreateValidProcess(),
            Lifecycle: null));

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(
            GameSessionVerificationState.GamePresentUnverified,
            snapshot.State);
    }

    [TestMethod]
    public async Task HistoricalEvidence_IsStale()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        ReplayLifecycleEvidence staleLifecycle =
            CreateValidLifecycle() with { ObservedAtUtc = StartTime.AddSeconds(-16) };

        coordinator.ApplyEvidence(
            CreateValidEvidence() with { Lifecycle = staleLifecycle });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.EvidenceStale, snapshot.State);
    }

    [TestMethod]
    public async Task StopEvidence_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                State = ReplayLifecycleState.OfflineReplayStopped,
                SourceSequence = 12,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
    }

    [TestMethod]
    public async Task OnlineBattleEvidence_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                State = ReplayLifecycleState.OnlineBattle,
                SourceSequence = 12,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
    }

    [TestMethod]
    public async Task MonitorFailure_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ReportMonitorFailure();

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.monitor_unhealthy", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ProcessExit_RevokesVerifiedState()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with { IsAlive = false },
            Lifecycle = CreateValidLifecycle() with { SourceSequence = 12 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
    }

    [TestMethod]
    public async Task SamePidWithDifferentStartIdentity_IsDenied()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with { ProcessStartIdentity = 43 },
            Lifecycle = CreateValidLifecycle() with { SourceSequence = 12 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ExactVersionAndHashMismatch_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with
            {
                ObservedProductVersion = "11.18.0.8",
                ObservedExecutableSha256 = new ContentHash(new string('b', 64)),
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task LifecycleBoundToAnotherProcess_IsUnverified()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with { ProcessStartIdentity = 43 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(
            GameSessionVerificationState.GamePresentUnverified,
            snapshot.State);
    }

    [TestMethod]
    public async Task FirstEvidenceForDifferentSuspendedIdentity_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess() with
            {
                ProcessId = 4321,
                ProcessStartIdentity = 43,
                WindowOwnerProcessId = 4321,
            },
            Lifecycle = CreateValidLifecycle() with
            {
                ProcessId = 4321,
                ProcessStartIdentity = 43,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task MarkerAtLaunchBaseline_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with { SourceSequence = 10 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task MarkerFromSecondBaselineSource_IsVerified()
    {
        var (coordinator, _) = CreateCoordinator();
        ContentHash secondSource = Hash('b');
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
        [
            new LifecycleSourceCursor(Hash('a'), 1, 100),
            new LifecycleSourceCursor(secondSource, 2, 200),
        ]));

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = secondSource.Value,
                SourceGeneration = 2,
                SourceByteOffset = 201,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, snapshot.State);
    }

    [TestMethod]
    public async Task MarkerFromNewLiveGenerationOneSource_IsVerified()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = Hash('b').Value,
                SourceGeneration = 1,
                SourceByteOffset = 50,
                Provenance = LifecycleMarkerProvenance.Live,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, snapshot.State);
    }

    [TestMethod]
    public async Task MarkerFromNewSourceAfterHealthyEmptyBaseline_IsVerified()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch([]));

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = Hash('b').Value,
                SourceGeneration = 1,
                SourceByteOffset = 50,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, snapshot.State);
    }

    [TestMethod]
    public async Task MarkerFromNewSourceWithStaleTimestamp_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch([]));

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = Hash('b').Value,
                SourceGeneration = 1,
                SourceByteOffset = 50,
                SourceTimestampUtc = StartTime.AddMinutes(-2),
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task MarkerFromNewHistoricalSource_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = Hash('b').Value,
                SourceGeneration = 1,
                SourceByteOffset = 50,
                Provenance = LifecycleMarkerProvenance.Historical,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task MarkerFromNewAdvancedGenerationSource_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                SourceIdentity = Hash('b').Value,
                SourceGeneration = 2,
                SourceByteOffset = 50,
            },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task MarkerAtSourceByteBaseline_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        coordinator.RecordManagedLaunch(CreateManagedLaunch());

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with { SourceByteOffset = 100 },
        });

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ReusedOrRegressedCursor_IsDenied()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(CreateValidEvidence());

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.cursor_invalid", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task AuthorizationExpiry_IsObservedWithoutNewEvidence()
    {
        var (coordinator, timeProvider) = CreateVerifiedCoordinator();
        timeProvider.Advance(TimeSpan.FromSeconds(16));

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(GameSessionVerificationState.EvidenceStale, snapshot.State);
        Assert.AreEqual("evidence.expired", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task VerifiedLivenessHeartbeat_ExtendsAuthorizationExpiry()
    {
        // Quiet replay playback produces no new Start markers, so the liveness
        // heartbeat must keep a verified game alive past the default 15s
        // evidence lifetime (OD-044: a 281s replay was terminated ~120s after
        // verification because the authorization expired mid-battle). The
        // monitor heartbeats every ~500ms; here one mid-window heartbeat must
        // carry the authorization past the original marker-based expiry.
        var (coordinator, timeProvider) = CreateCoordinator();
        ManagedGameLaunchContext launch = CreateManagedLaunch();
        coordinator.RecordManagedLaunch(launch);
        coordinator.ApplyEvidence(CreateValidEvidence());

        // 10s into the window, a fresh healthy process observation rolls the
        // expiry forward by the lifetime again.
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        coordinator.RefreshVerifiedEvidence(
            launch,
            CreateValidProcess(),
            CancellationToken.None);

        // Past the original 15s expiry (t=20s) but inside the heartbeat window
        // (expiry t=25s): still verified.
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(
            GameSessionVerificationState.OfflineReplayVerified,
            snapshot.State);
        Assert.IsTrue(snapshot.EvidenceExpiresAtUtc > timeProvider.GetUtcNow());
    }

    [TestMethod]
    public async Task VerifiedLivenessHeartbeat_WithIdentityDrift_IsDenied()
    {
        var (coordinator, _) = CreateCoordinator();
        ManagedGameLaunchContext launch = CreateManagedLaunch();
        coordinator.RecordManagedLaunch(launch);
        coordinator.ApplyEvidence(CreateValidEvidence());

        // The heartbeat must never extend authorization for a different
        // process identity — fail closed exactly like a fresh evidence apply.
        coordinator.RefreshVerifiedEvidence(
            launch,
            CreateValidProcess() with { ProcessStartIdentity = 43 },
            CancellationToken.None);

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("process.identity_mismatch", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task VerifiedLivenessHeartbeat_BeforeVerification_IsNoOp()
    {
        // A launch awaiting evidence must not be promoted to Verified by the
        // heartbeat: only ApplyEvidence's marker correlation can do that.
        var (coordinator, _) = CreateCoordinator();
        ManagedGameLaunchContext launch = CreateManagedLaunch();
        coordinator.RecordManagedLaunch(launch);

        coordinator.RefreshVerifiedEvidence(
            launch,
            CreateValidProcess(),
            CancellationToken.None);

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Unknown, snapshot.State);
    }

    [TestMethod]
    public async Task VerifiedLivenessHeartbeat_ForSupersededLaunch_IsNoOp()
    {
        // The heartbeat of a replaced launch must neither extend nor revoke:
        // it belongs to an earlier generation whose leases were detached.
        var (coordinator, _) = CreateCoordinator();
        ManagedGameLaunchContext firstLaunch = CreateManagedLaunch();
        coordinator.RecordManagedLaunch(firstLaunch);
        coordinator.ApplyEvidence(CreateValidEvidence());

        ManagedGameLaunchContext secondLaunch = CreateManagedLaunch(
            sourceBaselines: [new LifecycleSourceCursor(Hash('b'), 2, 200)]);
        coordinator.RecordManagedLaunch(secondLaunch);

        coordinator.RefreshVerifiedEvidence(
            firstLaunch,
            CreateValidProcess(),
            CancellationToken.None);

        // Still awaiting evidence for the second launch, unchanged.
        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Unknown, snapshot.State);
    }

    [TestMethod]
    public async Task VerifiedLivenessHeartbeat_CancelledMonitor_IsNoOp()
    {
        // A monitor that has been stopped (token cancelled) must not keep
        // extending the authorization; the terminal path owns revocation.
        var (coordinator, timeProvider) = CreateCoordinator();
        ManagedGameLaunchContext launch = CreateManagedLaunch();
        coordinator.RecordManagedLaunch(launch);
        coordinator.ApplyEvidence(CreateValidEvidence());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        coordinator.RefreshVerifiedEvidence(
            launch,
            CreateValidProcess(),
            cancelled.Token);

        timeProvider.Advance(TimeSpan.FromSeconds(16));
        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.EvidenceStale, snapshot.State);
        Assert.AreEqual("evidence.expired", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ExplicitResearchLifetime_ExtendsExpiryButNotStopRevocation()
    {
        var options = new GameIntegrationOptions
        {
            OfflineReplayEvidenceLifetime = TimeSpan.FromMinutes(2),
        };
        var (coordinator, timeProvider) = CreateVerifiedCoordinator(options);
        timeProvider.Advance(TimeSpan.FromSeconds(16));

        GameSessionSnapshot active =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, active.State);
        Assert.AreEqual(StartTime.AddMinutes(2), active.EvidenceExpiresAtUtc);

        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Lifecycle = CreateValidLifecycle() with
            {
                State = ReplayLifecycleState.OfflineReplayStopped,
                ObservedAtUtc = StartTime.AddSeconds(16),
                SourceSequence = 12,
                SourceByteOffset = 102,
            },
        });

        GameSessionSnapshot denied =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, denied.State);
        Assert.AreEqual("evidence.lifecycle_denied", denied.ReasonCode);
    }

    [TestMethod]
    public async Task ExplicitResearchLifetime_DoesNotDelayMonitorFailure()
    {
        var options = new GameIntegrationOptions
        {
            OfflineReplayEvidenceLifetime = TimeSpan.FromMinutes(2),
        };
        var (coordinator, timeProvider) = CreateVerifiedCoordinator(options);
        timeProvider.Advance(TimeSpan.FromSeconds(16));

        coordinator.ReportMonitorFailure();

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual("evidence.monitor_unhealthy", snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task ExplicitResearchLifetime_ExpiresAtHardMaximum()
    {
        var options = new GameIntegrationOptions
        {
            OfflineReplayEvidenceLifetime = TimeSpan.FromMinutes(2),
        };
        var (coordinator, timeProvider) = CreateVerifiedCoordinator(options);
        timeProvider.Advance(TimeSpan.FromSeconds(119));

        GameSessionSnapshot active =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, active.State);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        GameSessionSnapshot expired =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.EvidenceStale, expired.State);
        Assert.AreEqual("evidence.expired", expired.ReasonCode);
    }

    [TestMethod]
    public async Task MemoryObservation_ReturnsAvailableWhenVerified()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        GameMemoryObservation observation =
            await coordinator.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(
            GameMemoryObservationAvailability.Available,
            observation.Availability);
        Assert.IsNull(observation.ReplayTimeSeconds);
        Assert.IsNull(observation.PlayerHitPoints);
    }

    [TestMethod]
    public async Task Launch_PropagatesFailureWhenPreparerFails()
    {
        var (coordinator, _) = CreateCoordinator(
            preparer: new FailingPreparer("game.launch.preparer_failed"));

        var result = await coordinator.LaunchAsync(
            new GameReplayLaunchRequest(SourceArtifactId.New()),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.launch.preparer_failed", result.Error?.Code);
    }

    [TestMethod]
    public async Task ScannerRetriesModuleBaseResolutionWithoutRevokingVerifiedEvidence()
    {
        var resolver = new SequencedModuleBaseResolver(nint.Zero, (nint)0x10000000);
        var (coordinator, _) = CreateCoordinator(moduleBaseAddressResolver: resolver);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<MemoryScanResult> first = await coordinator.ScanAsync(
            new MemoryScanRequest("position", "Float", [0, 0, 0, 0], null, 1, 4096),
            CancellationToken.None);

        Assert.IsFalse(first.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", first.Error?.Code);
        GameSessionSnapshot afterFirst = await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.OfflineReplayVerified, afterFirst.State);

        OperationResult<MemoryScanResult> second = await coordinator.ScanAsync(
            new MemoryScanRequest("position", "Float", [0, 0, 0, 0], null, 1, 4096),
            CancellationToken.None);

        Assert.IsFalse(second.IsSuccess);
        Assert.AreNotEqual("discover.gate_not_satisfied", second.Error?.Code);
        Assert.AreEqual(2, resolver.CallCount);
    }

    [TestMethod]
    public async Task ModuleBaseResolutionCancellation_StopsBeforeStartingAnyScan()
    {
        using CancellationTokenSource cancellation = new();
        var resolver = new CancellingModuleBaseResolver(cancellation);
        var (coordinator, _) = CreateCoordinator(moduleBaseAddressResolver: resolver);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.ScanAsync(
                new MemoryScanRequest("position", "Float", [0, 0, 0, 0], null, 1, 4096),
                cancellation.Token));
    }

    [TestMethod]
    public async Task DiscoveryCancellation_IsHonoredBeforeStartingAnyScan()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.CreateSnapshotAsync(
                new MemorySnapshotRequest(4, -500, 500, null, null, 0, 0),
                cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.CompareAsync("000001", "changed", 10, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.ScanAsync(
                new MemoryScanRequest("yaw", "Float", [0, 0, 0, 0], null, 1, 4096),
                cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await coordinator.ScanNeighborhoodAsync(
                new MemoryNeighborhoodRequest(1, 64, true, true, false, null, null, null, null),
                cancellation.Token));
    }

    [TestMethod]
    public async Task InstructionSnapshot_UsesVerifiedIdentityAndReturnsProjectedCapture()
    {
        DateTimeOffset capturedAt = StartTime.AddSeconds(1);
        var runner = new RecordingInstructionSnapshotRunner(
            new InstructionSnapshotRunnerOutcome(
                IsSuccess: true,
                new InstructionSnapshotResult(
                    StartTime,
                    capturedAt,
                    "completed",
                    "wotblitz.exe",
                    0x22FA78D,
                    InstructionFingerprintMatched: true,
                    CleanupProven: true,
                    Truncated: false,
                    []),
                Error: null,
                CleanupProven: true));
        var (coordinator, _) = CreateCoordinator(instructionSnapshotRunner: runner);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<InstructionSnapshotResult> result =
            await coordinator.CaptureInstructionSnapshotAsync(
                new InstructionSnapshotRequest(2_000, 8),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(runner.Request);
        Assert.AreEqual(1234, runner.Request!.ProcessId);
        Assert.AreEqual(42, runner.Request.ProcessStartIdentity);
        Assert.AreEqual(@"C:\Games\wotblitz.exe", runner.Request.CanonicalExecutablePath);
        Assert.AreEqual(2_000, runner.Request.DurationMilliseconds);
        Assert.AreEqual(8, runner.Request.MaxHits);
    }

    [TestMethod]
    public async Task InstructionSnapshot_CleanupFailureRevokesVerifiedSession()
    {
        var runner = new RecordingInstructionSnapshotRunner(
            new InstructionSnapshotRunnerOutcome(
                IsSuccess: false,
                Result: null,
                new ApplicationError(
                    "discover.instruction_snapshot.cleanup_unproven",
                    "Test failure."),
                CleanupProven: false));
        GameSessionCoordinator? coordinatorForRefresh = null;
        runner.BeforeReturn = () => coordinatorForRefresh!.ApplyEvidence(
            CreateValidEvidence() with
            {
                Lifecycle = CreateValidLifecycle() with
                {
                    SourceSequence = 12,
                    SourceByteOffset = 102,
                },
            });
        var (coordinator, _) = CreateCoordinator(instructionSnapshotRunner: runner);
        coordinatorForRefresh = coordinator;
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<InstructionSnapshotResult> result =
            await coordinator.CaptureInstructionSnapshotAsync(
                new InstructionSnapshotRequest(),
                CancellationToken.None);
        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "discover.instruction_snapshot.cleanup_unproven",
            result.Error?.Code);
        Assert.AreEqual(GameSessionVerificationState.Denied, snapshot.State);
        Assert.AreEqual(
            "discover.instruction_snapshot.cleanup_unproven",
            snapshot.ReasonCode);
    }

    [TestMethod]
    public async Task InstructionSnapshot_InvalidBoundsAndMissingGateNeverCallRunner()
    {
        var runner = new RecordingInstructionSnapshotRunner(
            new InstructionSnapshotRunnerOutcome(
                IsSuccess: false,
                Result: null,
                new ApplicationError("unexpected", "Should not run."),
                CleanupProven: true));
        var (coordinator, _) = CreateCoordinator(instructionSnapshotRunner: runner);

        OperationResult<InstructionSnapshotResult> invalid =
            await coordinator.CaptureInstructionSnapshotAsync(
                new InstructionSnapshotRequest(5_001, 65),
                CancellationToken.None);
        OperationResult<InstructionSnapshotResult> gated =
            await coordinator.CaptureInstructionSnapshotAsync(
                new InstructionSnapshotRequest(),
                CancellationToken.None);

        Assert.AreEqual("discover.instruction_snapshot.invalid_options", invalid.Error?.Code);
        Assert.AreEqual("discover.gate_not_satisfied", gated.Error?.Code);
        Assert.IsNull(runner.Request);
    }

    [TestMethod]
    public async Task ObservationCancellation_IsHonoredBeforeReturningData()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await coordinator.ObserveAsync(cancellation.Token));
    }

    [TestMethod]
    public async Task AbsentEvidence_ClearsPresence()
    {
        var (coordinator, _) = CreateVerifiedCoordinator();

        coordinator.ApplyEvidence(new GameSessionEvidence(
            GamePresent: false,
            MonitorHealthy: false,
            ReplayUiConfirmed: false,
            Process: null,
            Lifecycle: null));

        GameSessionSnapshot snapshot =
            await coordinator.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(GameSessionVerificationState.GameAbsent, snapshot.State);
        Assert.IsFalse(snapshot.GamePresent);
    }

    private static (GameSessionCoordinator Coordinator, ManualTimeProvider TimeProvider)
        CreateCoordinator(
            IManagedLaunchPreparer? preparer = null,
            IManagedReplayArtifactStager? artifactStager = null,
            ISuspendedProcessPlatform? suspendedPlatform = null,
            IManagedLaunchCorrelationRegistrar? correlationRegistrar = null,
            IThreadResumePlatform? threadResumePlatform = null,
            IGameProcessIdentityObserver? processIdentityObserver = null,
            IGuardedMemoryReaderFactory? memoryReaderFactory = null,
            IGameProcessModuleBaseAddressResolver? moduleBaseAddressResolver = null,
            IOffsetTableReader? offsetTableReader = null,
            IBlitzReplayLifecycleFeed? lifecycleFeed = null,
            IInstructionSnapshotRunner? instructionSnapshotRunner = null,
            GameIntegrationOptions? options = null)
    {
        var timeProvider = new ManualTimeProvider(StartTime);
        return (new GameSessionCoordinator(
            timeProvider,
            options ?? new GameIntegrationOptions(),
            NullLogger<GameSessionCoordinator>.Instance,
            preparer ?? new StubPreparer(),
            artifactStager ?? new StubArtifactStager(),
            suspendedPlatform ?? new StubSuspendedPlatform(),
            correlationRegistrar ?? new StubCorrelationRegistrar(),
            threadResumePlatform ?? new StubThreadResumePlatform(),
            processIdentityObserver ?? new StubProcessIdentityObserver(),
            memoryReaderFactory ?? new StubMemoryReaderFactory(),
            moduleBaseAddressResolver ?? new FixedModuleBaseResolver((nint)0x10000000),
            offsetTableReader ?? new StubOffsetTableReader(),
            new MemoryScanDiscoverer(timeProvider, NullLogger<MemoryScanDiscoverer>.Instance),
            new MemoryScanEngine(timeProvider, NullLogger<MemoryScanEngine>.Instance),
            lifecycleFeed ?? new StubLifecycleFeed(),
            instructionSnapshotRunner ?? new StubInstructionSnapshotRunner()), timeProvider);
    }

    private static (GameSessionCoordinator Coordinator, ManualTimeProvider TimeProvider)
        CreateVerifiedCoordinator(GameIntegrationOptions? options = null)
    {
        var pair = CreateCoordinator(options: options);
        pair.Coordinator.RecordManagedLaunch(CreateManagedLaunch());
        pair.Coordinator.ApplyEvidence(CreateValidEvidence());
        return pair;
    }

    private static GameSessionEvidence CreateValidEvidence() =>
        new(
            GamePresent: true,
            MonitorHealthy: true,
            ReplayUiConfirmed: true,
            CreateValidProcess(),
            CreateValidLifecycle());

    private static GameProcessEvidence CreateValidProcess() =>
        new(
            ProcessId: 1234,
            ProcessStartIdentity: 42,
            IsAlive: true,
            ObservedCanonicalExecutablePath: @"C:\Games\wotblitz.exe",
            ObservedProductVersion: "11.18.0.7",
            ObservedExecutableSha256: new ContentHash(new string('a', 64)),
            WindowHandle: 99,
            WindowOwnerProcessId: 1234);

    private static ReplayLifecycleEvidence CreateValidLifecycle() =>
        new(
            ReplayLifecycleState.OfflineReplayStarted,
            StartTime,
            SourceTimestampUtc: StartTime,
            ReplayEvidenceSource.BlitzNativeLog,
            SourceIdentity: Hash('a').Value,
            SourceGeneration: 1,
            SourceSequence: 11,
            SourceByteOffset: 101,
            Provenance: LifecycleMarkerProvenance.Live,
            ProcessId: 1234,
            ProcessStartIdentity: 42,
            LaunchCorrelation);

    private static ManagedGameLaunchContext CreateManagedLaunch(
        IReadOnlyList<LifecycleSourceCursor>? sourceBaselines = null) =>
        new(
            LaunchCorrelation,
            new InstalledGameIdentity(
                ExecutablePath: @"C:\Games\wotblitz.exe",
                ProductVersion: "11.18.0.7",
                ExecutableSha256: new ContentHash(new string('a', 64)),
                ResourceRoot: @"C:\Games",
                DlcRoots: []),
            processId: 1234,
            processStartIdentity: 42,
            sourceBaselines ?? [new LifecycleSourceCursor(Hash('a'), 1, 100)],
            sourceSequenceBaseline: 10,
            lifecycleBaselineCapturedAtUtc: StartTime.AddMinutes(-1));

    private static ContentHash Hash(char value) => new(new string(value, 64));

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    // ── M2 stubs for tests that only exercise evidence evaluation ──

    private sealed class StubPreparer : IManagedLaunchPreparer
    {
        public ValueTask<OperationResult<ManagedLaunchPreparation>> PrepareAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("StubPreparer is not intended for LaunchAsync tests.");
    }

    private sealed class StubArtifactStager : IManagedReplayArtifactStager
    {
        public ValueTask<OperationResult<ManagedReplayArtifactLease>> StageAsync(
            SourceArtifactId sourceArtifactId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("StubArtifactStager is not intended for LaunchAsync tests.");
    }

    private sealed class StubSuspendedPlatform : ISuspendedProcessPlatform
    {
        public ValueTask<OperationResult<SuspendedGameProcessLease>> CreateAsync(
            WindowsTrustedExecutableLaunchLease executableLease,
            ManagedReplayArtifactLease artifactLease,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("StubSuspendedPlatform is not intended for LaunchAsync tests.");
    }

    private sealed class StubCorrelationRegistrar : IManagedLaunchCorrelationRegistrar
    {
        public OperationResult<ManagedGameLaunchContext> Register(
            ManagedLaunchPreparation preparation,
            SuspendedGameProcessLease suspendedLease) =>
            throw new NotSupportedException("StubCorrelationRegistrar is not intended for LaunchAsync tests.");
    }

    private sealed class StubThreadResumePlatform : IThreadResumePlatform
    {
        public OperationResult<ThreadResumeOutcome> Resume(SafeThreadHandle threadHandle) =>
            throw new NotSupportedException("StubThreadResumePlatform is not intended for LaunchAsync tests.");
    }

    private sealed class StubInstructionSnapshotRunner : IInstructionSnapshotRunner
    {
        public ValueTask<InstructionSnapshotRunnerOutcome> RunAsync(
            InstructionSnapshotExecutionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "StubInstructionSnapshotRunner is not intended for unrelated coordinator tests.");
    }

    private sealed class RecordingInstructionSnapshotRunner(
        InstructionSnapshotRunnerOutcome outcome) : IInstructionSnapshotRunner
    {
        public InstructionSnapshotExecutionRequest? Request { get; private set; }

        public Action? BeforeReturn { get; set; }

        public ValueTask<InstructionSnapshotRunnerOutcome> RunAsync(
            InstructionSnapshotExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            BeforeReturn?.Invoke();
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class FailingPreparer(string errorCode) : IManagedLaunchPreparer
    {
        public ValueTask<OperationResult<ManagedLaunchPreparation>> PrepareAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<ManagedLaunchPreparation>(
                new ApplicationError(errorCode, "Test failure.", Retryable: false)));
    }

    private sealed class FixedModuleBaseResolver(nint baseAddress) : IGameProcessModuleBaseAddressResolver
    {
        public nint Resolve(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return baseAddress;
        }
    }

    private sealed class SequencedModuleBaseResolver(params nint[] results) : IGameProcessModuleBaseAddressResolver
    {
        private readonly nint[] _results = results;
        private int _index;

        public int CallCount => _index;

        public nint Resolve(int processId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Interlocked.Increment(ref _index) - 1;
            cancellationToken.ThrowIfCancellationRequested();
            return _results[Math.Min(index, _results.Length - 1)];
        }
    }

    private sealed class CancellingModuleBaseResolver(CancellationTokenSource cancellation)
        : IGameProcessModuleBaseAddressResolver
    {
        public nint Resolve(int processId, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return (nint)0x10000000;
        }
    }

    private sealed class StubOffsetTableReader : IOffsetTableReader
    {
        public OperationResult<OffsetTable?> Load(
            string gameVersion,
            string executableSha256,
            CancellationToken cancellationToken = default) =>
            OperationResult.Success<OffsetTable?>(null);
    }

    private sealed class StubMemoryReaderFactory : IGuardedMemoryReaderFactory
    {
        public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
            AuthorizedMemoryObservation observation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("StubMemoryReaderFactory is not intended for evidence evaluation tests.");
    }

    private sealed class StubProcessIdentityObserver : IGameProcessIdentityObserver
    {
        public ValueTask<GameProcessObservationResult> ObserveAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GameProcessObservationResult(
                GameProcessObservationStatus.Absent,
                Identity: null));
    }

    private sealed class StubLifecycleFeed : IBlitzReplayLifecycleFeed
    {
        public ValueTask<LifecycleFeedBaseline> CaptureBaselineAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedBaseline(
                0, 0, LifecycleFeedHealth.Healthy, [])
            {
                CapturedAtUtc = StartTime,
            });

        public ValueTask<LifecycleFeedBaseline> CaptureReconciledBaselineAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedBaseline(
                0, 0, LifecycleFeedHealth.Healthy, [])
            {
                CapturedAtUtc = StartTime,
            });

        public ValueTask<LifecycleFeedReadResult> ReadAfterAsync(
            long afterSequence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedReadResult(
                afterSequence, afterSequence, false, []));
    }
}
