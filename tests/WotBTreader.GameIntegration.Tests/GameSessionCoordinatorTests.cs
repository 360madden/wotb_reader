using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
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
    public async Task EntityPositionRead_UnsupportedBuildDoesNotCreateMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, result.Value?.Status);
        Assert.AreEqual("build-identity", result.Value?.FailureStage);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityPositionRead_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityPositionRead_ExactBuildUsesServerOwnedLayout()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        Assert.AreEqual(12.5f, result.Value?.X);
        Assert.IsTrue(result.Value?.ModuleRooted);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(0x10000000, factory.Reader.ModuleBase.ToInt64());
        Assert.AreEqual(4242, factory.Reader.EntityId);
        Assert.AreSame(layout, factory.Reader.Layout);
        Assert.AreEqual(layout.GameVersion, factory.Observation?.ProductVersion);
        Assert.AreEqual(executableHash, factory.Observation?.ExecutableSha256);
    }

    [TestMethod]
    public async Task EntityPositionAddress_ExactBuildReturnsRecordAndPage()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityPositionAddressResult> result = await coordinator
            .ResolveEntityPositionAddressAsync(
                new EntityPositionAddressRequest(4242),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        Assert.AreEqual(0x25000038u, result.Value?.RecordAddress);
        Assert.AreEqual(0x25000000u, result.Value?.PageAddress);
        Assert.IsTrue(result.Value?.ModuleRooted);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(4242, factory.Reader.EntityId);
        Assert.AreSame(layout, factory.Reader.Layout);
    }

    [TestMethod]
    public async Task EntityPositionAddress_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityPositionAddressResult> result = await coordinator
            .ResolveEntityPositionAddressAsync(
                new EntityPositionAddressRequest(4242),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityPositionAddress_UnsupportedBuildFailsClosed()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<EntityPositionAddressResult> result = await coordinator
            .ResolveEntityPositionAddressAsync(
                new EntityPositionAddressRequest(4242),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.entity_position.address_unsupported_build", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionRead_ExactBuildReturnsBytesOnly()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 8),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        CollectionAssert.AreEqual(expectedRegion, result.Value?.RegionBytes);
        Assert.IsTrue(result.Value?.ModuleRooted);
        // No battle session id supplied -> replay time and clock flag stay null/false.
        Assert.IsNull(result.Value?.ReplayTimeSeconds);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(8, factory.Reader.RegionReads[0].Length);
        // The read goes to the resolved ring-record address, never returned.
        Assert.AreEqual(0x25000038, factory.Reader.RegionReads[0].Address.ToInt64());
        Assert.AreEqual(4242, factory.Reader.EntityId);
        Assert.AreSame(layout, factory.Reader.Layout);
    }

    [TestMethod]
    public async Task EntityRegionRead_WithSessionIdAttestsClockAndLabelsReplayTime()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [1, 2, 3, 4];
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion);
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Estimated, TimeSpan.FromMilliseconds(500)));
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            replayClockSource: clock);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 4, sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(expectedRegion, result.Value?.RegionBytes);
        Assert.IsTrue(result.Value?.SameDecodedClockProven);
        Assert.AreEqual(sessionId, clock.LastRequestedSessionId);
        Assert.IsNotNull(result.Value?.ReplayTimeSeconds);
    }

    [TestMethod]
    public async Task EntityRegionRead_TankRecordAnchor_DerefsEntityPlus0x3CAndReadsThere()
    {
        // The HP / damage-dealt harness anchors the dump at the per-entity
        // tank record [entity+0x3C], NOT the movement ring record. The
        // coordinator must dereference the pointer itself under the lease and
        // read the region from the tank record.
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [9, 8, 7, 6, 5, 4, 3, 2];
        const uint tankRecord = 0x3a100000;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion,
            tankRecordAddress: tankRecord);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(
                    4242,
                    RegionLength: expectedRegion.Length,
                    RegionAnchor: EntityRecordRegionAnchor.EntityTankRecord),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        CollectionAssert.AreEqual(expectedRegion, result.Value?.RegionBytes);
        // Two reads under the lease: the 4-byte pointer probe at
        // [entity + 0x3C] then the region read AT the tank record address.
        Assert.HasCount(2, factory.Reader.RegionReads);
        Assert.AreEqual(0x25000064, factory.Reader.RegionReads[0].Address.ToInt64());
        Assert.AreEqual(4, factory.Reader.RegionReads[0].Length);
        Assert.AreEqual(tankRecord, factory.Reader.RegionReads[1].Address.ToInt64());
        Assert.AreEqual(expectedRegion.Length, factory.Reader.RegionReads[1].Length);
    }

    [TestMethod]
    public async Task EntityRegionRead_EntityBaseAnchor_ReadsEntityRecordDirectly()
    {
        // The static playerHP evidence (VerifyPlayerHpChain, 11.19.0.10)
        // pins current health as a SIGNED int16 at [entity+0xB8] on the
        // ENTITY BASE record (alive byte +0xBA, healing int16 +0x11E) — not
        // inside the tank record at [entity+0x3C]. The entity-base anchor
        // reads the region directly at the resolved entity address with a
        // single guarded read (no pointer deref).
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = new byte[0x120];
        // Plant the statically-verified layout: health int16 at +0xB8.
        BinaryPrimitives.WriteInt16LittleEndian(
            expectedRegion.AsSpan(0xB8), (short)500);
        expectedRegion[0xBA] = 1;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(
                    4242,
                    RegionLength: expectedRegion.Length,
                    RegionAnchor: EntityRecordRegionAnchor.EntityBase),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        CollectionAssert.AreEqual(expectedRegion, result.Value?.RegionBytes);
        // Exactly one read under the lease, AT the entity base (no pointer
        // probe — the anchor IS the record).
        Assert.HasCount(1, factory.Reader.RegionReads);
        Assert.AreEqual(0x25000028, factory.Reader.RegionReads[0].Address.ToInt64());
        Assert.AreEqual(expectedRegion.Length, factory.Reader.RegionReads[0].Length);
    }

    [TestMethod]
    public async Task EntityRegionRead_TankRecordAnchor_MissingEntityBaseFailsClosed()
    {
        // Resolver status Resolved but no entity base -> the tank-record
        // anchor cannot deref; fail closed without a region read.
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            addressResult: new Type10EntityPositionAddressResult(
                Type10EntityPositionStatus.Resolved,
                RecordAddress: 0x25000038,
                PageAddress: 0x25000000,
                EntityAddress: null,
                FailureStage: null,
                Attempts: 1,
                NodesVisited: 0,
                ModuleRooted: true));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(
                    4242,
                    RegionLength: 8,
                    RegionAnchor: EntityRecordRegionAnchor.EntityTankRecord),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.entity_region.tank_record_unresolved", result.Error?.Code);
        // No entity base -> the coordinator never even probes; no reads at all.
        Assert.IsEmpty(factory.Reader.RegionReads);
    }

    [TestMethod]
    public async Task EntityRegionRead_InvalidLengthFailsClosedBeforeGate()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 0),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.entity_region.invalid_length", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);

        OperationResult<EntityRecordRegionReadResult> tooBig = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 5000),
                CancellationToken.None);
        Assert.IsFalse(tooBig.IsSuccess);
        Assert.AreEqual("discover.entity_region.invalid_length", tooBig.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionRead_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 8),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionRead_UnsupportedBuildFailsClosed()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 8),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, result.Value?.Status);
        Assert.IsNull(result.Value?.RegionBytes);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionRead_UnresolvedEntityReturnsNullBytes()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            addressResult: new Type10EntityPositionAddressResult(
                Type10EntityPositionStatus.EntityNotFound,
                RecordAddress: null,
                PageAddress: null,
                EntityAddress: null,
                FailureStage: "entity-lookup",
                Attempts: 3,
                NodesVisited: 5,
                ModuleRooted: true));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRecordRegionReadResult> result = await coordinator
            .ReadEntityRegionAsync(
                new EntityRecordRegionReadRequest(4242, RegionLength: 8),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, result.Value?.Status);
        Assert.IsNull(result.Value?.RegionBytes);
        Assert.AreEqual("entity-lookup", result.Value?.FailureStage);
        // The region read must never fire for an unresolved entity.
        Assert.IsEmpty(factory.Reader.RegionReads);
    }

    [TestMethod]
    public async Task EntityRegionsRead_ExactBuildReturnsBytesInRequestOrder()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80];
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [
                        new EntityRegionReadRequestItem(4242, RegionLength: 8),
                        new EntityRegionReadRequestItem(4243, RegionLength: 8),
                    ]),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRegionsReadResult batch = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, batch.Status);
        Assert.HasCount(2, batch.Regions);
        // Request order preserved, both resolved with the expected bytes.
        Assert.AreEqual(4242, batch.Regions[0].EntityId);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, batch.Regions[0].Status);
        CollectionAssert.AreEqual(expectedRegion, batch.Regions[0].RegionBytes);
        Assert.AreEqual(4243, batch.Regions[1].EntityId);
        CollectionAssert.AreEqual(expectedRegion, batch.Regions[1].RegionBytes);
        // One guarded reader, one read per entity at the ring-record address.
        Assert.AreEqual(1, factory.CreateCount);
        Assert.HasCount(2, factory.Reader.RegionReads);
        Assert.AreEqual(0x25000038, factory.Reader.RegionReads[0].Address.ToInt64());
        Assert.AreEqual(0x25000038, factory.Reader.RegionReads[1].Address.ToInt64());
        // No battle session id -> no clock attestation, no replay label.
        Assert.IsNull(batch.ReplayTimeSeconds);
        Assert.IsFalse(batch.SameDecodedClockProven);
        // The read-pass window is measured (item-7 groundwork): present,
        // sane ordering, and no snapshot moment (no session id).
        Assert.IsNotNull(batch.Measurement);
        Assert.IsTrue(
            batch.Measurement!.BatchEndedAtUtc >= batch.Measurement.BatchStartedAtUtc);
        Assert.IsNull(batch.Measurement.ClockSnapshotAtUtc);
    }

    [TestMethod]
    public async Task EntityRegionsRead_WithSessionIdAttestsClockOnceForWholeBatch()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [1, 2, 3, 4];
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion);
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Estimated, TimeSpan.FromMilliseconds(500)));
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            replayClockSource: clock);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [
                        new EntityRegionReadRequestItem(4242, RegionLength: 4),
                        new EntityRegionReadRequestItem(4243, RegionLength: 4),
                    ],
                    sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRegionsReadResult batch = result.Value!;
        Assert.IsTrue(batch.SameDecodedClockProven);
        Assert.IsNotNull(batch.ReplayTimeSeconds);
        Assert.AreEqual(sessionId, clock.LastRequestedSessionId);
        // ONE snapshot for the whole batch, not one per entity.
        Assert.AreEqual(1, clock.CallCount);
        // Per-entity time mirrors carry the batch label.
        Assert.AreEqual(batch.ReplayTimeSeconds, batch.Regions[0].ReplayTimeSeconds);
        Assert.AreEqual(batch.ReplayTimeSeconds, batch.Regions[1].ReplayTimeSeconds);
        // The snapshot moment is measured so the label-vs-read gap is
        // quantifiable.
        Assert.IsNotNull(batch.Measurement);
        Assert.IsNotNull(batch.Measurement!.ClockSnapshotAtUtc);
    }

    [TestMethod]
    public async Task EntityRegionsRead_OneUnresolvedEntityFailsOnlyItself()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        byte[] expectedRegion = [0xAA, 0xBB, 0xCC, 0xDD];
        var resolved = new Type10EntityPositionAddressResult(
            Type10EntityPositionStatus.Resolved,
            RecordAddress: 0x25000038,
            PageAddress: 0x25000000,
            EntityAddress: 0x25000028,
            FailureStage: null,
            Attempts: 1,
            NodesVisited: 3,
            ModuleRooted: true);
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: expectedRegion,
            addressByEntity: new Dictionary<int, Type10EntityPositionAddressResult>
            {
                [4242] = resolved,
            });
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [
                        new EntityRegionReadRequestItem(4242, RegionLength: 4),
                        new EntityRegionReadRequestItem(4243, RegionLength: 4),
                    ]),
                CancellationToken.None);

        // The batch succeeds; only the unresolved entity reports a failure.
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRegionsReadResult batch = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, batch.Status);
        Assert.HasCount(2, batch.Regions);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, batch.Regions[0].Status);
        CollectionAssert.AreEqual(expectedRegion, batch.Regions[0].RegionBytes);
        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, batch.Regions[1].Status);
        Assert.IsNull(batch.Regions[1].RegionBytes);
        Assert.AreEqual("entity-lookup", batch.Regions[1].FailureStage);
        // Only the resolved entity's region was read.
        Assert.HasCount(1, factory.Reader.RegionReads);
    }

    [TestMethod]
    public async Task EntityRegionsRead_InactivePhaseFailsWholeBatch()
    {
        // The pre-battle phase is global: if ANY resolve reports the
        // retryable ReplaySessionInactive, the whole batch reports it and no
        // region read fires (a frame cannot be half-timed).
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var inactive = new Type10EntityPositionAddressResult(
            Type10EntityPositionStatus.ReplaySessionInactive,
            RecordAddress: null,
            PageAddress: null,
            EntityAddress: null,
            FailureStage: "session-controller",
            Attempts: 1,
            NodesVisited: 0,
            ModuleRooted: false);
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242),
            regionBytes: [1, 2, 3, 4],
            addressByEntity: new Dictionary<int, Type10EntityPositionAddressResult>
            {
                [4242] = inactive,
            });
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [
                        new EntityRegionReadRequestItem(4242, RegionLength: 4),
                        new EntityRegionReadRequestItem(4243, RegionLength: 4),
                    ]),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRegionsReadResult batch = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.ReplaySessionInactive, batch.Status);
        // Every requested entity reports the phase, none carry bytes.
        Assert.HasCount(2, batch.Regions);
        Assert.AreEqual(Type10EntityPositionStatus.ReplaySessionInactive, batch.Regions[0].Status);
        Assert.AreEqual("pre-battle-inactive", batch.Regions[0].FailureStage);
        Assert.AreEqual(Type10EntityPositionStatus.ReplaySessionInactive, batch.Regions[1].Status);
        Assert.IsNull(batch.Regions[0].RegionBytes);
        Assert.IsNull(batch.Regions[1].RegionBytes);
        // No region read ever fired -> no read-pass window measurement.
        Assert.IsEmpty(factory.Reader.RegionReads);
        Assert.IsNull(batch.Measurement);
    }

    [TestMethod]
    public async Task EntityRegionsRead_InvalidRequestFailsClosedBeforeGate()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityRegionsReadResult> empty = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest([]),
                CancellationToken.None);
        Assert.IsFalse(empty.IsSuccess);
        Assert.AreEqual("discover.entity_regions.invalid_request", empty.Error?.Code);

        OperationResult<EntityRegionsReadResult> tooMany = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    Enumerable.Range(0, EntityRegionsReadRequest.MaxEntities + 1)
                        .Select(i => new EntityRegionReadRequestItem(i, RegionLength: 8))
                        .ToList()),
                CancellationToken.None);
        Assert.IsFalse(tooMany.IsSuccess);
        Assert.AreEqual("discover.entity_regions.invalid_request", tooMany.Error?.Code);

        OperationResult<EntityRegionsReadResult> zeroLength = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [new EntityRegionReadRequestItem(4242, RegionLength: 0)]),
                CancellationToken.None);
        Assert.IsFalse(zeroLength.IsSuccess);
        Assert.AreEqual("discover.entity_regions.invalid_request", zeroLength.Error?.Code);

        OperationResult<EntityRegionsReadResult> badAnchor = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [new EntityRegionReadRequestItem(
                        4242,
                        RegionLength: 8,
                        (EntityRecordRegionAnchor)99)]),
                CancellationToken.None);
        Assert.IsFalse(badAnchor.IsSuccess);
        Assert.AreEqual("discover.entity_regions.invalid_request", badAnchor.Error?.Code);

        OperationResult<EntityRegionsReadResult> tooManyBytes = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    Enumerable.Range(0, EntityRegionsReadRequest.MaxEntities)
                        .Select(i => new EntityRegionReadRequestItem(i, RegionLength: 1025))
                        .ToList()),
                CancellationToken.None);
        Assert.IsFalse(tooManyBytes.IsSuccess);
        Assert.AreEqual("discover.entity_regions.invalid_request", tooManyBytes.Error?.Code);

        // No validation failure ever creates a memory reader.
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionsRead_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [new EntityRegionReadRequestItem(4242, RegionLength: 8)]),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EntityRegionsRead_UnsupportedBuildFailsWholeBatch()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<EntityRegionsReadResult> result = await coordinator
            .ReadEntityRegionsAsync(
                new EntityRegionsReadRequest(
                    [
                        new EntityRegionReadRequestItem(4242, RegionLength: 8),
                        new EntityRegionReadRequestItem(4243, RegionLength: 8),
                    ]),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        EntityRegionsReadResult batch = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, batch.Status);
        Assert.HasCount(2, batch.Regions);
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, batch.Regions[0].Status);
        Assert.AreEqual("build-identity", batch.Regions[0].FailureStage);
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, batch.Regions[1].Status);
        Assert.IsNull(batch.Regions[0].RegionBytes);
        Assert.AreEqual(0, factory.CreateCount);
        // No reads happened -> no read-pass window measurement.
        Assert.IsNull(batch.Measurement);
    }

    [TestMethod]
    public async Task EnumerateEntities_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);

        OperationResult<EntityRosterReadResult> result = await coordinator
            .EnumerateEntitiesAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
        Assert.AreEqual(0, scan.ScanCount);
    }

    [TestMethod]
    public async Task EnumerateEntities_UnsupportedBuildFailsClosed()
    {
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<EntityRosterReadResult> result = await coordinator
            .EnumerateEntitiesAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        EntityRosterReadResult roster = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, roster.Status);
        Assert.AreEqual("build-identity", roster.FailureStage);
        Assert.AreEqual(0, roster.CandidatesSeen);
        Assert.IsEmpty(roster.EntityIds);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task EnumerateEntities_ExactBuildReturnsAvatarIdsOnly()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        factory.Reader.RosterResult = new EntityRosterResult(
            Type10EntityPositionStatus.Resolved,
            FailureStage: null,
            ModuleRooted: true,
            NodesVisited: 12,
            CandidatesSeen: 18,
            FilteredOut: 4,
            Entities:
            [
                new EntityRosterEntry(3760578, 0x25000028),
                new EntityRosterEntry(3760577, 0x25000100),
                new EntityRosterEntry(3760579, 0x25000200),
            ],
            TraversalLimited: false);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRosterReadResult> result = await coordinator
            .EnumerateEntitiesAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRosterReadResult roster = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, roster.Status);
        Assert.AreEqual(18, roster.CandidatesSeen);
        Assert.AreEqual(4, roster.FilteredOut);
        Assert.IsTrue(roster.ModuleRooted);
        Assert.IsFalse(roster.TraversalLimited);
        // Ids only — the resolved addresses never leave the coordinator.
        int[] ids = roster.EntityIds.Order().ToArray();
        int[] expected = [3760577, 3760578, 3760579];
        CollectionAssert.AreEqual(expected, ids);
        // The enumeration ran through the one guarded reader under the lease.
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(layout, factory.Reader.Layout);
    }

    [TestMethod]
    public async Task EnumerateEntities_PreLoginPhaseSurfacesRetryableStatus()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        factory.Reader.RosterResult = new EntityRosterResult(
            Type10EntityPositionStatus.ReplaySessionInactive,
            "session-controller-vtable",
            ModuleRooted: true,
            NodesVisited: 0,
            CandidatesSeen: 0,
            FilteredOut: 0,
            Entities: null,
            TraversalLimited: false);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<EntityRosterReadResult> result = await coordinator
            .EnumerateEntitiesAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        EntityRosterReadResult roster = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.ReplaySessionInactive, roster.Status);
        Assert.AreEqual("session-controller-vtable", roster.FailureStage);
        Assert.IsEmpty(roster.EntityIds);
    }

    [TestMethod]
    public async Task LiveFrame_ComposesRosterBatchAndCamera()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        Type10CameraPoseLayout cameraLayout = Type10CameraPoseLayout.WotBlitz1119010;
        // Ring-record regions for two roster entities: position at +0x10,
        // hull yaw at +0x2C (the L2 chain field).
        byte[] ringA = CreateRingRegion(12.5f, 3.25f, -44.75f, yaw: 0.5f);
        byte[] ringB = CreateRingRegion(-8f, 2f, 30f, yaw: -1.25f);
        Dictionary<long, byte[]> pages = CreateCameraChainPages(cameraLayout);
        pages[0x25000038] = ringA;
        pages[0x25000100] = ringB;
        var factory = new ScriptedCameraReaderFactory(pages);
        factory.Reader.RosterResult = new EntityRosterResult(
            Type10EntityPositionStatus.Resolved,
            FailureStage: null,
            ModuleRooted: true,
            NodesVisited: 12,
            CandidatesSeen: 18,
            FilteredOut: 4,
            Entities:
            [
                new EntityRosterEntry(3760578, 0x25000028),
                new EntityRosterEntry(3760577, 0x25000100),
            ],
            TraversalLimited: false);
        factory.Reader.AddressResult = new Type10EntityPositionAddressResult(
            Type10EntityPositionStatus.Resolved,
            RecordAddress: 0x25000038,
            PageAddress: 0x25000000,
            EntityAddress: 0x25000028,
            FailureStage: null,
            Attempts: 1,
            NodesVisited: 0,
            ModuleRooted: true);
        // Second entity resolves to the second ring region.
        factory.Reader.AddressByEntity = new Dictionary<int, Type10EntityPositionAddressResult>
        {
            [3760578] = new(
                Type10EntityPositionStatus.Resolved,
                RecordAddress: 0x25000038,
                PageAddress: 0x25000000,
                EntityAddress: 0x25000028,
                FailureStage: null,
                Attempts: 1,
                NodesVisited: 0,
                ModuleRooted: true),
            [3760577] = new(
                Type10EntityPositionStatus.Resolved,
                RecordAddress: 0x25000100,
                PageAddress: 0x25000000,
                EntityAddress: 0x25000100,
                FailureStage: null,
                Attempts: 1,
                NodesVisited: 0,
                ModuleRooted: true),
        };
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<LiveFrameReadResult> result = await coordinator
            .ReadLiveFrameAsync(
                new LiveFrameReadRequest(),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        LiveFrameReadResult frame = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, frame.Status);
        Assert.IsNull(frame.ReplayTimeSeconds);
        Assert.IsFalse(frame.SameDecodedClockProven);
        Assert.AreEqual(18, frame.RosterCandidatesSeen);
        Assert.AreEqual(4, frame.RosterFilteredOut);
        Assert.HasCount(2, frame.Tanks);
        // Tank A: position + yaw decoded from its ring region, hp honest null.
        LiveFrameTankState first = frame.Tanks[0];
        Assert.AreEqual(3760578, first.EntityId);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, first.Status);
        Assert.AreEqual(12.5f, first.X);
        Assert.AreEqual(3.25f, first.Y);
        Assert.AreEqual(-44.75f, first.Z);
        Assert.AreEqual(0.5f, first.YawRadians);
        Assert.IsNull(first.Hp);
        Assert.IsTrue(first.ModuleRooted);
        // Tank B: its own ring region.
        LiveFrameTankState second = frame.Tanks[1];
        Assert.AreEqual(3760577, second.EntityId);
        Assert.AreEqual(-8f, second.X);
        Assert.AreEqual(-1.25f, second.YawRadians);
        // Camera pose resolved through the CAM-001 chain.
        Assert.IsNotNull(frame.Camera);
        Assert.AreEqual(CameraPoseStatus.Resolved, frame.Camera!.Status);
        Assert.AreEqual(10.5f, frame.Camera.X);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(1, scan.ScanCount);
    }

    [TestMethod]
    public async Task LiveFrame_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);

        OperationResult<LiveFrameReadResult> result = await coordinator
            .ReadLiveFrameAsync(new LiveFrameReadRequest(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
        Assert.AreEqual(0, scan.ScanCount);
    }

    [TestMethod]
    public async Task LiveFrame_UnsupportedBuildFailsClosed()
    {
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<LiveFrameReadResult> result = await coordinator
            .ReadLiveFrameAsync(new LiveFrameReadRequest(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        LiveFrameReadResult frame = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedBuild, frame.Status);
        Assert.AreEqual("build-identity", frame.FailureStage);
        Assert.IsEmpty(frame.Tanks);
        Assert.IsNull(frame.Camera);
        Assert.AreEqual(0, factory.CreateCount);
    }

    [TestMethod]
    public async Task LiveFrame_RosterInactiveFailsWholeFrame()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        factory.Reader.RosterResult = new EntityRosterResult(
            Type10EntityPositionStatus.ReplaySessionInactive,
            "session-controller-vtable",
            ModuleRooted: true,
            NodesVisited: 0,
            CandidatesSeen: 0,
            FilteredOut: 0,
            Entities: null,
            TraversalLimited: false);
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<LiveFrameReadResult> result = await coordinator
            .ReadLiveFrameAsync(new LiveFrameReadRequest(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        LiveFrameReadResult frame = result.Value!;
        Assert.AreEqual(Type10EntityPositionStatus.ReplaySessionInactive, frame.Status);
        Assert.AreEqual("session-controller-vtable", frame.FailureStage);
        Assert.IsEmpty(frame.Tanks);
        Assert.IsNull(frame.Camera);
    }

    [TestMethod]
    public async Task CameraPoseRead_MissingOfflineGateNeverCreatesMemoryReader()
    {
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);

        OperationResult<CameraPoseReadResult> result = await coordinator
            .ReadCameraPoseAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
        Assert.AreEqual(0, scan.ScanCount);
    }

    [TestMethod]
    public async Task CameraPoseRead_UnsupportedBuildFailsClosed()
    {
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        OperationResult<CameraPoseReadResult> result = await coordinator
            .ReadCameraPoseAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.camera_pose.unsupported_build", result.Error?.Code);
        Assert.AreEqual(0, factory.CreateCount);
        Assert.AreEqual(0, scan.ScanCount);
    }

    [TestMethod]
    public async Task CameraPoseRead_ExactBuildResolvesPoseWithIdentityGates()
    {
        Type10CameraPoseLayout layout = Type10CameraPoseLayout.WotBlitz1119010;
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages(layout));
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<CameraPoseReadResult> result = await coordinator
            .ReadCameraPoseAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        CameraPoseReadResult pose = result.Value!;
        Assert.AreEqual(CameraPoseStatus.Resolved, pose.Status);
        Assert.AreEqual(10.5f, pose.X);
        Assert.AreEqual(20.25f, pose.Y);
        Assert.AreEqual(-3.5f, pose.Z);
        Assert.AreEqual(0.7f, pose.YawRadians, 0.001f);
        Assert.AreEqual(-0.2f, pose.PitchRadians);
        Assert.AreEqual(1f, pose.Basis[0]);
        Assert.IsTrue(pose.AvatarIdentityVerified);
        Assert.IsTrue(pose.CameraIdentityVerified);
        Assert.IsTrue(pose.CameraStateIdentityVerified);
        Assert.IsTrue(pose.ConsistentDoubleRead);
        Assert.IsTrue(pose.ModuleRooted);
        Assert.AreEqual(0x25001000, pose.AvatarAddress);
        Assert.AreEqual(0x25003000, pose.CameraAddress);
        Assert.AreEqual(0x25004000, pose.CameraStateAddress);
        // The pose region is read twice under the lease (double-read).
        Assert.HasCount(2, factory.Reader.Reads.Where(read => read.Length == 0x78));
        Assert.AreEqual(1, factory.CreateCount);
    }

    [TestMethod]
    public async Task CameraPoseRead_AnchorNotFoundReturnsStatus()
    {
        var factory = new ScriptedCameraReaderFactory(CreateCameraChainPages());
        var scan = new FakeScanDiscoverer(new MemoryScanResult(
            DateTimeOffset.UnixEpoch,
            BaseAddress: 0x10000000,
            RegionsScanned: 1,
            BytesScanned: 4096,
            Candidates: [],
            TotalMatchesBeforeTruncation: 0));
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);
        Type10CameraPoseLayout layout = Type10CameraPoseLayout.WotBlitz1119010;
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<CameraPoseReadResult> result = await coordinator
            .ReadCameraPoseAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(CameraPoseStatus.AnchorNotFound, result.Value?.Status);
        Assert.AreEqual("avatar-vftable-anchor", result.Value?.FailureStage);
        Assert.IsFalse(result.Value?.AvatarIdentityVerified);
        Assert.AreEqual(0, factory.CreateCount);
        // Both avatar-vftable variants are probed before giving up.
        Assert.AreEqual(2, scan.ScanCount);
    }

    [TestMethod]
    public async Task CameraPoseRead_CameraVftableMismatchFailsClosed()
    {
        Type10CameraPoseLayout layout = Type10CameraPoseLayout.WotBlitz1119010;
        IReadOnlyDictionary<long, byte[]> pages = CreateCameraChainPages(layout);
        // Corrupt the camera-controller vftable dword (hop 2 identity gate).
        pages = pages.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == 0x25003000
                ? BitConverter.GetBytes(0x19999999u)
                : pair.Value);
        var factory = new ScriptedCameraReaderFactory(pages);
        var scan = new FakeScanDiscoverer(CreateAvatarScanResult());
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            scanDiscoverer: scan);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });

        OperationResult<CameraPoseReadResult> result = await coordinator
            .ReadCameraPoseAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(CameraPoseStatus.ChainBroken, result.Value?.Status);
        Assert.AreEqual("camera-vftable", result.Value?.FailureStage);
        Assert.IsTrue(result.Value?.AvatarIdentityVerified);
        Assert.IsFalse(result.Value?.CameraIdentityVerified);
        Assert.IsFalse(result.Value?.CameraStateIdentityVerified);
        Assert.IsFalse(result.Value?.ConsistentDoubleRead);
    }

    private static (GameSessionCoordinator Coordinator, TrackingEntityPositionReaderFactory Factory)
        CreateVerifiedExactBuildCoordinator(
            Type10EntityPositionResult result,
            IReplayClockSource replayClockSource)
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        ContentHash executableHash = new(layout.ExecutableSha256);
        var factory = new TrackingEntityPositionReaderFactory(result);
        (GameSessionCoordinator coordinator, _) = CreateCoordinator(
            memoryReaderFactory: factory,
            replayClockSource: replayClockSource);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });
        return (coordinator, factory);
    }

    [TestMethod]
    public async Task EntityPositionRead_NullSessionIdNeverClaimsSameClock()
    {
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Estimated, TimeSpan.FromMilliseconds(500)));
        (GameSessionCoordinator coordinator, _) = CreateVerifiedExactBuildCoordinator(
            CreateResolvedEntityPosition(4242), clock);

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
        Assert.IsNull(clock.LastRequestedSessionId);
    }

    [TestMethod]
    public async Task EntityPositionRead_MissingSegmentsDoesNotClaimSameClock()
    {
        var clock = new StubReplayClockSource(); // default: clock.anchor.missing
        (GameSessionCoordinator coordinator, _) = CreateVerifiedExactBuildCoordinator(
            CreateResolvedEntityPosition(4242), clock);
        BattleSessionId sessionId = BattleSessionId.New();

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242, sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
        Assert.AreEqual(sessionId, clock.LastRequestedSessionId);
    }

    [TestMethod]
    public async Task EntityPositionRead_StaleClockDoesNotClaimSameClock()
    {
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Stale, TimeSpan.FromMilliseconds(500)));
        (GameSessionCoordinator coordinator, _) = CreateVerifiedExactBuildCoordinator(
            CreateResolvedEntityPosition(4242), clock);

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242, sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
    }

    [TestMethod]
    public async Task EntityPositionRead_UncertaintyBeyondLimitDoesNotClaimSameClock()
    {
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Estimated, TimeSpan.FromSeconds(5)));
        (GameSessionCoordinator coordinator, _) = CreateVerifiedExactBuildCoordinator(
            CreateResolvedEntityPosition(4242), clock);

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242, sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.Value?.SameDecodedClockProven);
    }

    [TestMethod]
    public async Task EntityPositionRead_WithinUncertaintyLimitClaimsSameClock()
    {
        BattleSessionId sessionId = BattleSessionId.New();
        var clock = new StubReplayClockSource(
            CreateSnapshotResult(sessionId, ReplayClockQuality.Estimated, TimeSpan.FromMilliseconds(500)));
        (GameSessionCoordinator coordinator, _) = CreateVerifiedExactBuildCoordinator(
            CreateResolvedEntityPosition(4242), clock);

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242, sessionId),
                CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Value?.Status);
        Assert.IsTrue(result.Value?.SameDecodedClockProven);
        Assert.AreEqual(sessionId, clock.LastRequestedSessionId);
    }

    [TestMethod]
    public async Task EntityPositionRead_RevocationDuringReadDiscardsResult()
    {
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        var factory = new TrackingEntityPositionReaderFactory(
            CreateResolvedEntityPosition(4242));
        var (coordinator, _) = CreateCoordinator(memoryReaderFactory: factory);
        ContentHash executableHash = new(layout.ExecutableSha256);
        coordinator.RecordManagedLaunch(CreateManagedLaunch(
            productVersion: layout.GameVersion,
            executableSha256: executableHash));
        coordinator.ApplyEvidence(CreateValidEvidence() with
        {
            Process = CreateValidProcess(layout.GameVersion, executableHash),
        });
        factory.Reader.BeforeReturn = () => coordinator.ApplyEvidence(
            CreateValidEvidence() with
            {
                Process = CreateValidProcess(layout.GameVersion, executableHash),
                Lifecycle = CreateValidLifecycle() with
                {
                    State = ReplayLifecycleState.OfflineReplayStopped,
                    SourceSequence = 12,
                    SourceByteOffset = 102,
                },
            });

        OperationResult<EntityPositionReadResult> result = await coordinator
            .ReadEntityPositionAsync(
                new EntityPositionReadRequest(4242),
                CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.gate_not_satisfied", result.Error?.Code);
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

    [TestMethod]
    public async Task ChainedFields_AreExcludedFromObservationReads()
    {
        // G0 publication contract (2026-08-09): a chained field
        // (playerPositionX, Verified, offsets value 0) must NEVER be read as
        // moduleBase + 0 by the legacy observation path — the position chain
        // lives in the resolver layout, not the table, and the runtime
        // computes moduleBase + offset. A non-chained Verified field with a
        // real offset must still be read.
        var readerFactory = new RecordingObservationReaderFactory();
        var (coordinator, _) = CreateCoordinator(
            memoryReaderFactory: readerFactory,
            offsetTableReader: new FixedOffsetTableReader(CreateObservationFixtureTable()));
        coordinator.RecordManagedLaunch(CreateManagedLaunch());
        coordinator.ApplyEvidence(CreateValidEvidence());

        GameMemoryObservation observation =
            await coordinator.ObserveAsync(CancellationToken.None);

        nint moduleBase = (nint)0x10000000;
        Assert.AreEqual(
            GameMemoryObservationAvailability.Available,
            observation.Availability);
        // The non-chained Verified field (replayTime at +0x1000) is read.
        CollectionAssert.Contains(
            readerFactory.Reader.Addresses,
            moduleBase + 0x1000);
        // The chained field (offset 0) is never read as moduleBase + 0.
        CollectionAssert.DoesNotContain(
            readerFactory.Reader.Addresses,
            moduleBase,
            "a chained field with offsets=0 must not be read as moduleBase + 0");
        // Chained position stays null; the control field is populated.
        Assert.IsNull(observation.PlayerPositionX);
        Assert.IsNotNull(observation.ReplayTimeSeconds);
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
            IReplayClockSource? replayClockSource = null,
            IGameProcessModuleBaseAddressResolver? moduleBaseAddressResolver = null,
            IOffsetTableReader? offsetTableReader = null,
            IBlitzReplayLifecycleFeed? lifecycleFeed = null,
            IInstructionSnapshotRunner? instructionSnapshotRunner = null,
            IMemoryScanDiscoverer? scanDiscoverer = null,
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
            replayClockSource ?? new StubReplayClockSource(),
            threadResumePlatform ?? new StubThreadResumePlatform(),
            processIdentityObserver ?? new StubProcessIdentityObserver(),
            memoryReaderFactory ?? new StubMemoryReaderFactory(),
            moduleBaseAddressResolver ?? new FixedModuleBaseResolver((nint)0x10000000),
            offsetTableReader ?? new StubOffsetTableReader(),
            scanDiscoverer ?? new MemoryScanDiscoverer(timeProvider, NullLogger<MemoryScanDiscoverer>.Instance),
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

    private static GameProcessEvidence CreateValidProcess(
        string productVersion = "11.18.0.7",
        ContentHash? executableSha256 = null) =>
        new(
            ProcessId: 1234,
            ProcessStartIdentity: 42,
            IsAlive: true,
            ObservedCanonicalExecutablePath: @"C:\Games\wotblitz.exe",
            ObservedProductVersion: productVersion,
            ObservedExecutableSha256: executableSha256 ?? new ContentHash(new string('a', 64)),
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
        IReadOnlyList<LifecycleSourceCursor>? sourceBaselines = null,
        string productVersion = "11.18.0.7",
        ContentHash? executableSha256 = null) =>
        new(
            LaunchCorrelation,
            new InstalledGameIdentity(
                ExecutablePath: @"C:\Games\wotblitz.exe",
                ProductVersion: productVersion,
                ExecutableSha256: executableSha256 ?? new ContentHash(new string('a', 64)),
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

    private static Type10EntityPositionResult CreateResolvedEntityPosition(int entityId) => new(
        Type10EntityPositionStatus.Resolved,
        entityId,
        12.5f,
        3.25f,
        -44.75f,
        "primary",
        null,
        Attempts: 1,
        NodesVisited: 3,
        ModuleRooted: true,
        EntityIdentityRevalidated: true,
        ConsistentDoubleRead: true,
        HardwareAtomicReadProven: false);

    private sealed class TrackingEntityPositionReaderFactory(
        Type10EntityPositionResult result,
        Type10EntityPositionAddressResult? addressResult = null,
        byte[]? regionBytes = null,
        uint? tankRecordAddress = null,
        IReadOnlyDictionary<int, Type10EntityPositionAddressResult>? addressByEntity = null) : IGuardedMemoryReaderFactory
    {
        public int CreateCount { get; private set; }
        public AuthorizedMemoryObservation? Observation { get; private set; }
        public TrackingEntityPositionReader Reader { get; } =
            new(result, addressResult, regionBytes, tankRecordAddress, addressByEntity);

        public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
            AuthorizedMemoryObservation observation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            Observation = observation;
            return ValueTask.FromResult(OperationResult.Success<IAuthorizedMemoryReader>(Reader));
        }
    }

    private sealed class TrackingEntityPositionReader(
        Type10EntityPositionResult result,
        Type10EntityPositionAddressResult? addressResult = null,
        byte[]? regionBytes = null,
        uint? tankRecordAddress = null,
        IReadOnlyDictionary<int, Type10EntityPositionAddressResult>? addressByEntity = null)
        : IAuthorizedMemoryReader
    {
        public nint ModuleBase { get; private set; }
        public int EntityId { get; private set; }
        public Type10EntityPositionLayout? Layout { get; private set; }
        public Action? BeforeReturn { get; set; }
        public List<(nint Address, int Length)> RegionReads { get; } = [];

        // The L0 seam's tank-record anchor probes [entity + 0x3C]; the
        // entity base comes from the resolved address result.
        private nint ProbeAddress =>
            (nint)((addressResult?.EntityAddress ?? 0x25000028) + 0x3C);

        public ValueTask<OperationResult<byte[]>> ReadAsync(
            nint address,
            int length,
            CancellationToken cancellationToken)
        {
            RegionReads.Add((address, length));
            if (tankRecordAddress is uint tankRecord &&
                length == sizeof(uint) &&
                address == ProbeAddress)
            {
                // The L0 seam's tank-record anchor dereferences
                // [entity + 0x3C] under the same lease: return the tank-record
                // pointer for the 4-byte probe at entity+0x3C.
                return ValueTask.FromResult(OperationResult.Success(
                    BitConverter.GetBytes(tankRecord)));
            }
            if (regionBytes is null)
            {
                throw new NotSupportedException();
            }
            return ValueTask.FromResult(OperationResult.Success(regionBytes));
        }

        public ValueTask<OperationResult<IReadOnlyList<MemoryReadItem>>> ReadBatchAsync(
            IReadOnlyList<nint> addresses,
            int length,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<Type10EntityPositionResult>> ResolveEntityPositionAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleBase = moduleBase;
            EntityId = entityId;
            Layout = layout;
            BeforeReturn?.Invoke();
            return ValueTask.FromResult(OperationResult.Success(result));
        }

        public ValueTask<OperationResult<Type10EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleBase = moduleBase;
            EntityId = entityId;
            Layout = layout;
            BeforeReturn?.Invoke();
            if (addressByEntity is not null)
            {
                return ValueTask.FromResult(OperationResult.Success(
                    addressByEntity.TryGetValue(entityId, out Type10EntityPositionAddressResult? perEntity)
                        ? perEntity
                        : new Type10EntityPositionAddressResult(
                            Type10EntityPositionStatus.EntityNotFound,
                            RecordAddress: null,
                            PageAddress: null,
                            EntityAddress: null,
                            FailureStage: "entity-lookup",
                            Attempts: 3,
                            NodesVisited: 2,
                            ModuleRooted: true)));
            }
            return ValueTask.FromResult(OperationResult.Success(
                addressResult ?? new Type10EntityPositionAddressResult(
                    Type10EntityPositionStatus.Resolved,
                    RecordAddress: 0x25000038,
                    PageAddress: 0x25000000,
                    EntityAddress: 0x25000028,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 0,
                    ModuleRooted: true)));
        }

        public EntityRosterResult? RosterResult { get; set; }

        public ValueTask<OperationResult<EntityRosterResult>> EnumerateEntitiesAsync(
            nint moduleBase,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleBase = moduleBase;
            Layout = layout;
            return ValueTask.FromResult(OperationResult.Success(
                RosterResult ?? new EntityRosterResult(
                    Type10EntityPositionStatus.Resolved,
                    FailureStage: null,
                    ModuleRooted: true,
                    NodesVisited: 0,
                    CandidatesSeen: 0,
                    FilteredOut: 0,
                    Entities: [],
                    TraversalLimited: false)));
        }
    }

    private sealed class FakeScanDiscoverer(MemoryScanResult result) : IMemoryScanDiscoverer
    {
        public int ScanCount { get; private set; }
        public MemoryScanRequest? LastRequest { get; private set; }

        public OperationResult<MemoryScanResult> Scan(
            AuthorizedMemoryObservation observation,
            long baseAddress,
            MemoryScanRequest request,
            CancellationToken cancellationToken,
            string scanKind = "value")
        {
            ScanCount++;
            LastRequest = request;
            return OperationResult.Success(result);
        }

        public OperationResult<MemoryScanResult> ScanNeighborhood(
            AuthorizedMemoryObservation observation,
            long baseAddress,
            MemoryNeighborhoodRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public OperationResult<MemoryPointerChainResult> ResolvePointerChain(
            AuthorizedMemoryObservation observation,
            long baseAddress,
            MemoryPointerChainRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedCameraReaderFactory(
        IReadOnlyDictionary<long, byte[]> pages) : IGuardedMemoryReaderFactory
    {
        public int CreateCount { get; private set; }
        public AuthorizedMemoryObservation? Observation { get; private set; }
        public ScriptedCameraReader Reader { get; } = new(pages);

        public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
            AuthorizedMemoryObservation observation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            Observation = observation;
            return ValueTask.FromResult(OperationResult.Success<IAuthorizedMemoryReader>(Reader));
        }
    }

    private sealed class ScriptedCameraReader(
        IReadOnlyDictionary<long, byte[]> pages) : IAuthorizedMemoryReader
    {
        public List<(long Address, int Length)> Reads { get; } = [];

        public ValueTask<OperationResult<byte[]>> ReadAsync(
            nint address,
            int length,
            CancellationToken cancellationToken)
        {
            long key = address.ToInt64();
            Reads.Add((key, length));
            if (pages.TryGetValue(key, out byte[]? page) && page.Length >= length)
            {
                return ValueTask.FromResult(OperationResult.Success(
                    page.AsSpan(0, length).ToArray()));
            }

            return ValueTask.FromResult(OperationResult.Failure<byte[]>(
                new ApplicationError(
                    "test.read_miss",
                    "Scripted read miss at 0x"
                        + key.ToString("X", CultureInfo.InvariantCulture))));
        }

        public ValueTask<OperationResult<IReadOnlyList<MemoryReadItem>>> ReadBatchAsync(
            IReadOnlyList<nint> addresses,
            int length,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<Type10EntityPositionResult>> ResolveEntityPositionAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public EntityRosterResult? RosterResult { get; set; }
        public Type10EntityPositionAddressResult? AddressResult { get; set; }
        public Dictionary<int, Type10EntityPositionAddressResult>? AddressByEntity { get; set; }

        public ValueTask<OperationResult<Type10EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AddressByEntity is not null)
            {
                return ValueTask.FromResult(OperationResult.Success(
                    AddressByEntity.TryGetValue(entityId, out Type10EntityPositionAddressResult? perEntity)
                        ? perEntity
                        : new Type10EntityPositionAddressResult(
                            Type10EntityPositionStatus.EntityNotFound,
                            RecordAddress: null,
                            PageAddress: null,
                            EntityAddress: null,
                            FailureStage: "entity-lookup",
                            Attempts: 3,
                            NodesVisited: 2,
                            ModuleRooted: true)));
            }

            return ValueTask.FromResult(OperationResult.Success(
                AddressResult ?? new Type10EntityPositionAddressResult(
                    Type10EntityPositionStatus.Resolved,
                    RecordAddress: 0x25000038,
                    PageAddress: 0x25000000,
                    EntityAddress: 0x25000028,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 0,
                    ModuleRooted: true)));
        }

        public ValueTask<OperationResult<EntityRosterResult>> EnumerateEntitiesAsync(
            nint moduleBase,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(OperationResult.Success(
                RosterResult ?? new EntityRosterResult(
                    Type10EntityPositionStatus.Resolved,
                    FailureStage: null,
                    ModuleRooted: true,
                    NodesVisited: 0,
                    CandidatesSeen: 0,
                    FilteredOut: 0,
                    Entities: [],
                    TraversalLimited: false)));
        }
    }

    private static MemoryScanResult CreateAvatarScanResult() => new(
        DateTimeOffset.UnixEpoch,
        BaseAddress: 0x10000000,
        RegionsScanned: 1,
        BytesScanned: 4096,
        Candidates:
        [
            new MemoryScanCandidate(
                AbsoluteAddress: 0x25001000,
                BaseDisplacement: 0,
                ObservedValue: BitConverter.GetBytes(0x13277e8cu),
                ValueSummary: "avatar-vftable"),
        ],
        TotalMatchesBeforeTruncation: 1);

    private static Dictionary<long, byte[]> CreateCameraChainPages(
        Type10CameraPoseLayout? layout = null)
    {
        Type10CameraPoseLayout pinned = layout ?? Type10CameraPoseLayout.WotBlitz1119010;
        const uint moduleBase = 0x10000000u;
        return new Dictionary<long, byte[]>
        {
            // [avatar + 0x154] -> battle resources
            [0x25001154] = BitConverter.GetBytes(0x25002000u),
            // [br + 0x2C] -> camera controller
            [0x2500202C] = BitConverter.GetBytes(0x25003000u),
            // camera controller vftable (replay variant)
            [0x25003000] = BitConverter.GetBytes(moduleBase + pinned.CameraReplayVftableRva),
            // [camera + 0x28] -> GameCamera
            [0x25003028] = BitConverter.GetBytes(0x25004000u),
            // GameCamera vftable
            [0x25004000] = BitConverter.GetBytes(moduleBase + pinned.CameraStateVftableRva),
            // pose region [GameCamera + 0x38, 0x78)
            [0x25004038] = CreatePoseRegion(),
        };
    }

    private static byte[] CreateRingRegion(float x, float y, float z, float yaw)
    {
        byte[] region = new byte[0x40];
        BitConverter.GetBytes(x).CopyTo(region, RingRecordRegion.PositionOffset);
        BitConverter.GetBytes(y).CopyTo(region, RingRecordRegion.PositionOffset + 4);
        BitConverter.GetBytes(z).CopyTo(region, RingRecordRegion.PositionOffset + 8);
        BitConverter.GetBytes(yaw).CopyTo(region, RingRecordRegion.YawOffset);
        return region;
    }

    private static byte[] CreatePoseRegion()
    {
        byte[] region = new byte[0x78];
        BitConverter.GetBytes(10.5f).CopyTo(region, 0x00);
        BitConverter.GetBytes(20.25f).CopyTo(region, 0x04);
        BitConverter.GetBytes(-3.5f).CopyTo(region, 0x08);
        const float yaw = 0.7f;
        BitConverter.GetBytes((float)Math.Cos(yaw)).CopyTo(region, 0x18);
        BitConverter.GetBytes((float)Math.Sin(yaw)).CopyTo(region, 0x1C);
        BitConverter.GetBytes(-0.2f).CopyTo(region, 0x20);
        BitConverter.GetBytes(1f).CopyTo(region, 0x48);
        BitConverter.GetBytes(0f).CopyTo(region, 0x4C);
        BitConverter.GetBytes(0f).CopyTo(region, 0x50);
        BitConverter.GetBytes(0f).CopyTo(region, 0x54);
        BitConverter.GetBytes(1f).CopyTo(region, 0x58);
        BitConverter.GetBytes(0f).CopyTo(region, 0x5C);
        BitConverter.GetBytes(0f).CopyTo(region, 0x60);
        BitConverter.GetBytes(0f).CopyTo(region, 0x64);
        BitConverter.GetBytes(1f).CopyTo(region, 0x68);
        return region;
    }

    private static OffsetTable CreateObservationFixtureTable() =>
        new(
            SchemaVersion: 1,
            GameVersion: "11.18.0.7",
            ExecutableSha256: new string('a', 64),
            DiscoveredAtUtc: StartTime,
            Confidence: OffsetConfidence.High,
            Notes: "chained-field exclusion fixture (2026-08-09)",
            Fields:
            [
                new OffsetField(
                    "playerPositionX",
                    OffsetFieldType.FloatField,
                    Offset: 0,
                    OffsetFieldStatus.Verified,
                    OffsetConfidence.High,
                    Array.Empty<OffsetFieldEvidence>()),
                new OffsetField(
                    "replayTime",
                    OffsetFieldType.DoubleField,
                    Offset: 0x1000,
                    OffsetFieldStatus.Verified,
                    OffsetConfidence.High,
                    Array.Empty<OffsetFieldEvidence>()),
            ]);

    private sealed class FixedOffsetTableReader(OffsetTable table) : IOffsetTableReader
    {
        public OperationResult<OffsetTable?> Load(
            string gameVersion,
            string executableSha256,
            CancellationToken cancellationToken = default) =>
            OperationResult.Success<OffsetTable?>(table);
    }

    private sealed class RecordingObservationReaderFactory : IGuardedMemoryReaderFactory
    {
        public RecordingObservationReader Reader { get; } = new();

        public ValueTask<OperationResult<IAuthorizedMemoryReader>> CreateAsync(
            AuthorizedMemoryObservation observation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                OperationResult.Success<IAuthorizedMemoryReader>(Reader));
        }
    }

    private sealed class RecordingObservationReader : IAuthorizedMemoryReader
    {
        public List<nint> Addresses { get; } = [];

        public ValueTask<OperationResult<byte[]>> ReadAsync(
            nint address,
            int length,
            CancellationToken cancellationToken)
        {
            Addresses.Add(address);
            return ValueTask.FromResult(
                OperationResult.Success<byte[]>(new byte[length]));
        }

        public ValueTask<OperationResult<IReadOnlyList<MemoryReadItem>>> ReadBatchAsync(
            IReadOnlyList<nint> addresses,
            int length,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<Type10EntityPositionResult>> ResolveEntityPositionAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<Type10EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
            nint moduleBase,
            int entityId,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityRosterResult>> EnumerateEntitiesAsync(
            nint moduleBase,
            Type10EntityPositionLayout layout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReplayClockSource : IReplayClockSource
    {
        private readonly OperationResult<ReplayClockSnapshot> _snapshotResult;

        public StubReplayClockSource(OperationResult<ReplayClockSnapshot>? snapshotResult = null)
        {
            _snapshotResult = snapshotResult ?? OperationResult.Failure<ReplayClockSnapshot>(
                new ApplicationError("clock.anchor.missing", "No replay-clock anchor."));
        }

        public BattleSessionId? LastRequestedSessionId { get; private set; }
        public int CallCount { get; private set; }

        public ValueTask<OperationResult<ReplayClockSnapshot>> GetSnapshotAsync(
            BattleSessionId battleSessionId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestedSessionId = battleSessionId;
            return ValueTask.FromResult(_snapshotResult);
        }

        public ValueTask<OperationResult<ReplayClockSegment>> AddSegmentAsync(
            ReplayClockSegment segment,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<ReplayClockSnapshot>> MarkStaleAsync(
            BattleSessionId battleSessionId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static OperationResult<ReplayClockSnapshot> CreateSnapshotResult(
        BattleSessionId sessionId,
        ReplayClockQuality quality,
        TimeSpan uncertainty) =>
        OperationResult.Success(new ReplayClockSnapshot(
            sessionId,
            EstimatedReplayTime: TimeSpan.Zero,
            quality,
            TelemetrySourceKind.CaptureLog,
            Offset: TimeSpan.Zero,
            uncertainty,
            StartTime,
            LastAnchorUtc: StartTime));

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
