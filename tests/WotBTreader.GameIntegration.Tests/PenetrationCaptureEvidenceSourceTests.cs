using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;
using WotBTreader.GameIntegration.Session;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class PenetrationCaptureEvidenceSourceTests
{
    private const long ModuleBase = 0x10000000;

    // Hash-bound 11.19.0.10 vftable RVAs (FindVftableViaCol, SHA-256 1cda5c31…):
    private const uint VehicleGunVftableRva = 0x32dacf4;
    private const uint VehicleGunRotatorVftableRva = 0x32eeb40;
    private const uint AvatarGunAgentVftableRva = 0x324dae8;

    [TestMethod]
    public async Task CaptureAsync_UniqueStableOwners_ReportsUniqueAndStable()
    {
        var discoverer = new CensusScanDiscoverer([1, 1, 0, 1, 1, 0]);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        OperationResult<PenetrationCaptureSourceAggregate> result = await source
            .CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        PenetrationCaptureSourceAggregate aggregate = result.Value!;
        Assert.AreEqual(2, aggregate.OwnerCandidateCount);
        Assert.AreEqual(2, aggregate.ObservationRounds);
        Assert.AreEqual(0, aggregate.IndividualReadBytes);
        Assert.AreEqual(0, aggregate.BatchReadBytes);
        Assert.IsTrue(aggregate.Evidence.OwnerUnique);
        Assert.IsTrue(aggregate.Evidence.OwnerStable);
        // Phases 2-4 stay honestly unproven; the census only addresses ownership.
        Assert.IsFalse(aggregate.Evidence.ConfiguredGunJoined);
        Assert.IsFalse(aggregate.Evidence.ShellAbaTransitionObserved);
        Assert.AreEqual(0, aggregate.Evidence.ShellStatesObserved);
        Assert.AreEqual(0, aggregate.Evidence.ShellIdentityMatches);
        Assert.AreEqual(0, aggregate.Evidence.AimSamples);
        Assert.AreEqual(0, aggregate.Evidence.RaySamples);
        Assert.AreEqual(0, aggregate.Evidence.JoinedRaySamples);
        Assert.IsFalse(aggregate.Evidence.RawObservationRetained);
        Assert.AreEqual(6, discoverer.ScanCount);
    }

    [TestMethod]
    public async Task CaptureAsync_AmbiguousInstances_ReportsNotUnique()
    {
        var discoverer = new CensusScanDiscoverer([7, 7, 7, 7, 7, 7]);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        OperationResult<PenetrationCaptureSourceAggregate> result = await source
            .CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        PenetrationCaptureSourceAggregate aggregate = result.Value!;
        Assert.AreEqual(14, aggregate.OwnerCandidateCount);
        Assert.IsFalse(aggregate.Evidence.OwnerUnique);
        Assert.IsTrue(aggregate.Evidence.OwnerStable);
    }

    [TestMethod]
    public async Task CaptureAsync_UnstableCountsAcrossPasses_ReportsUnstable()
    {
        var discoverer = new CensusScanDiscoverer([1, 1, 0, 2, 1, 0]);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        OperationResult<PenetrationCaptureSourceAggregate> result = await source
            .CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        PenetrationCaptureSourceAggregate aggregate = result.Value!;
        Assert.IsFalse(aggregate.Evidence.OwnerStable);
        // First-pass counts still drive the candidate count and uniqueness.
        Assert.AreEqual(2, aggregate.OwnerCandidateCount);
        Assert.IsTrue(aggregate.Evidence.OwnerUnique);
    }

    [TestMethod]
    public async Task CaptureAsync_ZeroCandidates_ReportsNotUnique()
    {
        var discoverer = new CensusScanDiscoverer([0, 0, 0, 0, 0, 0]);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        OperationResult<PenetrationCaptureSourceAggregate> result = await source
            .CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        PenetrationCaptureSourceAggregate aggregate = result.Value!;
        Assert.AreEqual(0, aggregate.OwnerCandidateCount);
        Assert.IsFalse(aggregate.Evidence.OwnerUnique);
        Assert.IsTrue(aggregate.Evidence.OwnerStable);
    }

    [TestMethod]
    public async Task CaptureAsync_ScanFailure_FailsClosed()
    {
        var discoverer = new CensusScanDiscoverer([1, 1, 0, 1, 1, 0], failAtCall: 4);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        OperationResult<PenetrationCaptureSourceAggregate> result = await source
            .CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual("capture.census_unavailable", result.Error!.Code);
    }

    [TestMethod]
    public async Task CaptureAsync_ScanRequestsTargetPinnedVftables()
    {
        var discoverer = new CensusScanDiscoverer([1, 1, 0, 1, 1, 0]);
        var source = new ExactBuildOwnerCensusCaptureEvidenceSource(
            discoverer,
            NullLogger<ExactBuildOwnerCensusCaptureEvidenceSource>.Instance);

        await source.CaptureAsync(CreateContext(), CancellationToken.None);

        Assert.HasCount(6, discoverer.Requests);
        for (int i = 0; i < discoverer.Requests.Count; i++)
        {
            MemoryScanRequest request = discoverer.Requests[i];
            Assert.AreEqual("Bytes", request.FieldType);
            Assert.AreEqual(4, request.Alignment);
            Assert.AreEqual(64, request.MaxCandidates);
            Assert.AreEqual(4096, request.MinRegionSize);
            Assert.IsNull(request.ToleranceMask);
        }

        AssertRequestsTarget(discoverer.Requests[0], VehicleGunVftableRva);
        AssertRequestsTarget(discoverer.Requests[1], VehicleGunRotatorVftableRva);
        AssertRequestsTarget(discoverer.Requests[2], AvatarGunAgentVftableRva);
        AssertRequestsTarget(discoverer.Requests[3], VehicleGunVftableRva);
        AssertRequestsTarget(discoverer.Requests[4], VehicleGunRotatorVftableRva);
        AssertRequestsTarget(discoverer.Requests[5], AvatarGunAgentVftableRva);
    }

    private static void AssertRequestsTarget(MemoryScanRequest request, uint vftableRva)
    {
        Assert.IsNotNull(request.ExpectedValue);
        Assert.HasCount(4, request.ExpectedValue);
        uint actual = BinaryPrimitives.ReadUInt32LittleEndian(request.ExpectedValue);
        Assert.AreEqual((uint)(ModuleBase + vftableRva), actual);
    }

    private static PenetrationCaptureReadContext CreateContext() =>
        new(
            PenetrationCapturePhaseIntent.FullExactInputVerdict,
            PenetrationCaptureBuildIdentity.WotBlitz1119010,
            new AuthorizedMemoryObservation(
                ProcessId: 0,
                ProcessStartIdentity: 1,
                CanonicalExecutablePath: @"C:\missing.exe",
                ProductVersion: "11.19.0.10",
                ExecutableSha256: new ContentHash(
                    "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d"),
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
                ReadGate: new AuthorizationReadGate()),
            ModuleBase);

    /// <summary>
    /// Serves one scripted count per scan call (pass 1 then pass 2, each in
    /// VehicleGun → VehicleGunRotator → AvatarGunAgent order), with an
    /// optional injected failure at a specific call index.
    /// </summary>
    private sealed class CensusScanDiscoverer(
        int[] counts,
        int? failAtCall = null) : IMemoryScanDiscoverer
    {
        private int _call;

        public int ScanCount => _call;
        public List<MemoryScanRequest> Requests { get; } = [];

        public OperationResult<MemoryScanResult> Scan(
            AuthorizedMemoryObservation observation,
            long baseAddress,
            MemoryScanRequest request,
            CancellationToken cancellationToken,
            string scanKind = "value")
        {
            _call++;
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (failAtCall == _call)
            {
                return OperationResult.Failure<MemoryScanResult>(
                    new ApplicationError("scan.failed", "scripted failure", Retryable: true));
            }

            int count = _call <= counts.Length ? counts[_call - 1] : 0;
            return OperationResult.Success(new MemoryScanResult(
                DateTimeOffset.UtcNow,
                baseAddress,
                RegionsScanned: 1,
                BytesScanned: 4096,
                Candidates: [],
                TotalMatchesBeforeTruncation: count));
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
}
