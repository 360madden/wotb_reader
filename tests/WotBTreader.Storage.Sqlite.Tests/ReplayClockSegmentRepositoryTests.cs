using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class ReplayClockSegmentRepositoryTests
{
    [TestMethod]
    public async Task AppendRoundTripsOrderedMonotonicSegments()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);
        DateTimeOffset anchor = DateTimeOffset.UtcNow.AddMinutes(-2);
        ReplayClockSegment first = Segment(
            session.Id,
            sequence: 0,
            anchor,
            TimeSpan.Zero,
            TelemetrySourceKind.NativeGameLog);
        ReplayClockSegment second = Segment(
            session.Id,
            sequence: 1,
            anchor.AddSeconds(10),
            TimeSpan.FromSeconds(9.8),
            TelemetrySourceKind.Manual);

        ReplayClockSegment storedFirst = StorageTestScope.Success(
            await scope.ClockSegments.AppendAsync(first, CancellationToken.None));
        ReplayClockSegment storedSecond = StorageTestScope.Success(
            await scope.ClockSegments.AppendAsync(second, CancellationToken.None));
        IReadOnlyList<ReplayClockSegment> listed = StorageTestScope.Success(
            await scope.ClockSegments.ListAsync(session.Id, CancellationToken.None));

        Assert.AreEqual(first, storedFirst);
        Assert.AreEqual(second, storedSecond);
        CollectionAssert.AreEqual(new[] { first, second }, listed.ToArray());

        ReplayClockSegment repeated = StorageTestScope.Success(
            await scope.ClockSegments.AppendAsync(second, CancellationToken.None));
        Assert.AreEqual(second, repeated);
    }

    [TestMethod]
    public async Task AppendRejectsSequenceSourceAndReplayRegressions()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);
        DateTimeOffset anchor = DateTimeOffset.UtcNow.AddMinutes(-1);
        ReplayClockSegment first = Segment(
            session.Id,
            sequence: 5,
            anchor,
            TimeSpan.FromSeconds(20),
            TelemetrySourceKind.NativeGameLog);
        StorageTestScope.Success(
            await scope.ClockSegments.AppendAsync(first, CancellationToken.None));

        ReplayClockSegment[] regressions =
        [
            Segment(
                session.Id,
                sequence: 5,
                anchor.AddSeconds(1),
                TimeSpan.FromSeconds(21),
                TelemetrySourceKind.Manual),
            Segment(
                session.Id,
                sequence: 6,
                anchor,
                TimeSpan.FromSeconds(21),
                TelemetrySourceKind.Manual),
            Segment(
                session.Id,
                sequence: 6,
                anchor.AddSeconds(1),
                TimeSpan.FromSeconds(19),
                TelemetrySourceKind.Manual),
        ];

        foreach (ReplayClockSegment regression in regressions)
        {
            OperationResult<ReplayClockSegment> result =
                await scope.ClockSegments.AppendAsync(regression, CancellationToken.None);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("storage.clock_not_monotonic", result.Error?.Code);
        }

        IReadOnlyList<ReplayClockSegment> listed = StorageTestScope.Success(
            await scope.ClockSegments.ListAsync(session.Id, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { first }, listed.ToArray());
    }

    [TestMethod]
    public async Task ConcurrentAppendSerializesMonotonicDecision()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);
        DateTimeOffset anchor = DateTimeOffset.UtcNow.AddMinutes(-1);
        ReplayClockSegment first = Segment(
            session.Id,
            sequence: 0,
            anchor,
            TimeSpan.Zero,
            TelemetrySourceKind.NativeGameLog);
        StorageTestScope.Success(
            await scope.ClockSegments.AppendAsync(first, CancellationToken.None));
        ReplayClockSegment candidateA = Segment(
            session.Id,
            sequence: 1,
            anchor.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            TelemetrySourceKind.Manual);
        ReplayClockSegment candidateB = Segment(
            session.Id,
            sequence: 1,
            anchor.AddSeconds(6),
            TimeSpan.FromSeconds(6),
            TelemetrySourceKind.Manual);

        OperationResult<ReplayClockSegment>[] results = await Task.WhenAll(
            scope.ClockSegments.AppendAsync(candidateA, CancellationToken.None).AsTask(),
            scope.ClockSegments.AppendAsync(candidateB, CancellationToken.None).AsTask());

        Assert.AreEqual(1, results.Count(result => result.IsSuccess));
        Assert.AreEqual(
            1,
            results.Count(result => result.Error?.Code == "storage.clock_not_monotonic"));
        IReadOnlyList<ReplayClockSegment> listed = StorageTestScope.Success(
            await scope.ClockSegments.ListAsync(session.Id, CancellationToken.None));
        Assert.HasCount(2, listed);
        Assert.AreEqual(0, listed[0].Sequence);
        Assert.AreEqual(1, listed[1].Sequence);
    }

    [TestMethod]
    public async Task ListMissingSessionReturnsStableNotFound()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();

        OperationResult<IReadOnlyList<ReplayClockSegment>> result =
            await scope.ClockSegments.ListAsync(BattleSessionId.New(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("storage.not_found", result.Error?.Code);
    }

    private static async ValueTask<BattleSession> CreateSessionAsync(StorageTestScope scope)
    {
        SourceArtifact artifact = await scope.ImportAsync(
            $"{Guid.NewGuid():N}.wotbreplay",
            "clock session evidence"u8.ToArray());
        DecodeRun running = new(
            DecodeRunId.New(),
            artifact.Id,
            "clock-test",
            "1",
            "1",
            DecodeRunStatus.Running,
            ReplayCapability.Metadata,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAtUtc: null,
            FailureCode: null,
            FailureSummary: null);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        BattleSession session = new(
            BattleSessionId.New(),
            running.Id,
            "11.18.0.7",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: null,
            ViewpointParticipantId: null,
            SchemaVersion: "1");
        ReplayDecodeProjection projection = new(
            running with
            {
                Status = DecodeRunStatus.Succeeded,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            },
            session,
            Participants: [],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
        StorageTestScope.Success(
            await scope.DecodeRuns.CommitAsync(projection, CancellationToken.None));
        return session;
    }

    private static ReplayClockSegment Segment(
        BattleSessionId sessionId,
        long sequence,
        DateTimeOffset sourceAnchor,
        TimeSpan replayAnchor,
        TelemetrySourceKind source) =>
        new(
            ReplayClockSegmentId.New(),
            sessionId,
            sequence,
            sourceAnchor,
            replayAnchor,
            Speed: 1,
            source,
            TimeSpan.FromMilliseconds(25),
            sourceAnchor);
}
