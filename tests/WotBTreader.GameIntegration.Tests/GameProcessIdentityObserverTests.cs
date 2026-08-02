using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameProcessIdentityObserverTests
{
    [TestMethod]
    public async Task UnsupportedPlatform_DoesNotEnumerateOrOpen()
    {
        var platform = new FakePlatform { IsSupported = false };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Unsupported, result.Status);
        Assert.AreEqual(0, platform.EnumerationCount);
        Assert.AreEqual(0, platform.OpenCount);
    }

    [TestMethod]
    public async Task NoEligibleWindow_ReturnsAbsentWithoutOpeningProcess()
    {
        var platform = new FakePlatform();
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Absent, result.Status);
        Assert.AreEqual(0, platform.OpenCount);
    }

    [TestMethod]
    public async Task MultipleEligibleWindows_ReturnAmbiguousWithoutOpeningProcess()
    {
        var platform = new FakePlatform
        {
            Candidates =
            [
                new GameWindowCandidate(10, 100),
                new GameWindowCandidate(20, 200),
            ],
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Ambiguous, result.Status);
        Assert.AreEqual(0, platform.OpenCount);
    }

    [TestMethod]
    public async Task IncompleteEnumeration_ReturnsAmbiguousWithoutOpeningProcess()
    {
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            EnumerationComplete = false,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Ambiguous, result.Status);
        Assert.AreEqual(0, platform.OpenCount);
    }

    [TestMethod]
    public async Task CompleteCandidate_UsesExactQueryOnlyAccessAndDisposesSession()
    {
        FakeQuerySession session = CreateSession();
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Available, result.Status);
        Assert.AreEqual(
            GameProcessIdentityObserver.ProcessQueryLimitedInformation,
            platform.DesiredAccess);
        Assert.AreEqual(0x1000u, platform.DesiredAccess);
        Assert.IsTrue(session.Disposed);
        Assert.IsNotNull(result.Identity);
        Assert.AreEqual(100, result.Identity.ProcessId);
        Assert.AreEqual(42, result.Identity.ProcessStartIdentity);
        Assert.AreEqual(10, result.Identity.WindowHandle);
    }

    [TestMethod]
    public async Task ExpectedProcessId_IsPassedToWindowEnumeration()
    {
        FakeQuerySession session = CreateSession();
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(100, CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.Available, result.Status);
        Assert.AreEqual(100, platform.ExpectedProcessId);
    }

    [TestMethod]
    public async Task QueryFailure_ReturnsNoPartialIdentity()
    {
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = null,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.QueryFailed, result.Status);
        Assert.IsNull(result.Identity);
    }

    [TestMethod]
    public async Task PidReuseOrOwnerRace_ReturnsNoIdentityAndDisposesSession()
    {
        FakeQuerySession session = CreateSession() with { ProcessId = 101 };
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.QueryFailed, result.Status);
        Assert.IsNull(result.Identity);
        Assert.IsTrue(session.Disposed);
    }

    [TestMethod]
    public async Task ChangedWindowOwner_ReturnsNoIdentityAndDisposesSession()
    {
        FakeQuerySession session = CreateSession();
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
            WindowOwnerMatches = false,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.QueryFailed, result.Status);
        Assert.IsTrue(session.Disposed);
    }

    [TestMethod]
    public async Task ExitedProcess_ReturnsNoIdentityAndDisposesSession()
    {
        FakeQuerySession session = CreateSession() with { IsAlive = false };
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
        };
        var observer = new GameProcessIdentityObserver(platform);

        GameProcessObservationResult result =
            await observer.ObserveAsync(CancellationToken.None);

        Assert.AreEqual(GameProcessObservationStatus.QueryFailed, result.Status);
        Assert.IsTrue(session.Disposed);
    }

    [TestMethod]
    public async Task CancellationBeforeObservation_DoesNotOpenProcess()
    {
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = CreateSession(),
        };
        var observer = new GameProcessIdentityObserver(platform);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await observer.ObserveAsync(cancellation.Token));

        Assert.AreEqual(0, platform.OpenCount);
    }

    [TestMethod]
    public async Task CancellationAfterOpen_DisposesSession()
    {
        using CancellationTokenSource cancellation = new();
        FakeQuerySession session = CreateSession();
        var platform = new FakePlatform
        {
            Candidates = [new GameWindowCandidate(10, 100)],
            Session = session,
            AfterOpen = cancellation.Cancel,
        };
        var observer = new GameProcessIdentityObserver(platform);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await observer.ObserveAsync(cancellation.Token));

        Assert.IsTrue(session.Disposed);
    }

    private static FakeQuerySession CreateSession() =>
        new()
        {
            ProcessId = 100,
            ProcessStartIdentity = 42,
            IsAlive = true,
            CanonicalExecutablePath = @"C:\Games\wotblitz.exe",
            FileIdentity = new ExecutableFileIdentity(7, 11),
            ProductVersion = "11.18.0.7",
            ExecutableSha256 = new ContentHash(new string('a', 64)),
        };

    private sealed class FakePlatform : IGameProcessQueryPlatform
    {
        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<GameWindowCandidate> Candidates { get; init; } = [];

        public FakeQuerySession? Session { get; init; }

        public bool WindowOwnerMatches { get; init; } = true;

        public bool EnumerationComplete { get; init; } = true;

        public int EnumerationCount { get; private set; }

        public int OpenCount { get; private set; }

        public int? ExpectedProcessId { get; private set; }

        public uint DesiredAccess { get; private set; }

        public Action? AfterOpen { get; init; }

        public GameWindowEnumerationResult EnumerateEligibleGameWindows(
            int? expectedProcessId = null)
        {
            EnumerationCount++;
            ExpectedProcessId = expectedProcessId;
            return new GameWindowEnumerationResult(
                Candidates,
                EnumerationComplete);
        }

        public ValueTask<IGameProcessQuerySession?> OpenQuerySessionAsync(
            GameWindowCandidate candidate,
            uint desiredAccess,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            DesiredAccess = desiredAccess;
            AfterOpen?.Invoke();
            return ValueTask.FromResult<IGameProcessQuerySession?>(Session);
        }

        public bool IsWindowStillEligible(GameWindowCandidate candidate) =>
            WindowOwnerMatches;
    }

    private sealed record FakeQuerySession : IGameProcessQuerySession
    {
        public required int ProcessId { get; init; }

        public required long ProcessStartIdentity { get; init; }

        public required bool IsAlive { get; init; }

        public required string CanonicalExecutablePath { get; init; }

        public required ExecutableFileIdentity FileIdentity { get; init; }

        public required string ProductVersion { get; init; }

        public required ContentHash ExecutableSha256 { get; init; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
