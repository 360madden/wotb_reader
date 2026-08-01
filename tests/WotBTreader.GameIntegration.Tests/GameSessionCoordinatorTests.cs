using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
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
        Assert.AreEqual("process.identity_changed", snapshot.ReasonCode);
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
            IGuardedMemoryReaderFactory? memoryReaderFactory = null,
            IOffsetTableReader? offsetTableReader = null,
            IBlitzReplayLifecycleFeed? lifecycleFeed = null)
    {
        var timeProvider = new ManualTimeProvider(StartTime);
        return (new GameSessionCoordinator(
            timeProvider,
            preparer ?? new StubPreparer(),
            artifactStager ?? new StubArtifactStager(),
            suspendedPlatform ?? new StubSuspendedPlatform(),
            correlationRegistrar ?? new StubCorrelationRegistrar(),
            threadResumePlatform ?? new StubThreadResumePlatform(),
            memoryReaderFactory ?? new StubMemoryReaderFactory(),
            offsetTableReader ?? new StubOffsetTableReader(),
            new MemoryScanDiscoverer(timeProvider, NullLogger<MemoryScanDiscoverer>.Instance),
            new MemoryScanEngine(timeProvider, NullLogger<MemoryScanEngine>.Instance),
            lifecycleFeed ?? new StubLifecycleFeed()), timeProvider);
    }

    private static (GameSessionCoordinator Coordinator, ManualTimeProvider TimeProvider)
        CreateVerifiedCoordinator()
    {
        var pair = CreateCoordinator();
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
            ReplayEvidenceSource.BlitzNativeLog,
            SourceIdentity: "synthetic-log-identity",
            SourceGeneration: 1,
            SourceSequence: 11,
            ProcessId: 1234,
            ProcessStartIdentity: 42,
            LaunchCorrelation);

    private static ManagedGameLaunchContext CreateManagedLaunch() =>
        new(
            LaunchCorrelation,
            new InstalledGameIdentity(
                ExecutablePath: @"C:\Games\wotblitz.exe",
                ProductVersion: "11.18.0.7",
                ExecutableSha256: new ContentHash(new string('a', 64)),
                ResourceRoot: @"C:\Games",
                DlcRoots: []),
            LifecycleSourceIdentity: "synthetic-log-identity",
            SourceGeneration: 1,
            SourceSequenceBaseline: 10);

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

    private sealed class FailingPreparer(string errorCode) : IManagedLaunchPreparer
    {
        public ValueTask<OperationResult<ManagedLaunchPreparation>> PrepareAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Failure<ManagedLaunchPreparation>(
                new ApplicationError(errorCode, "Test failure.", Retryable: false)));
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

    private sealed class StubLifecycleFeed : IBlitzReplayLifecycleFeed
    {
        public ValueTask<LifecycleFeedBaseline> CaptureBaselineAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedBaseline(
                0, 0, LifecycleFeedHealth.Healthy, []));

        public ValueTask<LifecycleFeedBaseline> CaptureReconciledBaselineAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedBaseline(
                0, 0, LifecycleFeedHealth.Healthy, []));

        public ValueTask<LifecycleFeedReadResult> ReadAfterAsync(
            long afterSequence,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LifecycleFeedReadResult(
                afterSequence, afterSequence, false, []));
    }
}
