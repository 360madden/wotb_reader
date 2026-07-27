using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Endpoints;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// Covers the read API's paging bounds, error mapping, payload capping, and the
/// fields it deliberately does or does not expose.
/// </summary>
[TestClass]
public sealed class ReadApiEndpointsTests
{
    [TestMethod]
    public async Task SessionsPageIsReturnedWithItsRequestedWindow()
    {
        FakeSessionQueries sessions = new([Summary()]);

        IResult result = await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset: 5,
            limit: 25,
            TestContext.CancellationToken);

        SessionPageResponse page = Value<SessionPageResponse>(result);
        Assert.AreEqual(5, page.Offset);
        Assert.AreEqual(25, page.Limit);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual((5, 25), sessions.LastRequest);
    }

    [TestMethod]
    public async Task SessionsAppliesTheDefaultWindowWhenNoneIsSupplied()
    {
        FakeSessionQueries sessions = new([]);

        await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset: null,
            limit: null,
            TestContext.CancellationToken);

        Assert.AreEqual((0, ReadApiEndpoints.DefaultPageSize), sessions.LastRequest);
    }

    [TestMethod]
    [DataRow(-1, 10)]
    [DataRow(0, 0)]
    [DataRow(0, -5)]
    [DataRow(0, ReadApiEndpoints.MaximumPageSize + 1)]
    public async Task OutOfRangePagingIsRejectedWithoutQueryingStorage(int offset, int limit)
    {
        FakeSessionQueries sessions = new([]);

        IResult result = await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset,
            limit,
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.IsNull(sessions.LastRequest, "An invalid window must never reach storage.");
    }

    [TestMethod]
    public async Task PositionSeriesIsCappedAndReportsTruncation()
    {
        int total = ReadApiEndpoints.MaximumPositionSamples + 25;
        FakeSessionQueries sessions = new([], Projection(positionCount: total));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        SessionDetailResponse detail = Value<SessionDetailResponse>(result);
        Assert.HasCount(ReadApiEndpoints.MaximumPositionSamples, detail.Positions);
        Assert.IsTrue(detail.PositionsTruncated);
        Assert.AreEqual(total, detail.TotalPositionCount);
    }

    [TestMethod]
    public async Task ShortPositionSeriesIsNotMarkedTruncated()
    {
        FakeSessionQueries sessions = new([], Projection(positionCount: 3));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        SessionDetailResponse detail = Value<SessionDetailResponse>(result);
        Assert.HasCount(3, detail.Positions);
        Assert.IsFalse(detail.PositionsTruncated);
    }

    [TestMethod]
    public async Task MissingSessionBecomesANotFoundProblem()
    {
        FakeSessionQueries sessions = new(
            [],
            error: new ApplicationError("storage.session.not_found", "No such session."));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [TestMethod]
    [DataRow("storage.session.not_found", StatusCodes.Status404NotFound)]
    [DataRow("storage.conflict", StatusCodes.Status409Conflict)]
    [DataRow("storage.busy", StatusCodes.Status409Conflict)]
    [DataRow("request.invalid", StatusCodes.Status400BadRequest)]
    [DataRow("replay.malformed", StatusCodes.Status400BadRequest)]
    [DataRow("decoder.unsupported", StatusCodes.Status501NotImplemented)]
    [DataRow("internal.unknown", StatusCodes.Status500InternalServerError)]
    public void ErrorCodesMapToStableStatusCodes(string code, int expected) =>
        Assert.AreEqual(expected, ReadApiEndpoints.MapStatusCode(code));

    [TestMethod]
    public void ParticipantResponseNeverCarriesTheAccountIdentifier()
    {
        Participant participant = ParticipantFixture() with { AccountId = 987654321 };

        ParticipantResponse response = ParticipantResponse.From(participant);

        Assert.AreEqual("pilot", response.PlayerName);
        Assert.IsFalse(
            response.ToString().Contains("987654321", StringComparison.Ordinal),
            "The durable account identifier must not reach an API client.");
    }

    [TestMethod]
    public void BotStatusIsPassedThroughWithItsConfidenceAndNeverInferred()
    {
        Participant participant = ParticipantFixture() with
        {
            BotStatus = BotStatus.Unknown,
            BotStatusConfidence = EvidenceConfidence.Unknown,
        };

        ParticipantResponse response = ParticipantResponse.From(participant);

        Assert.AreEqual("Unknown", response.BotStatus);
        Assert.AreEqual("Unknown", response.BotStatusConfidence);
    }

    [TestMethod]
    public void CapabilityFlagsAreExpandedIntoNames()
    {
        DecodeRun run = DecodeRunFixture() with
        {
            Capabilities = ReplayCapability.Metadata | ReplayCapability.Positions,
        };

        DecodeRunResponse response = DecodeRunResponse.From(run);

        Assert.HasCount(2, response.Capabilities);
        Assert.Contains("Metadata", response.Capabilities);
        Assert.Contains("Positions", response.Capabilities);
    }

    [TestMethod]
    public void IdentifiersAreRenderedAsPlainStrings()
    {
        DecodeRunResponse response = DecodeRunResponse.From(DecodeRunFixture());

        Assert.IsTrue(
            Guid.TryParse(response.DecodeRunId, out _),
            "Clients must receive an identifier they can use without unwrapping an object.");
    }

    public TestContext TestContext { get; set; } = null!;

    private static EvidenceReference EvidenceFixture() =>
        new(
            SourceArtifactId.New(),
            "data.wotreplay",
            0,
            1,
            new ContentHash(new string('a', 64)));

    private static T Value<T>(IResult result)
    {
        Assert.IsInstanceOfType<Ok<T>>(result);
        T? value = ((Ok<T>)result).Value;
        Assert.IsNotNull(value);
        return value;
    }

    private static int StatusOf(IResult result)
    {
        Assert.IsInstanceOfType<ProblemHttpResult>(result);
        return ((ProblemHttpResult)result).StatusCode;
    }

    private static DecodeRun DecodeRunFixture() =>
        new(
            DecodeRunId.New(),
            SourceArtifactId.New(),
            "wotb-11.18-strict",
            "0.1.0",
            "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Metadata,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            null,
            null);

    private static Participant ParticipantFixture() =>
        new(
            ParticipantId.New(),
            BattleSessionId.New(),
            AccountId: null,
            EntityId: 100,
            TeamNumber: 1,
            PlayerName: "pilot",
            ClanTag: "TAG",
            VehicleCompactDescriptor: 2897,
            TankId: null,
            TankName: null,
            TankClass.Unknown,
            BotStatus.Unknown,
            EvidenceConfidence.Unknown,
            EvidenceFixture());

    private static DecodeRunSummary Summary() =>
        new(DecodeRunFixture(), Session: null, 2, 4, 6, 8);

    private static ReplayDecodeProjection Projection(int positionCount)
    {
        BattleSessionId sessionId = BattleSessionId.New();
        PositionSample[] positions = [.. Enumerable.Range(0, positionCount).Select(index =>
            new PositionSample(
                PositionSampleId.New(),
                sessionId,
                ParticipantId: null,
                EntityId: 100,
                index,
                TimeSpan.FromMilliseconds(index),
                index,
                0,
                0,
                null,
                null,
                CoordinateSpace.ReplayRaw,
                null,
                EvidenceFixture()))];

        return new ReplayDecodeProjection(
            DecodeRunFixture(),
            Session: null,
            Participants: [],
            positions,
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private sealed class FakeSessionQueries(
        IReadOnlyList<DecodeRunSummary> page,
        ReplayDecodeProjection? projection = null,
        ApplicationError? error = null) : ISessionQueryRepository
    {
        public (int Offset, int Limit)? LastRequest { get; private set; }

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            LastRequest = (offset, limit);
            return ValueTask.FromResult(page);
        }

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(projection is null
                ? OperationResult.Failure<ReplayDecodeProjection>(
                    error ?? new ApplicationError("storage.session.not_found", "No such session."))
                : OperationResult.Success(projection));
    }
}
