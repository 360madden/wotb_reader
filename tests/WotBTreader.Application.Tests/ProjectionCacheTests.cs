using WotBTreader.Application.Replay;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class ProjectionCacheTests
{
    private static readonly BattleSessionId Session1 = new(new Guid("00000000-0000-0000-0000-000000000001"));
    private static readonly BattleSessionId Session2 = new(new Guid("00000000-0000-0000-0000-000000000002"));
    private static readonly BattleSessionId Session3 = new(new Guid("00000000-0000-0000-0000-000000000003"));
    private static readonly BattleSessionId Session4 = new(new Guid("00000000-0000-0000-0000-000000000004"));

    private static readonly ReplayDecodeProjection Projection1 = NewProjection();
    private static readonly ReplayDecodeProjection Projection2 = NewProjection();
    private static readonly ReplayDecodeProjection Projection3 = NewProjection();
    private static readonly ReplayDecodeProjection Projection4 = NewProjection();

    [TestMethod]
    public void Miss_ThenStore_ThenHit()
    {
        var cache = new ProjectionCache(capacity: 4);

        Assert.IsFalse(cache.TryGet(Session1, out _));

        cache.Store(Session1, Projection1);

        Assert.IsTrue(cache.TryGet(Session1, out ReplayDecodeProjection? projection));
        Assert.AreSame(Projection1, projection);
    }

    [TestMethod]
    public void Store_SameSession_RefreshesEntry()
    {
        var cache = new ProjectionCache(capacity: 1);
        cache.Store(Session1, Projection1);

        cache.Store(Session1, Projection2);

        Assert.IsTrue(cache.TryGet(Session1, out ReplayDecodeProjection? projection));
        Assert.AreSame(Projection2, projection);
    }

    [TestMethod]
    public void OverCapacity_EvictsLeastRecentlyStored()
    {
        var cache = new ProjectionCache(capacity: 2);
        cache.Store(Session1, Projection1);
        cache.Store(Session2, Projection2);

        // Session3 pushes the oldest (Session1) out.
        cache.Store(Session3, Projection3);

        Assert.IsFalse(cache.TryGet(Session1, out _));
        Assert.IsTrue(cache.TryGet(Session2, out _));
        Assert.IsTrue(cache.TryGet(Session3, out _));
    }

    [TestMethod]
    public void OverCapacity_RepeatedStores_EvictsInStoreOrder()
    {
        var cache = new ProjectionCache(capacity: 2);
        cache.Store(Session1, Projection1);
        cache.Store(Session2, Projection2);
        cache.Store(Session3, Projection3);
        cache.Store(Session4, Projection4);

        Assert.IsFalse(cache.TryGet(Session1, out _));
        Assert.IsFalse(cache.TryGet(Session2, out _));
        Assert.IsTrue(cache.TryGet(Session3, out _));
        Assert.IsTrue(cache.TryGet(Session4, out _));
    }

    private static ReplayDecodeProjection NewProjection() => new(
        DecodeRun: null!,
        Session: null!,
        Participants: Array.Empty<Participant>(),
        Positions: Array.Empty<PositionSample>(),
        Events: Array.Empty<CanonicalEvent>(),
        RawRecords: Array.Empty<RawRecord>(),
        Warnings: Array.Empty<string>());
}
