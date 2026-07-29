using WotBTreader.ApiContracts;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Host.Web.Endpoints;
using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Tests;

[TestClass]
public sealed class DashboardReadClientTests
{
    [TestMethod]
    public async Task ListSessionsReturnsMappedPage()
    {
        DecodeRunSummary summary = Summary();
        DashboardReadClient client = CreateClient(new FakeSessions([summary]));

        SessionPageResponse page = await client.ListSessionsAsync(
            0,
            10,
            TestContext.CancellationToken);

        Assert.AreEqual(0, page.Offset);
        Assert.AreEqual(10, page.Limit);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual(summary.DecodeRun.Id.Value.ToString("D"), page.Items[0].DecodeRun.DecodeRunId);
        Assert.AreEqual(summary.ParticipantCount, page.Items[0].ParticipantCount);
    }

    [TestMethod]
    public async Task ListSessionsRejectsAnOversizeLimit()
    {
        DashboardReadClient client = CreateClient(new FakeSessions([]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ListSessionsAsync(
                0,
                ReadApiEndpoints.MaximumPageSize + 1,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GetSessionReturnsNullWhenMissing()
    {
        DashboardReadClient client = CreateClient(new FakeSessions([]));

        SessionDetailResponse? detail = await client.GetSessionAsync(
            Guid.NewGuid(),
            TestContext.CancellationToken);

        Assert.IsNull(detail);
    }

    [TestMethod]
    public async Task GetSessionCapsPositionsLikeTheHttpApi()
    {
        int total = ReadApiEndpoints.MaximumPositionSamples + 3;
        DashboardReadClient client = CreateClient(
            new FakeSessions([], Projection(total)));

        SessionDetailResponse? detail = await client.GetSessionAsync(
            Guid.NewGuid(),
            TestContext.CancellationToken);

        Assert.IsNotNull(detail);
        Assert.HasCount(ReadApiEndpoints.MaximumPositionSamples, detail.Positions);
        Assert.IsTrue(detail.PositionsTruncated);
        Assert.AreEqual(total, detail.TotalPositionCount);
    }

    [TestMethod]
    public async Task GetDoctorForwardsTheReport()
    {
        DoctorReport report = new(
            "1",
            DateTimeOffset.UnixEpoch,
            [new DiagnosticCheck("storage", "pass", "ok", true, new Dictionary<string, string>())]);
        DashboardReadClient client = CreateClient(new FakeSessions([]), new FakeDoctor(report));

        DoctorReport actual = await client.GetDoctorAsync(TestContext.CancellationToken);

        Assert.AreEqual(report, actual);
    }

    public TestContext TestContext { get; set; } = null!;

    private static DashboardReadClient CreateClient(
        FakeSessions sessions,
        FakeDoctor? doctor = null,
        FakeComparisons? comparisons = null) =>
        new(sessions,
            doctor ?? new FakeDoctor(
                new DoctorReport("1", DateTimeOffset.UnixEpoch, [])),
            comparisons ?? new FakeComparisons());

    private static EvidenceReference Evidence() =>
        new(SourceArtifactId.New(), "data.wotreplay", 0, 1, new ContentHash(new string('a', 64)));

    private static DecodeRun DecodeRun() =>
        new(
            DecodeRunId.New(),
            SourceArtifactId.New(),
            "wotb-11.18-strict",
            "0.1.0",
            "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Metadata,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            null,
            null);

    private static DecodeRunSummary Summary() =>
        new(DecodeRun(), Session: null, 2, 4, 6, 8);

    private static ReplayDecodeProjection Projection(int positionCount)
    {
        BattleSessionId sessionId = BattleSessionId.New();
        PositionSample[] positions =
        [
            .. Enumerable.Range(0, positionCount).Select(index =>
                new PositionSample(
                    PositionSampleId.New(),
                    sessionId,
                    null,
                    100,
                    index,
                    TimeSpan.FromMilliseconds(index),
                    index,
                    0,
                    0,
                    null,
                    null,
                    CoordinateSpace.ReplayRaw,
                    null,
                    Evidence()))
        ];

        return new ReplayDecodeProjection(
            DecodeRun(),
            null,
            [],
            positions,
            [],
            [],
            []);
    }

    private sealed class FakeSessions(
        IReadOnlyList<DecodeRunSummary> page,
        ReplayDecodeProjection? projection = null) : ISessionQueryRepository
    {
        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(page);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(projection is null
                ? OperationResult.Failure<ReplayDecodeProjection>(
                    new ApplicationError("storage.not_found", "missing"))
                : OperationResult.Success(projection));

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }

    private sealed class FakeDoctor(DoctorReport report) : IDoctorService
    {
        public ValueTask<DoctorReport> RunAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(report);
    }

    private sealed class FakeComparisons(
        IReadOnlyList<ComparisonRun> runs = null!,
        TelemetryComparison? comparison = null) : IComparisonRunRepository
    {
        public ValueTask<IReadOnlyList<ComparisonRun>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ComparisonRun>>(runs ?? []);

        public ValueTask<OperationResult<TelemetryComparison>> AddAsync(
            TelemetryComparison comparison,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(comparison));

        public ValueTask<OperationResult<TelemetryComparison>> GetAsync(
            ComparisonRunId comparisonRunId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(comparison is null
                ? OperationResult.Failure<TelemetryComparison>(
                    new ApplicationError("storage.not_found", "missing"))
                : OperationResult.Success(comparison));
    }
}
