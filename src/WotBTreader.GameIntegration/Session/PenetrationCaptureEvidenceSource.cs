using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Exact-build owner-census source for the admitted capture phase. It runs
/// the same gated vftable AOB scan the avatar/camera anchors already use and
/// reports only privacy-safe counts. Viewpoint-ownership attribution is not
/// yet proven, so the aggregate honestly reports every live candidate the
/// census could not exclude and leaves the later shell/aim/ray phases
/// unproven (the evaluator stays <c>NotReady</c> until those are proven).
/// </summary>
internal sealed class ExactBuildOwnerCensusCaptureEvidenceSource
    : IPenetrationCaptureEvidenceSource
{
    /// <summary>
    /// Hash-bound 11.19.0.10 vftable RVAs derived from the RTTI complete
    /// object locators via <c>FindVftableViaCol.java</c> (executable SHA-256
    /// <c>1cda5c31…</c>): VehicleGun COL 0x35ce9e0, VehicleGunRotator COL
    /// 0x35e06a8, AvatarGunAgent COL 0x35317c0. The vftable pointer is the
    /// object's first dword, so the census scans heap regions for the image
    /// address <c>moduleBase + rva</c> exactly as the avatar-stats anchor
    /// does. AvatarGunAgent is census context only (a bridge candidate, not
    /// a proven state owner) and is never required for uniqueness.
    /// </summary>
    private const uint VehicleGunVftableRva = 0x32dacf4;
    private const uint VehicleGunRotatorVftableRva = 0x32eeb40;
    private const uint AvatarGunAgentVftableRva = 0x324dae8;

    private const int CensusMaxCandidates = 64;

    private readonly IMemoryScanDiscoverer _scanDiscoverer;
    private readonly ILogger<ExactBuildOwnerCensusCaptureEvidenceSource> _logger;

    public ExactBuildOwnerCensusCaptureEvidenceSource(
        IMemoryScanDiscoverer scanDiscoverer,
        ILogger<ExactBuildOwnerCensusCaptureEvidenceSource> logger)
    {
        _scanDiscoverer = scanDiscoverer ?? throw new ArgumentNullException(nameof(scanDiscoverer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<OperationResult<PenetrationCaptureSourceAggregate>> CaptureAsync(
        PenetrationCaptureReadContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        CensusCounts? first = await RunCensusAsync(context, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return CensusUnavailable();
        }

        CensusCounts? second = await RunCensusAsync(context, cancellationToken).ConfigureAwait(false);
        if (second is null)
        {
            return CensusUnavailable();
        }

        CensusCounts a = first.Value;
        CensusCounts b = second.Value;
        bool stable = a.VehicleGun == b.VehicleGun
            && a.VehicleGunRotator == b.VehicleGunRotator
            && a.AvatarGunAgent == b.AvatarGunAgent;

        // Privacy-safe census only: counts, never addresses, ids, or paths.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Penetration owner census: vehicleGun={VehicleGunCount}, vehicleGunRotator={VehicleGunRotatorCount}, avatarGunAgent={AvatarGunAgentCount}, stable={Stable}",
                a.VehicleGun,
                a.VehicleGunRotator,
                a.AvatarGunAgent,
                stable);
        }

        int ownerCandidates = a.VehicleGun + a.VehicleGunRotator;
        bool unique = a.VehicleGun == 1 && a.VehicleGunRotator == 1;

        return OperationResult.Success(new PenetrationCaptureSourceAggregate(
            OwnerCandidateCount: ownerCandidates,
            ObservationRounds: 2,
            IndividualReadBytes: 0,
            BatchReadBytes: 0,
            new PenetrationCaptureEvidence(
                OwnerUnique: unique,
                OwnerStable: stable,
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
                RawObservationRetained: false)));
    }

    public override string ToString() => nameof(ExactBuildOwnerCensusCaptureEvidenceSource);

    private static OperationResult<PenetrationCaptureSourceAggregate> CensusUnavailable() =>
        OperationResult.Failure<PenetrationCaptureSourceAggregate>(
            new ApplicationError(
                "capture.census_unavailable",
                "The exact-build owner census could not be read.",
                Retryable: true));

    private async ValueTask<CensusCounts?> RunCensusAsync(
        PenetrationCaptureReadContext context,
        CancellationToken cancellationToken)
    {
        int vehicleGun = await CountInstancesAsync(
            context, VehicleGunVftableRva, cancellationToken).ConfigureAwait(false);
        if (vehicleGun < 0)
        {
            return null;
        }

        int vehicleGunRotator = await CountInstancesAsync(
            context, VehicleGunRotatorVftableRva, cancellationToken).ConfigureAwait(false);
        if (vehicleGunRotator < 0)
        {
            return null;
        }

        int avatarGunAgent = await CountInstancesAsync(
            context, AvatarGunAgentVftableRva, cancellationToken).ConfigureAwait(false);
        if (avatarGunAgent < 0)
        {
            return null;
        }

        return new CensusCounts(vehicleGun, vehicleGunRotator, avatarGunAgent);
    }

    /// <summary>
    /// Counts live heap objects whose first dword equals the pinned vftable
    /// image address. Returns -1 when the guarded scan cannot produce a count,
    /// never fabricating a zero. The scan matches the exact 4-byte dword over
    /// Private|Mapped regions (object instances), never module image data.
    /// </summary>
    private async ValueTask<int> CountInstancesAsync(
        PenetrationCaptureReadContext context,
        uint vftableRva,
        CancellationToken cancellationToken)
    {
        uint expectedVftable = (uint)(context.ModuleBaseAddress + vftableRva);
        byte[] expected = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, expectedVftable);
        MemoryScanRequest request = new(
            FieldName: "pen-owner-census-vftable",
            FieldType: "Bytes",
            ExpectedValue: expected,
            ToleranceMask: null,
            MaxCandidates: CensusMaxCandidates,
            MinRegionSize: 4096,
            Alignment: 4);

        try
        {
            OperationResult<MemoryScanResult> result = await Task.Run(
                () => _scanDiscoverer.Scan(
                    context.Observation,
                    context.ModuleBaseAddress,
                    request,
                    cancellationToken,
                    "aob"),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value is null)
            {
                return -1;
            }

            return result.Value.TotalMatchesBeforeTruncation;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private readonly record struct CensusCounts(
        int VehicleGun,
        int VehicleGunRotator,
        int AvatarGunAgent);
}
