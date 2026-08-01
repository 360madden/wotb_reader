using System.Text.Json;
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
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
    public void DiscoveryCandidateSerializationKeepsLegacyOffsetAliases()
    {
        var candidate = new OffsetDiscoveryCandidate
        {
            BaseDisplacement = "0x120",
            BaseDisplacementDecimal = 0x120,
        };

        JsonElement json = JsonSerializer.SerializeToElement(candidate, CamelCaseJson);
        Assert.AreEqual("0x120", json.GetProperty("baseDisplacement").GetString());
        Assert.AreEqual(0x120, json.GetProperty("baseDisplacementDecimal").GetInt64());
        Assert.AreEqual("0x120", json.GetProperty("relativeOffset").GetString());
        Assert.AreEqual(0x120, json.GetProperty("relativeOffsetDecimal").GetInt64());
    }

    [TestMethod]
    public async Task DiscoverRejectsUnknownFieldTypeBeforeCallingScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "field",
                FieldType = "Vector3",
                ExpectedValueHex = "00000000",
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_field_type", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task PatternRejectsMalformedToleranceBeforeCallingScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverPatternAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "signature",
                ExpectedValueHex = "488B90",
                ToleranceMaskHex = "00ZZ00",
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_tolerance_hex", response.GetProperty("error").GetString());
        Assert.IsFalse(scanner.PatternCalled);
    }

    [TestMethod]
    public async Task PointerChainRejectsUnboundedRequestBeforeCallingScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverPointerChainAsync(
            scanner,
            new PointerChainDiscoveryRequest
            {
                RootRelativeOffset = 0x100,
                PointerOffsets = [1, 2, 3, 4, 5],
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.pointer_chain.invalid_request", response.GetProperty("error").GetString());
        Assert.IsFalse(scanner.PointerChainCalled);
    }

    [TestMethod]
    public async Task PatternMapsTypedScanMetadata()
    {
        var scanner = new FakeGameMemoryScanner
        {
            PatternResult = OperationResult.Success(new MemoryScanResult(
                DateTimeOffset.UnixEpoch,
                0x140000000,
                2,
                4096,
                [new MemoryScanCandidate(0x140001000, 0x1000, [0x48, 0x8B], "488B", "image-mapping")],
                1,
                "x64",
                "wotblitz.exe",
                0,
                8,
                false,
                "aob")),
        };

        IResult result = await GameApiEndpoints.DiscoverPatternAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "signature",
                ExpectedValueHex = "488B",
                Alignment = 8,
                IncludeImageRegions = true,
            },
            TestContext.CancellationToken);

        OffsetDiscoveryResponse response = Value<OffsetDiscoveryResponse>(result);
        Assert.AreEqual("aob", response.ScanKind);
        Assert.AreEqual(8, response.Alignment);
        Assert.AreEqual("image-mapping", response.Candidates[0].AddressKind);
        Assert.AreEqual(TestContext.CancellationToken, scanner.LastCancellationToken);
    }

    [TestMethod]
    public async Task CompareMapsRollingBaselineRetentionSeparately()
    {
        var scanner = new FakeGameMemoryScanner
        {
            CompareResult = OperationResult.Success(new MemoryCompareResult(
                DateTimeOffset.UnixEpoch,
                12,
                4,
                2,
                1,
                1,
                1,
                [],
                Truncated: true,
                ComparedAgainstRollingBaseline: true,
                RetainedCount: 3)),
        };

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new CompareRequestApi { CompareMode = "changed", RollingBaseline = true },
            TestContext.CancellationToken);

        JsonElement response = OkAnonymous(result);
        Assert.AreEqual(4, response.GetProperty("CurrentCount").GetInt32());
        Assert.AreEqual(3, response.GetProperty("RetainedCount").GetInt32());
        Assert.IsTrue(response.GetProperty("ComparedAgainstRollingBaseline").GetBoolean());
        Assert.IsTrue(response.GetProperty("Truncated").GetBoolean());
    }

    [TestMethod]
    public async Task PointerChainMapsBoundedEvidenceResult()
    {
        var scanner = new FakeGameMemoryScanner
        {
            PointerChainResult = OperationResult.Success(new MemoryPointerChainResult(
                DateTimeOffset.UnixEpoch,
                [new MemoryPointerChainCandidate(
                    0x140001000,
                    0x22000000,
                    [0x140001000, 0x22000000],
                    "pointer-chain")],
                1)),
        };

        IResult result = await GameApiEndpoints.DiscoverPointerChainAsync(
            scanner,
            new PointerChainDiscoveryRequest
            {
                RootRelativeOffset = 0x1000,
                PointerOffsets = [0x20],
                MaxDepth = 4,
            },
            TestContext.CancellationToken);

        JsonElement response = OkAnonymous(result);
        JsonElement candidate = response.GetProperty("Candidates")[0];
        Assert.AreEqual("0x140001000", candidate.GetProperty("RootAddress").GetString());
        Assert.AreEqual("0x22000000", candidate.GetProperty("FinalAddress").GetString());
        Assert.AreEqual("pointer-chain", candidate.GetProperty("AddressKind").GetString());
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

    private static JsonElement BadRequestAnonymous(IResult result)
    {
        Assert.IsInstanceOfType<IValueHttpResult>(result);
        object? value = ((IValueHttpResult)result).Value;
        Assert.IsNotNull(value);
        return JsonSerializer.SerializeToElement(value);
    }

    private static JsonElement OkAnonymous(IResult result)
    {
        Assert.IsInstanceOfType<IValueHttpResult>(result);
        object? value = ((IValueHttpResult)result).Value;
        Assert.IsNotNull(value);
        return JsonSerializer.SerializeToElement(value);
    }

    private sealed class FakeGameMemoryScanner : IGameMemoryScanner
    {
        public bool PatternCalled { get; private set; }
        public bool PointerChainCalled { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public OperationResult<MemoryCompareResult> CompareResult { get; init; } = OperationResult.Success(
            new MemoryCompareResult(DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0, [], false, false, 0));
        public OperationResult<MemoryPointerChainResult> PointerChainResult { get; init; } = OperationResult.Success(
            new MemoryPointerChainResult(DateTimeOffset.UnixEpoch, [], 0));
        public OperationResult<MemoryScanResult> PatternResult { get; init; } = OperationResult.Success(
            new MemoryScanResult(DateTimeOffset.UnixEpoch, 0x140000000, 0, 0, [], 0));

        public ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
            MemoryScanRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PatternResult);

        public ValueTask<OperationResult<MemoryScanResult>> ScanPatternAsync(
            MemoryScanRequest request, CancellationToken cancellationToken)
        {
            PatternCalled = true;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(PatternResult);
        }

        public ValueTask<OperationResult<MemoryPointerChainResult>> ResolvePointerChainAsync(
            MemoryPointerChainRequest request, CancellationToken cancellationToken)
        {
            PointerChainCalled = true;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(PointerChainResult);
        }

        public ValueTask<OperationResult<string>> CreateSnapshotAsync(
            MemorySnapshotRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success("test"));

        public ValueTask<OperationResult<MemoryCompareResult>> CompareAsync(
            string sessionId, string compareMode, int maxCandidates,
            CancellationToken cancellationToken, bool advanceBaseline = false) =>
            ValueTask.FromResult(CompareResult);

        public void DiscardSession(string sessionId) { }

        public ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
            MemoryNeighborhoodRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PatternResult);
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
