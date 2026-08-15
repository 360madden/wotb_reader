namespace WotBTreader.Core.Overlay;

/// <summary>Whether the penetration feature has enough proven inputs to show
/// a colored verdict for the current aim.</summary>
public enum PenetrationAssessmentStatus
{
    /// <summary>One or more required inputs are absent, stale, unsupported,
    /// or invalid. A colored badge is forbidden.</summary>
    NotReady = 0,

    /// <summary>Every required input is present and the badge is determinate.</summary>
    Ready = 1,
}

/// <summary>Stable, additive reasons why a penetration assessment is not
/// ready. These values are mapped explicitly to wire codes by the host.</summary>
public enum PenetrationReadinessReason
{
    None = 0,
    ManagedReplayAssociationMissing,
    ManagedReplayAssociationPending,
    ManagedReplayAssociationStale,
    ManagedReplayAssociationMismatch,
    DecodedSessionMissing,
    DecodedSourceMismatch,
    BuildIdentityMissing,
    BuildUnsupported,
    ReplayBuildIncomplete,
    ReplayBuildMismatch,
    ResourceManifestMissing,
    ResourceDrift,
    ClockStale,
    AimUnavailable,
    AimStale,
    NoTarget,
    TargetDead,
    OwnEntityUnknown,
    OwnTeamUnknown,
    TargetTeamUnknown,
    TargetNotEnemy,
    WeaponStateUnavailable,
    WeaponStateStale,
    WeaponUnsupported,
    VehicleUnresolved,
    ArmorModelUnavailable,
    ArmorSurfaceMiss,
    ArmorLayerUnsupported,
    InvalidInput,
    InternalFailure,
}

/// <summary>
/// Provenance-ready envelope for the penetration feature. A not-ready
/// assessment carries ordered, deduplicated reasons and can never carry a
/// colored badge. A ready assessment always carries a determinate badge.
/// </summary>
public sealed record PenetrationAssessment
{
    private PenetrationAssessment(
        PenetrationAssessmentStatus status,
        PenetrationReadinessReason primaryReason,
        IReadOnlyList<PenetrationReadinessReason> reasons,
        string modelVersion,
        PenetrationBadge? badge,
        string? compatibilityManifestId)
    {
        Status = status;
        PrimaryReason = primaryReason;
        Reasons = reasons;
        ModelVersion = modelVersion;
        Badge = badge;
        CompatibilityManifestId = compatibilityManifestId;
    }

    public PenetrationAssessmentStatus Status { get; }

    public PenetrationReadinessReason PrimaryReason { get; }

    public IReadOnlyList<PenetrationReadinessReason> Reasons { get; }

    public string ModelVersion { get; }

    public PenetrationBadge? Badge { get; }

    public string? CompatibilityManifestId { get; }

    /// <summary>Creates a fail-closed assessment with stable reason order.</summary>
    public static PenetrationAssessment NotReady(
        PenetrationReadinessReason primaryReason,
        IEnumerable<PenetrationReadinessReason>? additionalReasons = null,
        string modelVersion = "penetration/0.3.0-alpha",
        string? compatibilityManifestId = null)
    {
        if (primaryReason == PenetrationReadinessReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(primaryReason),
                "A not-ready assessment requires a blocking reason.");
        }

        var reasons = new List<PenetrationReadinessReason> { primaryReason };
        if (additionalReasons is not null)
        {
            foreach (PenetrationReadinessReason reason in additionalReasons)
            {
                if (reason != PenetrationReadinessReason.None && !reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        return new PenetrationAssessment(
            PenetrationAssessmentStatus.NotReady,
            primaryReason,
            reasons.AsReadOnly(),
            modelVersion,
            badge: null,
            compatibilityManifestId);
    }

    /// <summary>Creates a ready assessment for a determinate badge.</summary>
    public static PenetrationAssessment Ready(
        PenetrationBadge badge,
        string modelVersion = "penetration/0.3.0-alpha",
        string? compatibilityManifestId = null)
    {
        if (badge.Verdict.Band == PenetrationBand.Unknown)
        {
            throw new ArgumentException(
                "A ready assessment requires a determinate penetration band.",
                nameof(badge));
        }

        return new PenetrationAssessment(
            PenetrationAssessmentStatus.Ready,
            PenetrationReadinessReason.None,
            Array.Empty<PenetrationReadinessReason>(),
            modelVersion,
            badge,
            compatibilityManifestId);
    }

    public PenetrationAssessment WithCompatibilityManifest(
        string? compatibilityManifestId) =>
        new(
            Status,
            PrimaryReason,
            Reasons,
            ModelVersion,
            Badge,
            compatibilityManifestId);
}
