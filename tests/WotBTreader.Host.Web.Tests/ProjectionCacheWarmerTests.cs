using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Tests;

[TestClass]
public sealed class ProjectionCacheWarmerTests
{
    private static readonly BattleSessionId SessionId = new(new Guid("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    public async Task StartAsync_WarmsMostRecentSession()
    {
        ReplayDecodeProjection projection = NewProjection(SessionId);
        var cache = new ProjectionCache(capacity: 2);
        var sessions = new FakeSessionQueryRepository(projection);
        var warmer = new ProjectionCacheWarmer(
            sessions,
            cache,
            NullLogger<ProjectionCacheWarmer>.Instance);

        await warmer.StartAsync(CancellationToken.None);

        // BackgroundService.StartAsync fires ExecuteAsync without awaiting it,
        // so wait for the warm to land before stopping (StopAsync cancels the
        // warmer's token, which would abort an in-flight warm).
        Assert.IsTrue(await WaitForWarmAsync(cache), "Cache never warmed.");
        await warmer.StopAsync(CancellationToken.None);
        Assert.IsTrue(cache.TryGet(SessionId, out ReplayDecodeProjection? cached));
        Assert.HasCount(2, cached!.Positions);
    }

    [TestMethod]
    public async Task StartAsync_NoSessions_SkipsWarm()
    {
        var cache = new ProjectionCache(capacity: 2);
        var sessions = new FakeSessionQueryRepository(projection: null);
        var warmer = new ProjectionCacheWarmer(
            sessions,
            cache,
            NullLogger<ProjectionCacheWarmer>.Instance);

        await warmer.StartAsync(CancellationToken.None);
        await warmer.StopAsync(CancellationToken.None);

        Assert.IsFalse(cache.TryGet(SessionId, out _));
    }

    [TestMethod]
    public async Task StartAsync_StorageNotReady_RetriesThenRecovers()
    {
        ReplayDecodeProjection projection = NewProjection(SessionId);
        var cache = new ProjectionCache(capacity: 2);
        var sessions = new FlakySessionQueryRepository(projection, failuresBeforeSuccess: 2);
        var warmer = new ProjectionCacheWarmer(
            sessions,
            cache,
            NullLogger<ProjectionCacheWarmer>.Instance);

        await warmer.StartAsync(CancellationToken.None);

        // Two failures (storage not ready), then the third attempt succeeds
        // after the 250ms/500ms retry delays.
        Assert.IsTrue(await WaitForWarmAsync(cache), "Cache never warmed after retries.");
        await warmer.StopAsync(CancellationToken.None);
        Assert.IsTrue(cache.TryGet(SessionId, out ReplayDecodeProjection? cached));
        Assert.HasCount(2, cached!.Positions);
    }

    private static async Task<bool> WaitForWarmAsync(ProjectionCache cache)
    {
        for (int i = 0; i < 200; i++)
        {
            if (cache.TryGet(SessionId, out _))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static ReplayDecodeProjection NewProjection(BattleSessionId sessionId)
    {
        DecodeRun decodeRun = new(
            DecodeRunId.New(),
            SourceArtifactId.New(),
            DecoderId: "strict",
            DecoderVersion: "1",
            SchemaVersion: "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Positions,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            FailureCode: null,
            FailureSummary: null);
        BattleSession session = new(
            sessionId,
            decodeRun.Id,
            GameVersion: "11.18.0.7",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: null,
            ViewpointParticipantId: null,
            SchemaVersion: "1");
        PositionSample sample = new(
            PositionSampleId.New(),
            sessionId,
            ParticipantId: null,
            EntityId: 1,
            Sequence: 1,
            ReplayTime: TimeSpan.Zero,
            RawX: 0,
            RawY: 0,
            RawZ: 0,
            NormalizedX: null,
            NormalizedY: null,
            RawCoordinateSpace: CoordinateSpace.ReplayRaw,
            NormalizedCoordinateSpace: null,
            Evidence: new EvidenceReference(
                SourceArtifactId.New(),
                "data.wotreplay",
                Offset: 0,
                Length: 1,
                new ContentHash(new string('c', ContentHash.Sha256HexLength))),
            Yaw: 0,
            Pitch: null,
            Roll: null);
        return new ReplayDecodeProjection(
            decodeRun,
            session,
            Participants: [],
            Positions: [sample, sample with { ReplayTime = TimeSpan.FromSeconds(1) }],
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private sealed class FakeSessionQueryRepository(ReplayDecodeProjection? projection)
        : ISessionQueryRepository
    {
        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            DecodeRunSummary? summary = projection is null
                ? null
                : new DecodeRunSummary(
                    projection.DecodeRun,
                    projection.Session,
                    projection.Participants.Count,
                    projection.Positions.Count,
                    projection.Events.Count,
                    projection.RawRecords.Count);
            return ValueTask.FromResult<IReadOnlyList<DecodeRunSummary>>(
                summary is null ? [] : [summary]);
        }

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                projection is null
                    ? OperationResult.Failure<ReplayDecodeProjection>(
                        new ApplicationError("test.missing", "No projection."))
                    : OperationResult.Success(projection));

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }

    private sealed class FlakySessionQueryRepository(
        ReplayDecodeProjection projection,
        int failuresBeforeSuccess) : ISessionQueryRepository
    {
        private int _failures;

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            if (_failures < failuresBeforeSuccess)
            {
                _failures++;
                throw new InvalidOperationException("Storage not ready yet.");
            }

            return new FakeSessionQueryRepository(projection).ListAsync(offset, limit, cancellationToken);
        }

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            new FakeSessionQueryRepository(projection).GetProjectionAsync(battleSessionId, cancellationToken);

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }
}
