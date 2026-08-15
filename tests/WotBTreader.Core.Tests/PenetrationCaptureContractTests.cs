using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class PenetrationCaptureContractTests
{
    private static PenetrationCaptureBuildIdentity ExpectedBuild =>
        PenetrationCaptureBuildIdentity.WotBlitz1119010;

    private static PenetrationCaptureEvidence ValidEvidence => new(
        OwnerUnique: true,
        OwnerStable: true,
        ConfiguredGunJoined: true,
        ShellAbaTransitionObserved: true,
        ShellStatesObserved: 3,
        ShellIdentityMatches: 3,
        AimSamples: 8,
        FiniteAimSamples: 8,
        TurretYawIndependent: true,
        GunElevationIndependent: true,
        RaySamples: 8,
        FiniteRaySamples: 8,
        NormalizedRaySamples: 8,
        JoinedRaySamples: 4,
        CameraFallbackUsed: false,
        PostShotOnlyObservation: false,
        RawObservationRetained: false);

    private static PenetrationCaptureRun ValidRun(bool distinctFromPrior = false) => new(
        PenetrationCaptureVerificationState.OfflineReplayVerified,
        PenetrationCaptureLimits.RequiredVerificationReasonCode,
        ManagedAssociationCurrent: true,
        ExactArtifactBound: true,
        DecodeRunComplete: true,
        ProcessIdentityMatches: true,
        ExpectedBuild,
        Duration: TimeSpan.FromSeconds(30),
        OwnerCandidateCount: 1,
        ObservationRounds: 32,
        IndividualReadBytes: 320,
        BatchReadBytes: 4096,
        ValidEvidence,
        ContentDistinctFromPrior: distinctFromPrior);

    [TestMethod]
    public void ValidFirstRun_RequiresDistinctRepeatBeforePromotion()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun(),
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.PositiveAwaitingRepeat, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.RepeatRequired, result.PrimaryReason);
        Assert.IsTrue(result.ExactWeaponOwnerProven);
        Assert.IsTrue(result.ExactLoadedShellProven);
        Assert.IsTrue(result.ExactGunRayProven);
        Assert.IsFalse(result.CanPromoteExactInputs);
    }

    [TestMethod]
    public void TwoValidContentDistinctRuns_ArePromotionReady()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun(),
            ExpectedBuild,
            ValidRun(distinctFromPrior: true));

        Assert.AreEqual(PenetrationCaptureStatus.PromotionReady, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.None, result.PrimaryReason);
        Assert.IsEmpty(result.Reasons);
        Assert.IsTrue(result.CanPromoteExactInputs);
        Assert.AreEqual(8, result.Summary.AimSamples);
        Assert.AreEqual(4, result.Summary.JoinedRaySamples);
    }

    [TestMethod]
    public void MissingOfflineGate_RejectsEvenCompleteEvidence()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with
            {
                VerificationState = PenetrationCaptureVerificationState.Unknown,
            },
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.OfflineGateMissing, result.PrimaryReason);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.OfflineGateMissing));
        Assert.IsFalse(result.CanPromoteExactInputs);
    }

    [TestMethod]
    public void WrongBuildAndOversizedRead_RejectWithStableReasons()
    {
        var wrongBuild = new PenetrationCaptureBuildIdentity(
            ExpectedBuild.GameVersion,
            new ContentHash(new string('0', ContentHash.Sha256HexLength)));

        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with
            {
                ObservedBuild = wrongBuild,
                IndividualReadBytes = PenetrationCaptureLimits.MaxIndividualReadBytes + 1,
            },
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.BuildIdentityMismatch, result.PrimaryReason);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.BoundsExceeded));
    }

    [TestMethod]
    public void AmbiguousOwner_RejectsWithoutPromotingOtherEvidence()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with
            {
                OwnerCandidateCount = 2,
                Evidence = ValidEvidence with { OwnerUnique = false },
            },
            ExpectedBuild,
            ValidRun(distinctFromPrior: true));

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.OwnerNotUnique, result.PrimaryReason);
        Assert.IsFalse(result.ExactWeaponOwnerProven);
        Assert.IsFalse(result.ExactLoadedShellProven);
        Assert.IsFalse(result.ExactGunRayProven);
    }

    [TestMethod]
    public void IncompleteShellAndAimEvidence_ReportsAllBlockingReasons()
    {
        PenetrationCaptureEvidence evidence = ValidEvidence with
        {
            ShellAbaTransitionObserved = false,
            ShellIdentityMatches = 1,
            AimSamples = 2,
            FiniteAimSamples = 1,
            TurretYawIndependent = false,
            GunElevationIndependent = false,
        };

        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with { Evidence = evidence },
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.ShellTransitionUnproven));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.ShellIdentityUnproven));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.AimSamplesInsufficient));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.AimNonFinite));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.TurretYawUnproven));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.GunElevationUnproven));
    }

    [TestMethod]
    public void InconsistentAggregateCounts_AreRejected()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with
            {
                Evidence = ValidEvidence with
                {
                    JoinedRaySamples = 9,
                },
            },
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.BoundsExceeded));
        Assert.IsFalse(result.CanPromoteExactInputs);
    }

    [TestMethod]
    public void CameraFallbackPostShotAndRawRetention_AreRejected()
    {
        PenetrationCaptureEvidence evidence = ValidEvidence with
        {
            CameraFallbackUsed = true,
            PostShotOnlyObservation = true,
            RawObservationRetained = true,
            NormalizedRaySamples = 7,
            JoinedRaySamples = 2,
        };

        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun() with { Evidence = evidence },
            ExpectedBuild);

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.CameraFallbackUsed));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.PostShotOnlyObservation));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.RawObservationRetained));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.RayNotNormalized));
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.RayTargetJoinInsufficient));
    }

    [TestMethod]
    public void SameContentRepeat_RejectsEvenWhenBothRunsAreOtherwiseValid()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun(),
            ExpectedBuild,
            ValidRun());

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.RepeatEvidenceRejected, result.PrimaryReason);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.RepeatNotContentDistinct));
        Assert.IsFalse(result.CanPromoteExactInputs);
    }

    [TestMethod]
    public void RepeatWithFailedGate_RejectsAndNeverPromotes()
    {
        PenetrationCaptureEvaluation result = PenetrationCaptureEvaluator.Evaluate(
            ValidRun(),
            ExpectedBuild,
            ValidRun(distinctFromPrior: true) with
            {
                ProcessIdentityMatches = false,
            });

        Assert.AreEqual(PenetrationCaptureStatus.Rejected, result.Status);
        Assert.AreEqual(PenetrationCaptureReason.RepeatEvidenceRejected, result.PrimaryReason);
        Assert.IsTrue(result.Reasons.Contains(PenetrationCaptureReason.ProcessIdentityMismatch));
        Assert.IsFalse(result.CanPromoteExactInputs);
    }
}
