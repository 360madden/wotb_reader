using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteHpGroundTruthProviderTests
{
    [TestMethod]
    public async Task GetAsync_ReturnsDamageAndDestroyedEventsOrderedWithParsedValues()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact artifact = await scope.ImportAsync(
            "damage-battle.wotbreplay",
            "damage event synthetic evidence payload"u8.ToArray());
        DecodeRun running = CreateRunningRun(artifact.Id);
        StorageTestScope.Success(
            await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
        ReplayDecodeProjection projection = CreateDamageProjection(running, artifact);
        StorageTestScope.Success(
            await scope.DecodeRuns.CommitAsync(projection, CancellationToken.None));

        var provider = new SqliteHpGroundTruthProvider(scope.Context);
        OperationResult<HpGroundTruth> result = await provider.GetAsync(
            projection.Session!.Id,
            CancellationToken.None);

        HpGroundTruth groundTruth = StorageTestScope.Success(result);
        Assert.AreEqual(TimeSpan.FromMinutes(4), groundTruth.Duration);
        Assert.HasCount(3, groundTruth.Events);

        // Ordered by replay time: 1s damage, 2s damage, 3s destroyed.
        HpDamageEvent first = groundTruth.Events[0];
        Assert.AreEqual(CanonicalEventKind.Damage, first.Kind);
        Assert.AreEqual(7001L, first.EntityId);
        Assert.AreEqual(TimeSpan.FromSeconds(1), first.ReplayTime);
        Assert.AreEqual(450, first.Damage);
        Assert.AreEqual(7002L, first.AttackerEntityId);
        Assert.IsNotNull(first.ParticipantId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.ValuesJson));

        HpDamageEvent second = groundTruth.Events[1];
        Assert.AreEqual(CanonicalEventKind.Damage, second.Kind);
        Assert.AreEqual(120, second.Damage);
        Assert.AreEqual(TimeSpan.FromSeconds(2), second.ReplayTime);

        HpDamageEvent destroyed = groundTruth.Events[2];
        Assert.AreEqual(CanonicalEventKind.Destroyed, destroyed.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(3), destroyed.ReplayTime);
        // No "damage" key in the destroyed event's values -> stays null.
        Assert.IsNull(destroyed.Damage);
        // The attacker key IS present -> parsed.
        Assert.AreEqual(7002L, destroyed.AttackerEntityId);
    }

    [TestMethod]
    public async Task GetAsync_UnknownSession_FailsClosed()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();

        var provider = new SqliteHpGroundTruthProvider(scope.Context);
        OperationResult<HpGroundTruth> result = await provider.GetAsync(
            BattleSessionId.New(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNotNull(result.Error);
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

    private static ReplayDecodeProjection CreateDamageProjection(
        DecodeRun running,
        SourceArtifact artifact)
    {
        DecodeRun completed = running with
        {
            Status = DecodeRunStatus.Succeeded,
            Capabilities =
                ReplayCapability.Metadata |
                ReplayCapability.Participants |
                ReplayCapability.Damage,
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

        List<CanonicalEvent> events =
        [
            new CanonicalEvent(
                CanonicalEventId.New(),
                completed.Id,
                sessionId,
                1,
                CanonicalEventKind.Damage,
                TimeSpan.FromSeconds(1),
                participantId,
                7001,
                """{"attackerEntityId":7002,"victimEntityId":7001,"damage":450}""",
                EvidenceConfidence.Exact,
                evidence),
            new CanonicalEvent(
                CanonicalEventId.New(),
                completed.Id,
                sessionId,
                2,
                CanonicalEventKind.Damage,
                TimeSpan.FromSeconds(2),
                participantId,
                7001,
                """{"attackerEntityId":7003,"victimEntityId":7001,"damage":120}""",
                EvidenceConfidence.Exact,
                evidence),
            new CanonicalEvent(
                CanonicalEventId.New(),
                completed.Id,
                sessionId,
                3,
                CanonicalEventKind.Destroyed,
                TimeSpan.FromSeconds(3),
                participantId,
                7001,
                """{"attackerEntityId":7002,"victimEntityId":7001}""",
                EvidenceConfidence.Exact,
                evidence),
        ];

        return new ReplayDecodeProjection(
            completed,
            session,
            [participant],
            [],
            events,
            [],
            ["Synthetic warning retained."]);
    }
}
