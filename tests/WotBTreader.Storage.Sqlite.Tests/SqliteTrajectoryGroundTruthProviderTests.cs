using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteTrajectoryGroundTruthProviderTests
{
    /// <summary>
    /// Regression: <c>Downsample</c> seeded <c>lastKeptTick</c> with
    /// <c>long.MinValue</c>; <c>tick - long.MinValue</c> overflows negative for
    /// every non-negative tick, so any battle with more than
    /// <c>MaximumSamplesPerEntity</c> (256) samples produced an EMPTY ground
    /// truth. That silently disabled the whole OD-048 correlation campaign on
    /// real battles (>~25s at 10 Hz) while synthetic fixtures stayed green.
    /// </summary>
    [TestMethod]
    public async Task LongBattleStillProducesGroundTruthWithEdgesIntact()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact artifact = await scope.ImportAsync(
            "long-battle.wotbreplay",
            "long battle synthetic evidence payload"u8.ToArray());
        DecodeRun running = CreateRunningRun(artifact.Id);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        ReplayDecodeProjection projection = CreateLongProjection(running, artifact, sampleCount: 300);
        StorageTestScope.Success(
            await scope.DecodeRuns.CommitAsync(projection, CancellationToken.None));

        var provider = new SqliteTrajectoryGroundTruthProvider(scope.Context);
        OperationResult<TrajectoryGroundTruth> result = await provider.GetAsync(
            projection.Session!.Id,
            CancellationToken.None);

        TrajectoryGroundTruth groundTruth = StorageTestScope.Success(result);
        Assert.HasCount(1, groundTruth.Entities);

        EntityTrajectory entity = groundTruth.Entities[0];
        // Downsampled, but not empty — the bug produced 0 samples.
        // Note: IsGreaterThanOrEqualTo(lowerBound, value) asserts value >= lowerBound.
        Assert.IsGreaterThanOrEqualTo(2, entity.Samples.Count);
        Assert.IsLessThan(300, entity.Samples.Count);
        // The window edges must survive downsampling: first tick and last tick.
        Assert.AreEqual(0L, entity.Samples[0].ReplayTimeTicks);
        Assert.AreEqual(299L * TrajectoryCorrelationScorer.ReplayClockTicksPerSecond,
            entity.Samples[^1].ReplayTimeTicks);
        // Monotone ticks so the scorer's binary search is valid.
        for (int index = 1; index < entity.Samples.Count; index++)
        {
            // IsGreaterThan(lowerBound, value) asserts value > lowerBound.
            Assert.IsGreaterThan(
                entity.Samples[index - 1].ReplayTimeTicks,
                entity.Samples[index].ReplayTimeTicks);
        }

        Assert.IsTrue(entity.IsViewpoint);
        Assert.AreEqual(TimeSpan.FromMinutes(4).Ticks, groundTruth.DurationTicks);
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

    private static ReplayDecodeProjection CreateLongProjection(
        DecodeRun running,
        SourceArtifact artifact,
        int sampleCount)
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

        // 10 Hz positions: a 300-sample battle spans 0 .. 29.9s at exactly
        // ReplayClockTicksPerSecond per real second.
        List<PositionSample> positions = [];
        List<CanonicalEvent> events = [];
        for (int index = 0; index < sampleCount; index++)
        {
            long replayTicks = (long)(index * TrajectoryCorrelationScorer.ReplayClockTicksPerSecond);
            TimeSpan replayTime = TimeSpan.FromTicks(replayTicks);
            double x = index * 0.5;
            PositionSample position = new(
                PositionSampleId.New(),
                sessionId,
                participantId,
                7001,
                index + 1,
                replayTime,
                x,
                0.25,
                -8.75,
                0.4,
                0.6,
                CoordinateSpace.ReplayRaw,
                CoordinateSpace.MapNormalized,
                evidence);
            positions.Add(position);
            events.Add(new CanonicalEvent(
                CanonicalEventId.New(),
                completed.Id,
                sessionId,
                index + 1,
                CanonicalEventKind.Position,
                replayTime,
                participantId,
                7001,
                """{"x":0,"z":-8.75}""",
                EvidenceConfidence.Exact,
                evidence));
        }

        return new ReplayDecodeProjection(
            completed,
            session,
            [participant],
            positions,
            events,
            [new RawRecord(
                RawRecordId.New(),
                completed.Id,
                1,
                "packet:10",
                TimeSpan.Zero,
                evidence,
                """{"packetType":10}""")],
            ["Synthetic warning retained."]);
    }
}
