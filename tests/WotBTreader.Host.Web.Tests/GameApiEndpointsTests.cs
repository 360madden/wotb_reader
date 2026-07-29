using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Host.Web.Endpoints;

namespace WotBTreader.Host.Web.Tests;

[TestClass]
public sealed class GameApiEndpointsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StateMapsTheSafeSessionSnapshot()
    {
        DateTimeOffset observedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        DateTimeOffset expiresAt = observedAt.AddMinutes(5);
        var state = new FakeGameSessionState(new GameSessionSnapshot(
            GameSessionVerificationState.OfflineReplayVerified,
            GamePresent: true,
            observedAt,
            expiresAt,
            "session.offline_replay_verified"));

        IResult result = await GameApiEndpoints.GetGameStateAsync(state, TestContext.CancellationToken);

        GameStateResponse response = Value<GameStateResponse>(result);
        Assert.IsTrue(response.GamePresent);
        Assert.AreEqual("OfflineReplayVerified", response.VerificationState);
        Assert.AreEqual(observedAt, response.ObservedAtUtc);
        Assert.AreEqual(expiresAt, response.EvidenceExpiresAtUtc);
        Assert.AreEqual("session.offline_replay_verified", response.ReasonCode);
        Assert.AreEqual(TestContext.CancellationToken, state.LastCancellationToken);
    }

    [TestMethod]
    public async Task MemoryMapsNullableObservationAndRenamesPlayerHitPoints()
    {
        DateTimeOffset capturedAt = DateTimeOffset.UnixEpoch.AddSeconds(3);
        var observer = new FakeGameMemoryObserver(new GameMemoryObservation(
            GameMemoryObservationAvailability.Available,
            capturedAt,
            ReplayTimeSeconds: 4.5,
            PlayerHitPoints: 712,
            PlayerPositionX: null,
            PlayerPositionY: 2.5f,
            PlayerPositionZ: null,
            PlayerYaw: 1.25f,
            CameraPitch: null,
            AliveTankCount: 9));

        IResult result = await GameApiEndpoints.GetGameMemoryAsync(observer, TestContext.CancellationToken);

        GameMemoryResponse response = Value<GameMemoryResponse>(result);
        Assert.AreEqual("Available", response.Availability);
        Assert.AreEqual(capturedAt, response.CapturedAtUtc);
        Assert.AreEqual(712, response.PlayerHP);
        Assert.IsNull(response.PlayerPositionX);
        Assert.AreEqual(2.5f, response.PlayerPositionY);
        Assert.AreEqual(TestContext.CancellationToken, observer.LastCancellationToken);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public async Task LaunchRejectsMalformedOrEmptyArtifactWithoutCallingThePort(string sourceArtifactId)
    {
        var launcher = new FakeGameReplayLauncher(OperationResult.Success(
            new GameReplayLaunchOutcome(DateTimeOffset.UnixEpoch)));

        IResult result = await GameApiEndpoints.LaunchGameAsync(
            launcher,
            new GameLaunchRequest { SourceArtifactId = sourceArtifactId },
            TestContext.CancellationToken);

        GameLaunchResponse response = BadRequestValue<GameLaunchResponse>(result);
        Assert.IsFalse(response.Success);
        Assert.AreEqual("launch.source_artifact.invalid", response.Message);
        Assert.IsNull(launcher.Request);
    }

    [TestMethod]
    public async Task LaunchPassesTheManagedArtifactToThePort()
    {
        Guid artifactId = Guid.CreateVersion7();
        var launcher = new FakeGameReplayLauncher(OperationResult.Success(
            new GameReplayLaunchOutcome(DateTimeOffset.UnixEpoch)));

        IResult result = await GameApiEndpoints.LaunchGameAsync(
            launcher,
            new GameLaunchRequest { SourceArtifactId = artifactId.ToString("D") },
            TestContext.CancellationToken);

        GameLaunchResponse response = Value<GameLaunchResponse>(result);
        Assert.IsTrue(response.Success);
        Assert.AreEqual("launch.accepted", response.Message);
        Assert.IsNotNull(launcher.Request);
        Assert.AreEqual(artifactId, launcher.Request.SourceArtifactId.Value);
        Assert.AreEqual(TestContext.CancellationToken, launcher.LastCancellationToken);
    }

    [TestMethod]
    public async Task LaunchFailureExposesOnlyTheStableErrorCode()
    {
        var launcher = new FakeGameReplayLauncher(OperationResult.Failure<GameReplayLaunchOutcome>(
            new ApplicationError("launch.game_unavailable", "C:\\secret\\game.exe is unavailable.")));

        IResult result = await GameApiEndpoints.LaunchGameAsync(
            launcher,
            new GameLaunchRequest { SourceArtifactId = Guid.CreateVersion7().ToString("D") },
            TestContext.CancellationToken);

        GameLaunchResponse response = BadRequestValue<GameLaunchResponse>(result);
        Assert.IsFalse(response.Success);
        Assert.AreEqual("launch.game_unavailable", response.Message);
        Assert.IsFalse(response.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    private static T Value<T>(IResult result)
    {
        Assert.IsInstanceOfType<Ok<T>>(result);
        T? value = ((Ok<T>)result).Value;
        Assert.IsNotNull(value);
        return value;
    }

    private static T BadRequestValue<T>(IResult result)
    {
        Assert.IsInstanceOfType<BadRequest<T>>(result);
        T? value = ((BadRequest<T>)result).Value;
        Assert.IsNotNull(value);
        return value;
    }

    private sealed class FakeGameSessionState(GameSessionSnapshot snapshot) : IGameSessionState
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<GameSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class FakeGameMemoryObserver(GameMemoryObservation observation) : IGameMemoryObserver
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<GameMemoryObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(observation);
        }
    }

    private sealed class FakeGameReplayLauncher(OperationResult<GameReplayLaunchOutcome> outcome) : IGameReplayLauncher
    {
        public GameReplayLaunchRequest? Request { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<OperationResult<GameReplayLaunchOutcome>> LaunchAsync(
            GameReplayLaunchRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(outcome);
        }
    }
}
