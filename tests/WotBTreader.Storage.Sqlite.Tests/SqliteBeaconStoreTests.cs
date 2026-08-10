using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteBeaconStoreTests
{
    [TestMethod]
    public async Task AddGetRemoveRoundTripsPerSession()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);
        BattleSession other = await CreateSessionAsync(scope);

        OverlayBeacon flag = new("Flag A", 10, 20, 30, "#FFD700", null, null);
        await scope.Beacons.AddBeaconAsync(session.Id, flag, CancellationToken.None);
        await scope.Beacons.AddBeaconAsync(
            session.Id,
            new OverlayBeacon("Timed", 1, 2, 3, "#00FF00", TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)),
            CancellationToken.None);
        await scope.Beacons.AddBeaconAsync(other.Id, flag with { Name = "Other Flag" }, CancellationToken.None);

        IReadOnlyList<OverlayBeacon> mine = await scope.Beacons.GetBeaconsAsync(session.Id, CancellationToken.None);
        Assert.HasCount(2, mine);
        Assert.AreEqual("Flag A", mine[0].Name);
        Assert.AreEqual(10.0, mine[0].X, 1e-9);
        Assert.AreEqual(TimeSpan.FromSeconds(40), mine[1].VisibleFrom);
        Assert.AreEqual(TimeSpan.FromSeconds(60), mine[1].VisibleUntil);

        IReadOnlyList<OverlayBeacon> theirs = await scope.Beacons.GetBeaconsAsync(other.Id, CancellationToken.None);
        Assert.HasCount(1, theirs);
        Assert.AreEqual("Other Flag", theirs[0].Name);

        bool removed = await scope.Beacons.RemoveBeaconAsync(session.Id, "Flag A", CancellationToken.None);
        Assert.IsTrue(removed);
        bool removedAgain = await scope.Beacons.RemoveBeaconAsync(session.Id, "Flag A", CancellationToken.None);
        Assert.IsFalse(removedAgain);

        IReadOnlyList<OverlayBeacon> after = await scope.Beacons.GetBeaconsAsync(session.Id, CancellationToken.None);
        Assert.HasCount(1, after);
        Assert.AreEqual("Timed", after[0].Name);
    }

    [TestMethod]
    public async Task AddReplacesExistingBeaconWithSameName()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);

        await scope.Beacons.AddBeaconAsync(
            session.Id,
            new OverlayBeacon("Flag A", 10, 20, 30, "#FFD700", null, null),
            CancellationToken.None);
        await scope.Beacons.AddBeaconAsync(
            session.Id,
            new OverlayBeacon("Flag A", 11, 21, 31, "#00FF00", null, null),
            CancellationToken.None);

        IReadOnlyList<OverlayBeacon> beacons = await scope.Beacons.GetBeaconsAsync(session.Id, CancellationToken.None);
        Assert.HasCount(1, beacons);
        Assert.AreEqual(11.0, beacons[0].X, 1e-9);
        Assert.AreEqual("#00FF00", beacons[0].Color);
    }

    [TestMethod]
    public async Task BeaconsSurviveReopen()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        BattleSession session = await CreateSessionAsync(scope);
        await scope.Beacons.AddBeaconAsync(
            session.Id,
            new OverlayBeacon("Flag A", 10, 20, 30, "#FFD700", null, null),
            CancellationToken.None);

        // Reopen the same database through a fresh scope at the same root
        // (the first scope stays alive — DisposeAsync deletes the root).
        await using StorageTestScope reopened = await StorageTestScope.CreateAsync(root: scope.Root);
        IReadOnlyList<OverlayBeacon> beacons = await reopened.Beacons.GetBeaconsAsync(session.Id, CancellationToken.None);
        Assert.HasCount(1, beacons);
        Assert.AreEqual("Flag A", beacons[0].Name);
    }

    private static async ValueTask<BattleSession> CreateSessionAsync(StorageTestScope scope)
    {
        SourceArtifact artifact = await scope.ImportAsync(
            $"{Guid.NewGuid():N}.wotbreplay",
            "beacon session evidence"u8.ToArray());
        DecodeRun running = new(
            DecodeRunId.New(),
            artifact.Id,
            "beacon-test",
            "1",
            "1",
            DecodeRunStatus.Running,
            ReplayCapability.Metadata,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAtUtc: null,
            FailureCode: null,
            FailureSummary: null);
        StorageTestScope.Success(await scope.DecodeRuns.StartAsync(running, CancellationToken.None));
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
        StorageTestScope.Success(await scope.DecodeRuns.CommitAsync(projection, CancellationToken.None));
        return session;
    }
}
