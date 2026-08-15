using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core.Overlay;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Coordinator-owned read context for the one admitted capture phase. It is
/// internal so no host can provide a process identity, module base, address,
/// or read plan. Implementations may use these values only to produce the
/// bounded aggregate below.
/// </summary>
internal sealed record PenetrationCaptureReadContext(
    PenetrationCapturePhaseIntent PhaseIntent,
    PenetrationCaptureBuildIdentity ExpectedBuild,
    AuthorizedMemoryObservation Observation,
    long ModuleBaseAddress);

/// <summary>
/// Aggregate-only result of the coordinator's fixed capture plan. Raw bytes,
/// pointers, process details, and candidate addresses cannot cross this seam.
/// </summary>
internal sealed record PenetrationCaptureSourceAggregate(
    int OwnerCandidateCount,
    int ObservationRounds,
    int IndividualReadBytes,
    int BatchReadBytes,
    PenetrationCaptureEvidence Evidence);

/// <summary>
/// Internal seam for the future exact-build source implementation. The first
/// implementation deliberately remains neutral because static evidence has
/// not yet proven configured-gun, loaded-shell, or shot-ray field ownership.
/// </summary>
internal interface IPenetrationCaptureEvidenceSource
{
    ValueTask<OperationResult<PenetrationCaptureSourceAggregate>> CaptureAsync(
        PenetrationCaptureReadContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Safe production default until the owner-approved exact-input source is
/// proven. Returning an aggregate rather than throwing preserves the HUD's
/// neutral readiness state and keeps unsupported fields from being promoted.
/// </summary>
internal sealed class UnavailablePenetrationCaptureEvidenceSource
    : IPenetrationCaptureEvidenceSource
{
    public ValueTask<OperationResult<PenetrationCaptureSourceAggregate>> CaptureAsync(
        PenetrationCaptureReadContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(OperationResult.Success(
            new PenetrationCaptureSourceAggregate(
                OwnerCandidateCount: 0,
                ObservationRounds: 0,
                IndividualReadBytes: 0,
                BatchReadBytes: 0,
                new PenetrationCaptureEvidence(
                    OwnerUnique: false,
                    OwnerStable: false,
                    ConfiguredGunJoined: false,
                    ShellAbaTransitionObserved: false,
                    ShellStatesObserved: 0,
                    ShellIdentityMatches: 0,
                    AimSamples: 0,
                    FiniteAimSamples: 0,
                    TurretYawIndependent: false,
                    GunElevationIndependent: false,
                    RaySamples: 0,
                    FiniteRaySamples: 0,
                    NormalizedRaySamples: 0,
                    JoinedRaySamples: 0,
                    CameraFallbackUsed: false,
                    PostShotOnlyObservation: false,
                    RawObservationRetained: false))));
    }

    public override string ToString() => nameof(UnavailablePenetrationCaptureEvidenceSource);
}
