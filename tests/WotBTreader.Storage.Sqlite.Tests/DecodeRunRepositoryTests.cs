using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class DecodeRunRepositoryTests
{
    [TestMethod]
    public async Task CompleteProjectionRoundTripsAllEvidence()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact artifact = await scope.ImportAsync(
            "projection.wotbreplay",
            "full synthetic evidence payload"u8.ToArray());
        DecodeRun running = CreateRunningRun(artifact.Id);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        ReplayDecodeProjection expected = CreateCompleteProjection(running, artifact);

        DecodeRunSummary committed = StorageTestScope.Success(
            await scope.DecodeRuns.CommitAsync(expected, CancellationToken.None));
        OperationResult<ReplayDecodeProjection> query =
            await scope.Sessions.GetProjectionAsync(expected.Session!.Id, CancellationToken.None);
        ReplayDecodeProjection actual = StorageTestScope.Success(query);

        Assert.AreEqual(DecodeRunStatus.Succeeded, committed.DecodeRun.Status);
        Assert.AreEqual(1, committed.ParticipantCount);
        Assert.AreEqual(1, committed.PositionCount);
        Assert.AreEqual(1, committed.EventCount);
        Assert.AreEqual(1, committed.RawRecordCount);
        Assert.AreEqual(expected.DecodeRun, actual.DecodeRun);
        Assert.AreEqual(expected.Session, actual.Session);
        CollectionAssert.AreEqual(expected.Participants.ToArray(), actual.Participants.ToArray());
        CollectionAssert.AreEqual(expected.Positions.ToArray(), actual.Positions.ToArray());
        CollectionAssert.AreEqual(expected.Events.ToArray(), actual.Events.ToArray());
        CollectionAssert.AreEqual(expected.RawRecords.ToArray(), actual.RawRecords.ToArray());
        CollectionAssert.AreEqual(expected.Warnings.ToArray(), actual.Warnings.ToArray());

        IReadOnlyList<DecodeRunSummary> listed =
            await scope.Sessions.ListAsync(0, 25, CancellationToken.None);
        Assert.HasCount(1, listed);
        Assert.AreEqual(committed, listed[0]);

        OperationResult<DecodeRunSummary> repeated =
            await scope.DecodeRuns.CommitAsync(expected, CancellationToken.None);
        Assert.IsFalse(repeated.IsSuccess);
        Assert.AreEqual("storage.conflict", repeated.Error?.Code);
    }

    [TestMethod]
    public async Task ConstraintFailureRollsBackWholeProjectionAndCanBeRetried()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact artifact = await scope.ImportAsync(
            "rollback.wotbreplay",
            "rollback evidence"u8.ToArray());
        DecodeRun running = CreateRunningRun(artifact.Id);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        DecodeRun completed = running with
        {
            Status = DecodeRunStatus.Succeeded,
            Capabilities = ReplayCapability.UnknownRecordsPreserved,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        EvidenceReference evidence = StorageTestScope.Evidence(artifact);
        ReplayDecodeProjection invalid = new(
            completed,
            Session: null,
            Participants: [],
            Positions: [],
            Events: [],
            RawRecords:
            [
                new RawRecord(
                    RawRecordId.New(),
                    running.Id,
                    7,
                    "unknown",
                    TimeSpan.FromSeconds(1),
                    evidence,
                    """{"wireType":2}"""),
                new RawRecord(
                    RawRecordId.New(),
                    running.Id,
                    7,
                    "unknown",
                    TimeSpan.FromSeconds(2),
                    evidence,
                    """{"wireType":5}"""),
            ],
            Warnings: []);

        OperationResult<DecodeRunSummary> failedCommit =
            await scope.DecodeRuns.CommitAsync(invalid, CancellationToken.None);
        Assert.IsFalse(failedCommit.IsSuccess);
        Assert.AreEqual("storage.conflict", failedCommit.Error?.Code);

        DecodeRunSummary afterFailure = StorageTestScope.Success(
            await scope.DecodeRuns.GetAsync(running.Id, CancellationToken.None));
        Assert.AreEqual(DecodeRunStatus.Running, afterFailure.DecodeRun.Status);
        Assert.AreEqual(0, afterFailure.RawRecordCount);

        ReplayDecodeProjection valid = invalid with
        {
            RawRecords = [invalid.RawRecords[0]],
        };
        DecodeRunSummary committed = StorageTestScope.Success(
            await scope.DecodeRuns.CommitAsync(valid, CancellationToken.None));
        Assert.AreEqual(1, committed.RawRecordCount);
        Assert.AreEqual(DecodeRunStatus.Succeeded, committed.DecodeRun.Status);
    }

    [TestMethod]
    public async Task FailureTransitionIsIdempotentButImmutable()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact artifact = await scope.ImportAsync(
            "failure.wotbreplay",
            "failure evidence"u8.ToArray());
        DecodeRun running = CreateRunningRun(artifact.Id);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        DateTimeOffset completed = DateTimeOffset.UtcNow;

        DecodeRun first = StorageTestScope.Success(
            await scope.DecodeRuns.FailAsync(
                running.Id,
                DecodeRunStatus.Unsupported,
                "replay.unsupported_version",
                "The replay version is unsupported.",
                completed,
                CancellationToken.None));
        DecodeRun repeated = StorageTestScope.Success(
            await scope.DecodeRuns.FailAsync(
                running.Id,
                DecodeRunStatus.Unsupported,
                "replay.unsupported_version",
                "The replay version is unsupported.",
                completed.AddMinutes(1),
                CancellationToken.None));
        OperationResult<DecodeRun> mutation = await scope.DecodeRuns.FailAsync(
            running.Id,
            DecodeRunStatus.Failed,
            "replay.failed",
            "A different terminal result.",
            completed,
            CancellationToken.None);

        Assert.AreEqual(first, repeated);
        Assert.IsFalse(mutation.IsSuccess);
        Assert.AreEqual("storage.conflict", mutation.Error?.Code);
    }

    private static DecodeRun CreateRunningRun(SourceArtifactId artifactId) =>
        new(
            DecodeRunId.New(),
            artifactId,
            "wotb-11.18",
            "0.1.0",
            "1",
            DecodeRunStatus.Running,
            ReplayCapability.Metadata,
            DateTimeOffset.UtcNow,
            CompletedAtUtc: null,
            FailureCode: null,
            FailureSummary: null);

    private static ReplayDecodeProjection CreateCompleteProjection(
        DecodeRun running,
        SourceArtifact artifact)
    {
        DecodeRun completed = running with
        {
            Status = DecodeRunStatus.Succeeded,
            Capabilities =
                ReplayCapability.Metadata |
                ReplayCapability.Participants |
                ReplayCapability.Teams |
                ReplayCapability.Positions |
                ReplayCapability.UnknownRecordsPreserved,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        BattleSessionId sessionId = BattleSessionId.New();
        ParticipantId participantId = ParticipantId.New();
        EvidenceReference evidence = StorageTestScope.Evidence(artifact, length: 8);
        BattleSession session = new(
            sessionId,
            completed.Id,
            "11.18.0.7",
            "arena-synthetic",
            "map-1",
            "Synthetic Map",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            TimeSpan.FromMinutes(4),
            participantId,
            "1");
        Participant participant = new(
            participantId,
            sessionId,
            42,
            7001,
            1,
            "SyntheticPlayer",
            "TEST",
            12345,
            "gb:churchill_i",
            "Churchill I",
            TankClass.Heavy,
            BotStatus.Unknown,
            EvidenceConfidence.Unknown,
            new BattleStats(
                CreditsEarned: 1200,
                BaseXp: 850,
                Shots: 15,
                HitsDealt: 9,
                PenetrationsDealt: 5,
                DamageDealt: 2340,
                DamageAssisted1: 300,
                DamageAssisted2: 120,
                HitsReceived: 2,
                NonPenetratingHitsReceived: 1,
                PenetrationsReceived: 1,
                EnemiesDamaged: 3,
                EnemiesDestroyed: 1,
                VictoryPointsEarned: 40,
                VictoryPointsSeized: 20,
                MmRating: 2575.5f,
                DamageBlocked: 410),
            evidence);
        PositionSample position = new(
            PositionSampleId.New(),
            sessionId,
            participantId,
            7001,
            1,
            TimeSpan.FromSeconds(3.25),
            10.5,
            0.25,
            -8.75,
            0.4,
            0.6,
            CoordinateSpace.ReplayRaw,
            CoordinateSpace.MapNormalized,
            evidence);
        CanonicalEvent canonicalEvent = new(
            CanonicalEventId.New(),
            completed.Id,
            sessionId,
            1,
            CanonicalEventKind.Position,
            position.ReplayTime,
            participantId,
            7001,
            """{"x":10.5,"z":-8.75}""",
            EvidenceConfidence.Exact,
            evidence);
        RawRecord raw = new(
            RawRecordId.New(),
            completed.Id,
            1,
            "packet:10",
            position.ReplayTime,
            evidence,
            """{"packetType":10}""");
        return new ReplayDecodeProjection(
            completed,
            session,
            [participant],
            [position],
            [canonicalEvent],
            [raw],
            ["Synthetic warning retained."]);
    }
}
