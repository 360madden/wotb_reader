namespace WotBTreader.Core.Overlay;

/// <summary>Lifecycle state admitted by the exact-input capture evaluator.</summary>
public enum PenetrationCaptureVerificationState
{
    Unknown = 0,
    OfflineReplayVerified = 1,
}

/// <summary>Outcome of one bounded exact-input capture or its repeat.</summary>
public enum PenetrationCaptureStatus
{
    Rejected = 0,
    PositiveAwaitingRepeat = 1,
    PromotionReady = 2,
}

/// <summary>Stable reasons that prevent exact-input evidence promotion.</summary>
public enum PenetrationCaptureReason
{
    None = 0,
    OfflineGateMissing,
    VerificationReasonMismatch,
    ManagedAssociationMissing,
    ArtifactAssociationMissing,
    DecodeRunIncomplete,
    ProcessIdentityMismatch,
    BuildIdentityMissing,
    BuildIdentityMismatch,
    BoundsExceeded,
    OwnerNotUnique,
    OwnerUnstable,
    ConfiguredGunUnproven,
    ShellTransitionUnproven,
    ShellIdentityUnproven,
    AimSamplesInsufficient,
    AimNonFinite,
    TurretYawUnproven,
    GunElevationUnproven,
    RaySamplesInsufficient,
    RayNonFinite,
    RayNotNormalized,
    RayTargetJoinInsufficient,
    CameraFallbackUsed,
    PostShotOnlyObservation,
    RawObservationRetained,
    RepeatRequired,
    RepeatNotContentDistinct,
    RepeatEvidenceRejected,
}

/// <summary>Exact build identity accepted by the capture contract.</summary>
public sealed record PenetrationCaptureBuildIdentity(
    string GameVersion,
    ContentHash ExecutableSha256)
{
    /// <summary>Published exact-build identity for the current capture lane.</summary>
    public static PenetrationCaptureBuildIdentity WotBlitz1119010 { get; } = new(
        "11.19.0.10",
        new ContentHash("1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d"));
}

/// <summary>
/// Fixed safety bounds for one managed-offline source capture. These are
/// evaluator invariants, not caller-configurable request parameters.
/// </summary>
public static class PenetrationCaptureLimits
{
    public const string RequiredVerificationReasonCode = "session.offline_replay_verified";
    public const int MaxOwnerCandidates = 4;
    public const int MaxObservationRoundsPerPhase = 64;
    public const int MaxIndividualReadBytes = 4096;
    public const int MaxBatchReadBytes = 16 * 1024;
    public const int MinimumShellStates = 3;
    public const int MinimumAimSamples = 8;
    public const int MinimumRaySamples = 8;
    public const int MinimumJoinedRays = 4;
    public static readonly TimeSpan MaxCaptureDuration = TimeSpan.FromSeconds(300);
}

/// <summary>
/// Coordinator-produced gate facts for one capture. No PID, address, path,
/// token, or raw memory value is represented here.
/// </summary>
public sealed record PenetrationCaptureRun(
    PenetrationCaptureVerificationState VerificationState,
    string? VerificationReasonCode,
    bool ManagedAssociationCurrent,
    bool ExactArtifactBound,
    bool DecodeRunComplete,
    bool ProcessIdentityMatches,
    PenetrationCaptureBuildIdentity? ObservedBuild,
    TimeSpan Duration,
    int OwnerCandidateCount,
    int ObservationRounds,
    int IndividualReadBytes,
    int BatchReadBytes,
    PenetrationCaptureEvidence Evidence,
    bool ContentDistinctFromPrior = false);

/// <summary>Privacy-safe aggregate facts from one capture phase set.</summary>
public sealed record PenetrationCaptureEvidence(
    bool OwnerUnique,
    bool OwnerStable,
    bool ConfiguredGunJoined,
    bool ShellAbaTransitionObserved,
    int ShellStatesObserved,
    int ShellIdentityMatches,
    int AimSamples,
    int FiniteAimSamples,
    bool TurretYawIndependent,
    bool GunElevationIndependent,
    int RaySamples,
    int FiniteRaySamples,
    int NormalizedRaySamples,
    int JoinedRaySamples,
    bool CameraFallbackUsed,
    bool PostShotOnlyObservation,
    bool RawObservationRetained);

/// <summary>Bounded, non-sensitive summary returned with an evaluation.</summary>
public sealed record PenetrationCaptureSummary(
    int OwnerCandidateCount,
    int ShellStatesObserved,
    int ShellIdentityMatches,
    int AimSamples,
    int RaySamples,
    int JoinedRaySamples);

/// <summary>
/// Pure result of evaluating one capture and, optionally, its distinct repeat.
/// A positive first run is deliberately not enough for promotion.
/// </summary>
public sealed record PenetrationCaptureEvaluation(
    PenetrationCaptureStatus Status,
    PenetrationCaptureReason PrimaryReason,
    IReadOnlyList<PenetrationCaptureReason> Reasons,
    bool ExactWeaponOwnerProven,
    bool ExactLoadedShellProven,
    bool ExactGunRayProven,
    PenetrationCaptureSummary Summary)
{
    public bool CanPromoteExactInputs => Status == PenetrationCaptureStatus.PromotionReady;
}

/// <summary>
/// Evaluates the managed-offline capture contract without IO, Win32, memory
/// access, logging, or persistence. It is the promotion gate that later
/// coordinator code must call after producing an aggregate.
/// </summary>
public static class PenetrationCaptureEvaluator
{
    /// <summary>
    /// Evaluates the current run. With no repeat, a complete run is positive
    /// but remains pending; two complete content-distinct runs are required.
    /// </summary>
    public static PenetrationCaptureEvaluation Evaluate(
        PenetrationCaptureRun current,
        PenetrationCaptureBuildIdentity expectedBuild,
        PenetrationCaptureRun? distinctRepeat = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expectedBuild);

        List<PenetrationCaptureReason> currentReasons = EvaluateRun(
            current,
            expectedBuild);
        PenetrationCaptureSummary summary = Summarize(current);
        if (currentReasons.Count > 0)
        {
            return Rejected(currentReasons, summary);
        }

        if (distinctRepeat is null)
        {
            return new PenetrationCaptureEvaluation(
                PenetrationCaptureStatus.PositiveAwaitingRepeat,
                PenetrationCaptureReason.RepeatRequired,
                [PenetrationCaptureReason.RepeatRequired],
                ExactWeaponOwnerProven: true,
                ExactLoadedShellProven: true,
                ExactGunRayProven: true,
                summary);
        }

        List<PenetrationCaptureReason> repeatReasons = EvaluateRun(
            distinctRepeat,
            expectedBuild);
        if (!IsContentDistinctRepeat(current, distinctRepeat))
        {
            repeatReasons = AppendDistinct(
                repeatReasons,
                PenetrationCaptureReason.RepeatNotContentDistinct);
        }

        if (repeatReasons.Count > 0)
        {
            var reasons = new List<PenetrationCaptureReason>
            {
                PenetrationCaptureReason.RepeatEvidenceRejected,
            };
            reasons.AddRange(repeatReasons);
            return Rejected(reasons, summary);
        }

        return new PenetrationCaptureEvaluation(
            PenetrationCaptureStatus.PromotionReady,
            PenetrationCaptureReason.None,
            Array.Empty<PenetrationCaptureReason>(),
            ExactWeaponOwnerProven: true,
            ExactLoadedShellProven: true,
            ExactGunRayProven: true,
            summary);
    }

    private static List<PenetrationCaptureReason> EvaluateRun(
        PenetrationCaptureRun run,
        PenetrationCaptureBuildIdentity expectedBuild)
    {
        var reasons = new List<PenetrationCaptureReason>();

        if (run.VerificationState != PenetrationCaptureVerificationState.OfflineReplayVerified)
        {
            reasons.Add(PenetrationCaptureReason.OfflineGateMissing);
        }

        if (!string.Equals(
                run.VerificationReasonCode,
                PenetrationCaptureLimits.RequiredVerificationReasonCode,
                StringComparison.Ordinal))
        {
            reasons.Add(PenetrationCaptureReason.VerificationReasonMismatch);
        }

        if (!run.ManagedAssociationCurrent)
        {
            reasons.Add(PenetrationCaptureReason.ManagedAssociationMissing);
        }

        if (!run.ExactArtifactBound)
        {
            reasons.Add(PenetrationCaptureReason.ArtifactAssociationMissing);
        }

        if (!run.DecodeRunComplete)
        {
            reasons.Add(PenetrationCaptureReason.DecodeRunIncomplete);
        }

        if (!run.ProcessIdentityMatches)
        {
            reasons.Add(PenetrationCaptureReason.ProcessIdentityMismatch);
        }

        if (run.ObservedBuild is null)
        {
            reasons.Add(PenetrationCaptureReason.BuildIdentityMissing);
        }
        else if (run.ObservedBuild != expectedBuild)
        {
            reasons.Add(PenetrationCaptureReason.BuildIdentityMismatch);
        }

        if (run.Duration < TimeSpan.Zero || run.Duration > PenetrationCaptureLimits.MaxCaptureDuration ||
            run.OwnerCandidateCount < 0 ||
            run.OwnerCandidateCount > PenetrationCaptureLimits.MaxOwnerCandidates ||
            run.ObservationRounds < 0 ||
            run.ObservationRounds > PenetrationCaptureLimits.MaxObservationRoundsPerPhase * 4 ||
            run.IndividualReadBytes < 0 ||
            run.IndividualReadBytes > PenetrationCaptureLimits.MaxIndividualReadBytes ||
            run.BatchReadBytes < 0 ||
            run.BatchReadBytes > PenetrationCaptureLimits.MaxBatchReadBytes ||
            HasInvalidEvidenceCounts(run.Evidence))
        {
            reasons.Add(PenetrationCaptureReason.BoundsExceeded);
        }

        PenetrationCaptureEvidence evidence = run.Evidence;
        if (!evidence.OwnerUnique || run.OwnerCandidateCount != 1)
        {
            reasons.Add(PenetrationCaptureReason.OwnerNotUnique);
        }

        if (!evidence.OwnerStable)
        {
            reasons.Add(PenetrationCaptureReason.OwnerUnstable);
        }

        if (!evidence.ConfiguredGunJoined)
        {
            reasons.Add(PenetrationCaptureReason.ConfiguredGunUnproven);
        }

        if (!evidence.ShellAbaTransitionObserved ||
            evidence.ShellStatesObserved < PenetrationCaptureLimits.MinimumShellStates)
        {
            reasons.Add(PenetrationCaptureReason.ShellTransitionUnproven);
        }

        if (evidence.ShellIdentityMatches < PenetrationCaptureLimits.MinimumShellStates)
        {
            reasons.Add(PenetrationCaptureReason.ShellIdentityUnproven);
        }

        if (evidence.AimSamples < PenetrationCaptureLimits.MinimumAimSamples)
        {
            reasons.Add(PenetrationCaptureReason.AimSamplesInsufficient);
        }

        if (evidence.FiniteAimSamples != evidence.AimSamples)
        {
            reasons.Add(PenetrationCaptureReason.AimNonFinite);
        }

        if (!evidence.TurretYawIndependent)
        {
            reasons.Add(PenetrationCaptureReason.TurretYawUnproven);
        }

        if (!evidence.GunElevationIndependent)
        {
            reasons.Add(PenetrationCaptureReason.GunElevationUnproven);
        }

        if (evidence.RaySamples < PenetrationCaptureLimits.MinimumRaySamples)
        {
            reasons.Add(PenetrationCaptureReason.RaySamplesInsufficient);
        }

        if (evidence.FiniteRaySamples != evidence.RaySamples)
        {
            reasons.Add(PenetrationCaptureReason.RayNonFinite);
        }

        if (evidence.NormalizedRaySamples != evidence.RaySamples)
        {
            reasons.Add(PenetrationCaptureReason.RayNotNormalized);
        }

        if (evidence.JoinedRaySamples < PenetrationCaptureLimits.MinimumJoinedRays)
        {
            reasons.Add(PenetrationCaptureReason.RayTargetJoinInsufficient);
        }

        if (evidence.CameraFallbackUsed)
        {
            reasons.Add(PenetrationCaptureReason.CameraFallbackUsed);
        }

        if (evidence.PostShotOnlyObservation)
        {
            reasons.Add(PenetrationCaptureReason.PostShotOnlyObservation);
        }

        if (evidence.RawObservationRetained)
        {
            reasons.Add(PenetrationCaptureReason.RawObservationRetained);
        }

        return reasons;
    }

    private static bool IsContentDistinctRepeat(
        PenetrationCaptureRun current,
        PenetrationCaptureRun repeat)
    {
        // The managed association layer proves content distinction without
        // exposing artifact identifiers. The capture aggregate carries only
        // that boolean witness into this pure evaluator.
        return current.ExactArtifactBound &&
            repeat.ExactArtifactBound &&
            repeat.ContentDistinctFromPrior;
    }

    private static bool HasInvalidEvidenceCounts(PenetrationCaptureEvidence evidence) =>
        evidence.ShellStatesObserved < 0 ||
        evidence.ShellIdentityMatches < 0 ||
        evidence.ShellIdentityMatches > evidence.ShellStatesObserved ||
        evidence.AimSamples < 0 ||
        evidence.FiniteAimSamples < 0 ||
        evidence.FiniteAimSamples > evidence.AimSamples ||
        evidence.RaySamples < 0 ||
        evidence.FiniteRaySamples < 0 ||
        evidence.FiniteRaySamples > evidence.RaySamples ||
        evidence.NormalizedRaySamples < 0 ||
        evidence.NormalizedRaySamples > evidence.RaySamples ||
        evidence.JoinedRaySamples < 0 ||
        evidence.JoinedRaySamples > evidence.NormalizedRaySamples;

    private static PenetrationCaptureEvaluation Rejected(
        IReadOnlyList<PenetrationCaptureReason> reasons,
        PenetrationCaptureSummary summary)
    {
        PenetrationCaptureReason[] distinct = reasons
            .Where(static reason => reason != PenetrationCaptureReason.None)
            .Distinct()
            .ToArray();

        return new PenetrationCaptureEvaluation(
            PenetrationCaptureStatus.Rejected,
            distinct.Length == 0 ? PenetrationCaptureReason.RepeatEvidenceRejected : distinct[0],
            distinct,
            ExactWeaponOwnerProven: false,
            ExactLoadedShellProven: false,
            ExactGunRayProven: false,
            summary);
    }

    private static List<PenetrationCaptureReason> AppendDistinct(
        IReadOnlyList<PenetrationCaptureReason> reasons,
        PenetrationCaptureReason reason)
    {
        return reasons.Contains(reason)
            ? reasons.ToList()
            : reasons.Concat([reason]).ToList();
    }

    private static PenetrationCaptureSummary Summarize(PenetrationCaptureRun run) =>
        new(
            run.OwnerCandidateCount,
            run.Evidence.ShellStatesObserved,
            run.Evidence.ShellIdentityMatches,
            run.Evidence.AimSamples,
            run.Evidence.RaySamples,
            run.Evidence.JoinedRaySamples);
}
