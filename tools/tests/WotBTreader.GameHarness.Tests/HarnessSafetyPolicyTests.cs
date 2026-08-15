namespace WotBTreader.GameHarness.Tests;

/// <summary>
/// The safety policy is the only thing standing between the harness and an
/// unsafe game interaction, so every denial path is pinned by name. The gate
/// must fail closed: a case that is not positively proven safe is denied.
/// </summary>
[TestClass]
public sealed class HarnessSafetyPolicyTests
{
    [TestMethod]
    [DataRow("OfflineReplayVerified", "session.offline_replay_verified", true)]
    [DataRow("OfflineReplayVerified", null, false)]
    [DataRow("OfflineReplayVerified", "evidence.expired", false)]
    [DataRow("GamePresentUnverified", "session.offline_replay_verified", false)]
    [DataRow(null, null, false)]
    public void HarnessGateRequiresExactStateAndReason(
        string? state,
        string? reasonCode,
        bool expected) =>
        Assert.AreEqual(expected, HarnessGatePolicy.IsVerified(state, reasonCode));

    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private const string GameHash =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private const string OtherHash =
        "2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly Guid LaunchId = Guid.Parse("019fa000-0000-7000-8000-000000000001");

    [TestMethod]
    public void FullyVerifiedObservationWithoutArmingIsPermitted()
    {
        HarnessSafetyDecision decision = Evaluate(Observation());

        Assert.IsTrue(decision.Allowed, decision.StableCode);
        Assert.AreEqual("harness.safety_passed", decision.StableCode);
    }

    [TestMethod]
    public void MissingOrStoppedProcessIsDenied()
    {
        AssertDenied("harness.game_not_running", Evaluate(observation: null));
        AssertDenied(
            "harness.game_not_running",
            Evaluate(Observation() with { IsRunning = false }));
    }

    [TestMethod]
    public void ProcessWithoutAUsableWindowIsDenied()
    {
        AssertDenied("harness.invalid_window", Evaluate(Observation() with { ProcessId = 0 }));
        AssertDenied("harness.invalid_window", Evaluate(Observation() with { ProcessId = -1 }));
        AssertDenied("harness.invalid_window", Evaluate(Observation() with { WindowHandle = 0 }));
    }

    [TestMethod]
    public void ProcessIdentityMustMatchTheVerifiedInstallation()
    {
        AssertDenied(
            "harness.process_path_mismatch",
            Evaluate(Observation() with { ExecutablePath = @"C:\other\wotblitz.exe" }));
        AssertDenied(
            "harness.process_version_mismatch",
            Evaluate(Observation() with { ExecutableVersion = "11.19.0.0" }));
        AssertDenied(
            "harness.process_hash_mismatch",
            Evaluate(Observation() with { ExecutableSha256 = OtherHash }));
    }

    [TestMethod]
    public void TruncatedHashesNeverCompareEqual()
    {
        // A short value on both sides must not be accepted just because the two
        // strings match; only a full SHA-256 counts as identity evidence.
        GameProcessObservation observation = Observation() with { ExecutableSha256 = "abc" };

        AssertDenied(
            "harness.process_hash_mismatch",
            Evaluate(observation, identity: Identity() with { ExecutableSha256 = "abc" }));
    }

    [TestMethod]
    public void BackgroundWindowIsDenied() =>
        AssertDenied(
            "harness.window_not_foreground",
            Evaluate(Observation() with { IsForegroundWindow = false }));

    [TestMethod]
    [DataRow(ProcessIntegrityLevel.Unknown, ProcessIntegrityLevel.Medium)]
    [DataRow(ProcessIntegrityLevel.Medium, ProcessIntegrityLevel.Unknown)]
    [DataRow(ProcessIntegrityLevel.High, ProcessIntegrityLevel.Medium)]
    [DataRow(ProcessIntegrityLevel.Medium, ProcessIntegrityLevel.High)]
    public void UnknownOrUnequalIntegrityIsDenied(
        ProcessIntegrityLevel game,
        ProcessIntegrityLevel harness) =>
        AssertDenied(
            "harness.integrity_mismatch",
            Evaluate(Observation() with { GameIntegrity = game, HarnessIntegrity = harness }));

    [TestMethod]
    [DataRow(ReplayLifecycleState.Unknown)]
    [DataRow(ReplayLifecycleState.NotRunning)]
    [DataRow(ReplayLifecycleState.LaunchPending)]
    [DataRow(ReplayLifecycleState.OfflineReplayStopped)]
    [DataRow(ReplayLifecycleState.OnlineBattle)]
    [DataRow(ReplayLifecycleState.Ambiguous)]
    public void AnyStateOtherThanAVerifiedOfflineReplayIsDenied(ReplayLifecycleState state)
    {
        // OnlineBattle is the case that must never be automated, and Ambiguous
        // and Unknown must be treated exactly as unsafely as it is.
        AssertDenied(
            "harness.offline_replay_not_verified",
            Evaluate(Observation() with { Lifecycle = Lifecycle() with { State = state } }));
    }

    [TestMethod]
    public void LifecycleEvidenceFromAnUnapprovedSourceIsDenied() =>
        AssertDenied(
            "harness.untrusted_lifecycle_source",
            Evaluate(Observation() with
            {
                Lifecycle = Lifecycle() with { Source = "user-supplied" },
            }));

    [TestMethod]
    public void StaleOrFutureDatedEvidenceIsDenied()
    {
        AssertDenied(
            "harness.lifecycle_evidence_stale",
            Evaluate(Observation() with
            {
                Lifecycle = Lifecycle() with { ObservedAtUtc = Now.AddSeconds(-16) },
            }));

        // A timestamp ahead of the clock indicates a forged or misordered record.
        AssertDenied(
            "harness.lifecycle_evidence_stale",
            Evaluate(Observation() with
            {
                Lifecycle = Lifecycle() with { ObservedAtUtc = Now.AddSeconds(1) },
            }));
    }

    [TestMethod]
    public void EvidenceBelongingToAnotherProcessIsDenied() =>
        AssertDenied(
            "harness.lifecycle_process_mismatch",
            Evaluate(Observation() with
            {
                Lifecycle = Lifecycle() with { ProcessId = 4321 },
            }));

    [TestMethod]
    public void EvidenceNotCorrelatedWithTheRequestedLaunchIsDenied()
    {
        AssertDenied(
            "harness.launch_correlation_mismatch",
            Evaluate(Observation(), launchCorrelationId: Guid.Empty));
        AssertDenied(
            "harness.launch_correlation_mismatch",
            Evaluate(Observation(), launchCorrelationId: Guid.NewGuid()));
        AssertDenied(
            "harness.launch_correlation_mismatch",
            Evaluate(Observation() with
            {
                Lifecycle = Lifecycle() with { LaunchCorrelationId = null },
            }));
    }

    [TestMethod]
    public void InputIsDeniedWhenArmingIsRequiredButAbsent() =>
        AssertDenied(
            "harness.input_not_armed",
            Evaluate(Observation(), arm: null, requireArm: true));

    [TestMethod]
    public void ArmMustIdentifyTheObservedProcessImage()
    {
        AssertDenied(
            "harness.arm_identity_mismatch",
            Evaluate(Observation(), arm: Arm() with { ArmId = Guid.Empty }, requireArm: true));
        AssertDenied(
            "harness.arm_identity_mismatch",
            Evaluate(Observation(), arm: Arm() with { ProcessId = 4321 }, requireArm: true));
        AssertDenied(
            "harness.arm_identity_mismatch",
            Evaluate(
                Observation(),
                arm: Arm() with { ExecutableSha256 = OtherHash },
                requireArm: true));
    }

    [TestMethod]
    public void ExpiredNotYetValidOrOverlongArmIsDenied()
    {
        AssertDenied(
            "harness.arm_expired",
            Evaluate(
                Observation(),
                arm: Arm() with { ArmedAtUtc = Now.AddMinutes(-5), ExpiresAtUtc = Now.AddSeconds(-1) },
                requireArm: true));

        AssertDenied(
            "harness.arm_expired",
            Evaluate(
                Observation(),
                arm: Arm() with { ArmedAtUtc = Now.AddMinutes(1), ExpiresAtUtc = Now.AddMinutes(2) },
                requireArm: true));

        // Two minutes is the maximum arm lifetime; anything longer is refused
        // even while it is still nominally unexpired.
        AssertDenied(
            "harness.arm_expired",
            Evaluate(
                Observation(),
                arm: Arm() with { ArmedAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(3) },
                requireArm: true));
    }

    [TestMethod]
    public void FullyVerifiedObservationWithAValidArmIsPermitted()
    {
        HarnessSafetyDecision decision = Evaluate(Observation(), arm: Arm(), requireArm: true);

        Assert.IsTrue(decision.Allowed, decision.StableCode);
        Assert.IsEmpty(decision.Reasons);
    }

    [TestMethod]
    public void MissingExpectedIdentityIsARejectedProgrammingError() =>
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new HarnessSafetyPolicy(timeProvider: new FixedTimeProvider(Now))
                .Evaluate(Observation(), null!, LaunchId, arm: null, requireArm: false));

    [TestMethod]
    public void EveryDenialCarriesAStableCodeAndAReason()
    {
        HarnessSafetyDecision decision = Evaluate(Observation() with { IsForegroundWindow = false });

        Assert.IsFalse(decision.Allowed);
        Assert.StartsWith("harness.", decision.StableCode);
        Assert.IsNotEmpty(decision.Reasons);
    }

    private static HarnessSafetyDecision Evaluate(
        GameProcessObservation? observation,
        ExpectedGameIdentity? identity = null,
        Guid? launchCorrelationId = null,
        InputArm? arm = null,
        bool requireArm = false) =>
        new HarnessSafetyPolicy(timeProvider: new FixedTimeProvider(Now)).Evaluate(
            observation,
            identity ?? Identity(),
            launchCorrelationId ?? LaunchId,
            arm,
            requireArm);

    private static void AssertDenied(string expectedCode, HarnessSafetyDecision decision)
    {
        Assert.IsFalse(decision.Allowed, $"Expected a denial but the policy permitted the operation.");
        Assert.AreEqual(expectedCode, decision.StableCode);
    }

    private static ExpectedGameIdentity Identity() =>
        new(@"C:\games\wotblitz\wotblitz.exe", "11.18.0.7", GameHash);

    private static GameProcessObservation Observation() =>
        new(
            IsRunning: true,
            ProcessId: 1234,
            WindowHandle: 0x2A,
            ExecutablePath: @"C:\games\wotblitz\wotblitz.exe",
            ExecutableVersion: "11.18.0.7",
            ExecutableSha256: GameHash,
            IsForegroundWindow: true,
            GameIntegrity: ProcessIntegrityLevel.Medium,
            HarnessIntegrity: ProcessIntegrityLevel.Medium,
            DpiX: 96,
            DpiY: 96,
            Lifecycle: Lifecycle());

    private static ReplayLifecycleEvidence Lifecycle() =>
        new(
            State: ReplayLifecycleState.OfflineReplayActive,
            ObservedAtUtc: Now.AddSeconds(-1),
            LogWatermark: "watermark-1",
            LaunchCorrelationId: LaunchId,
            ProcessId: 1234,
            Source: "blitz-native-log");

    private static InputArm Arm() =>
        new(
            ArmId: Guid.Parse("019fa000-0000-7000-8000-00000000000a"),
            ProcessId: 1234,
            ExecutableSha256: GameHash,
            ArmedAtUtc: Now.AddSeconds(-10),
            ExpiresAtUtc: Now.AddSeconds(50));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
