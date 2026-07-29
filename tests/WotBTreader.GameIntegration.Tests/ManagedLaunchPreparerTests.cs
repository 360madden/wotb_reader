using System.Reflection;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class ManagedLaunchPreparerTests
{
    private const string SyntheticCorrelation =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string[] ExpectedPreparationCallOrder =
        ["identity", "correlation", "reconciled-baseline"];

    [TestMethod]
    public async Task PrepareAsync_RetainsExactTrustedIdentityAndCompleteBaseline()
    {
        TrustedGameExecutableIdentity identity = Identity();
        LifecycleFeedBaseline baseline = Baseline(LifecycleFeedHealth.Healthy, Sources());
        var preparer = new ManagedLaunchPreparer(new IdentityProvider(identity), new Feed(baseline), new Generator(SyntheticCorrelation));

        OperationResult<ManagedLaunchPreparation> result = await preparer.PrepareAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreSame(identity, result.Value!.TrustedIdentity);
        Assert.AreSame(identity.FileIdentity, result.Value.TrustedIdentity.FileIdentity);
        Assert.AreSame(baseline, result.Value.LifecycleBaseline);
        CollectionAssert.AreEqual(baseline.Sources.ToArray(), result.Value.LifecycleBaseline.Sources.ToArray());
        Assert.AreEqual("ManagedLaunchPreparation", result.Value.ToString());
    }

    [TestMethod]
    public async Task PrepareAsync_UsesReconciledBarrierAfterIdentityAndCorrelation()
    {
        List<string> calls = [];
        var preparer = new ManagedLaunchPreparer(
            new IdentityProvider(Identity(), calls),
            new Feed(Baseline(LifecycleFeedHealth.Healthy, []), calls),
            new Generator(SyntheticCorrelation, calls));

        await preparer.PrepareAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            ExpectedPreparationCallOrder,
            calls);
    }

    [TestMethod]
    public async Task PrepareAsync_IdentityFailureIsPropagatedAndSkipsRemainingCalls()
    {
        ApplicationError error = new("identity.failure", "Synthetic.", Retryable: true);
        var generator = new Generator(SyntheticCorrelation);
        var feed = new Feed(Baseline(LifecycleFeedHealth.Healthy, []));
        var preparer = new ManagedLaunchPreparer(new IdentityProvider(error), feed, generator);

        OperationResult<ManagedLaunchPreparation> result = await preparer.PrepareAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreSame(error, result.Error);
        Assert.AreEqual(0, generator.Calls);
        Assert.AreEqual(0, feed.ReconciledCalls);
    }

    [TestMethod]
    public async Task PrepareAsync_CorrelationFailureOrInvalidValueFailsClosedAndSkipsBarrier()
    {
        foreach (OperationResult<string> correlation in new[]
                 {
                     OperationResult.Failure<string>(new ApplicationError("synthetic", "Synthetic.")),
                     OperationResult.Success(" "),
                     OperationResult.Success(new string('A', 42)),
                     OperationResult.Success(new string('+', 43)),
                 })
        {
            var feed = new Feed(Baseline(LifecycleFeedHealth.Healthy, []));
            var preparer = new ManagedLaunchPreparer(new IdentityProvider(Identity()), feed, new Generator(correlation));

            OperationResult<ManagedLaunchPreparation> result = await preparer.PrepareAsync(CancellationToken.None);

            Assert.IsFalse(result.IsSuccess);
            Assert.IsNull(result.Value);
            Assert.AreEqual("game.launch.correlation_failed", result.Error!.Code);
            Assert.IsTrue(result.Error.Retryable);
            Assert.AreEqual(0, feed.ReconciledCalls);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_UnhealthyBaselineFailsForBothNonHealthyStates()
    {
        foreach (LifecycleFeedHealth health in new[] { LifecycleFeedHealth.Uninitialized, LifecycleFeedHealth.Degraded })
        {
            var preparer = new ManagedLaunchPreparer(new IdentityProvider(Identity()), new Feed(Baseline(health, [])), new Generator(SyntheticCorrelation));
            OperationResult<ManagedLaunchPreparation> result = await preparer.PrepareAsync(CancellationToken.None);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("game.launch.lifecycle_unhealthy", result.Error!.Code);
            Assert.IsTrue(result.Error.Retryable);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_PreservesZeroAndMultipleSourcesWithoutSelection()
    {
        foreach (IReadOnlyList<LifecycleSourceCursor> sources in new IReadOnlyList<LifecycleSourceCursor>[] { [], Sources() })
        {
            LifecycleFeedBaseline baseline = Baseline(LifecycleFeedHealth.Healthy, sources);
            var preparer = new ManagedLaunchPreparer(new IdentityProvider(Identity()), new Feed(baseline), new Generator(SyntheticCorrelation));
            OperationResult<ManagedLaunchPreparation> result = await preparer.PrepareAsync(CancellationToken.None);
            Assert.AreSame(sources, result.Value!.LifecycleBaseline.Sources);
        }
    }

    [TestMethod]
    public async Task PrepareAsync_CancellationAtBoundariesPropagatesAndPreventsLaterCalls()
    {
        using (var initial = new CancellationTokenSource())
        {
            initial.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await new ManagedLaunchPreparer(new IdentityProvider(Identity()), new Feed(Baseline(LifecycleFeedHealth.Healthy, [])), new Generator(SyntheticCorrelation)).PrepareAsync(initial.Token));
        }

        using (var afterIdentity = new CancellationTokenSource())
        {
            var generator = new Generator(SyntheticCorrelation);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await new ManagedLaunchPreparer(new IdentityProvider(Identity(), cancellation: afterIdentity), new Feed(Baseline(LifecycleFeedHealth.Healthy, [])), generator).PrepareAsync(afterIdentity.Token));
            Assert.AreEqual(0, generator.Calls);
        }

        using (var afterCorrelation = new CancellationTokenSource())
        {
            var feed = new Feed(Baseline(LifecycleFeedHealth.Healthy, []));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await new ManagedLaunchPreparer(new IdentityProvider(Identity()), feed, new Generator(SyntheticCorrelation, cancellation: afterCorrelation)).PrepareAsync(afterCorrelation.Token));
            Assert.AreEqual(0, feed.ReconciledCalls);
        }

        using (var afterBarrier = new CancellationTokenSource())
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await new ManagedLaunchPreparer(new IdentityProvider(Identity()), new Feed(Baseline(LifecycleFeedHealth.Healthy, []), cancellation: afterBarrier), new Generator(SyntheticCorrelation)).PrepareAsync(afterBarrier.Token));
        }
    }

    [TestMethod]
    public void LaunchCorrelationGenerator_UsesDistinctUnpaddedBase64UrlValuesFrom32Bytes()
    {
        var generator = new LaunchCorrelationGenerator();
        string[] values = Enumerable.Range(0, 64).Select(_ => generator.Generate().Value!).ToArray();
        Assert.IsTrue(values.All(static value => value.Length == 43 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')));
        Assert.AreEqual(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(values.All(static value => DecodeBase64Url(value).Length == 32));
    }

    [TestMethod]
    public async Task PrepareAsync_RepeatedAndConcurrentCallsHaveDistinctCorrelations()
    {
        var identityProvider = new IdentityProvider(Identity());
        var preparer = new ManagedLaunchPreparer(identityProvider, new Feed(Baseline(LifecycleFeedHealth.Healthy, [])), new LaunchCorrelationGenerator());
        OperationResult<ManagedLaunchPreparation>[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => preparer.PrepareAsync(CancellationToken.None).AsTask()));
        Assert.AreEqual(results.Length, results.Select(static result => result.Value!.LaunchCorrelation).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(results.Length, identityProvider.Calls);
    }

    [TestMethod]
    public void ManagedLaunchPreparer_ConstructorHasOnlyFrozenDependencies()
    {
        ConstructorInfo constructor = typeof(ManagedLaunchPreparer).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single();
        CollectionAssert.AreEqual(
            new[] { typeof(ITrustedGameIdentityProvider), typeof(IBlitzReplayLifecycleFeed), typeof(ILaunchCorrelationGenerator) },
            constructor.GetParameters().Select(static parameter => parameter.ParameterType).ToArray());
    }

    private static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
    private static TrustedGameExecutableIdentity Identity() => new(new InstalledGameIdentity(@"C:\\safe\\wotblitz.exe", "1", Hash("1"), @"C:\\safe\\data", []), new ExecutableFileIdentity(3, 5));
    private static ContentHash Hash(string value) => new(value.PadLeft(64, '0'));
    private static IReadOnlyList<LifecycleSourceCursor> Sources() => [new(Hash("2"), 1, 10), new(Hash("3"), 2, 20)];
    private static LifecycleFeedBaseline Baseline(LifecycleFeedHealth health, IReadOnlyList<LifecycleSourceCursor> sources) => new(42, 9, health, sources);

    private sealed class IdentityProvider : ITrustedGameIdentityProvider
    {
        private readonly OperationResult<TrustedGameExecutableIdentity> _result;
        private readonly List<string>? _calls;
        private readonly CancellationTokenSource? _cancellation;
        private int _callCount;
        public int Calls => Volatile.Read(ref _callCount);
        public IdentityProvider(TrustedGameExecutableIdentity identity, List<string>? calls = null, CancellationTokenSource? cancellation = null) : this(OperationResult.Success(identity), calls, cancellation) { }
        public IdentityProvider(ApplicationError error) : this(OperationResult.Failure<TrustedGameExecutableIdentity>(error)) { }
        private IdentityProvider(OperationResult<TrustedGameExecutableIdentity> result, List<string>? calls = null, CancellationTokenSource? cancellation = null) { _result = result; _calls = calls; _cancellation = cancellation; }
        public ValueTask<OperationResult<TrustedGameExecutableIdentity>> GetAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref _callCount); _calls?.Add("identity"); _cancellation?.Cancel(); return ValueTask.FromResult(_result); }
    }

    private sealed class Generator : ILaunchCorrelationGenerator
    {
        private readonly OperationResult<string> _result; private readonly List<string>? _calls; private readonly CancellationTokenSource? _cancellation;
        public int Calls { get; private set; }
        public Generator(string value, List<string>? calls = null, CancellationTokenSource? cancellation = null) : this(OperationResult.Success(value), calls, cancellation) { }
        public Generator(OperationResult<string> result) : this(result, null, null) { }
        private Generator(OperationResult<string> result, List<string>? calls, CancellationTokenSource? cancellation) { _result = result; _calls = calls; _cancellation = cancellation; }
        public OperationResult<string> Generate() { Calls++; _calls?.Add("correlation"); _cancellation?.Cancel(); return _result; }
    }

    private sealed class Feed : IBlitzReplayLifecycleFeed
    {
        private readonly LifecycleFeedBaseline _baseline; private readonly List<string>? _calls; private readonly CancellationTokenSource? _cancellation;
        public int ReconciledCalls { get; private set; }
        public Feed(LifecycleFeedBaseline baseline, List<string>? calls = null, CancellationTokenSource? cancellation = null) { _baseline = baseline; _calls = calls; _cancellation = cancellation; }
        public ValueTask<LifecycleFeedBaseline> CaptureBaselineAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Snapshot baseline must not be used.");
        public ValueTask<LifecycleFeedBaseline> CaptureReconciledBaselineAsync(CancellationToken cancellationToken) { ReconciledCalls++; _calls?.Add("reconciled-baseline"); _cancellation?.Cancel(); return ValueTask.FromResult(_baseline); }
        public ValueTask<LifecycleFeedReadResult> ReadAfterAsync(long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
