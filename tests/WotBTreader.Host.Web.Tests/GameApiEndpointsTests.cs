using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
using WotBTreader.Core.Overlay;
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
            MissingSessionRepository(),
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
            MissingSessionRepository(),
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
    public async Task LaunchBindsExactCompletedSessionToExactArtifact()
    {
        SourceArtifactId artifactId = SourceArtifactId.New();
        BattleSessionId sessionId = BattleSessionId.New();
        ReplayDecodeProjection projection = LaunchProjection(artifactId, sessionId);
        var launcher = new FakeGameReplayLauncher(OperationResult.Success(
            new GameReplayLaunchOutcome(DateTimeOffset.UnixEpoch)));

        IResult result = await GameApiEndpoints.LaunchGameAsync(
            launcher,
            new FakeSessionQueryRepository(OperationResult.Success(projection)),
            new GameLaunchRequest
            {
                SourceArtifactId = artifactId.ToString(),
                BattleSessionId = sessionId.ToString(),
            },
            TestContext.CancellationToken);

        GameLaunchResponse response = Value<GameLaunchResponse>(result);
        Assert.IsTrue(response.Success);
        Assert.IsNotNull(launcher.Request);
        Assert.AreEqual(artifactId, launcher.Request.SourceArtifactId);
        Assert.AreEqual(sessionId, launcher.Request.BattleSessionId);
    }

    [TestMethod]
    public async Task LaunchRejectsSessionFromDifferentArtifactWithoutCallingLauncher()
    {
        SourceArtifactId requestedArtifact = SourceArtifactId.New();
        BattleSessionId sessionId = BattleSessionId.New();
        ReplayDecodeProjection projection = LaunchProjection(
            SourceArtifactId.New(),
            sessionId);
        var launcher = new FakeGameReplayLauncher(OperationResult.Success(
            new GameReplayLaunchOutcome(DateTimeOffset.UnixEpoch)));

        IResult result = await GameApiEndpoints.LaunchGameAsync(
            launcher,
            new FakeSessionQueryRepository(OperationResult.Success(projection)),
            new GameLaunchRequest
            {
                SourceArtifactId = requestedArtifact.ToString(),
                BattleSessionId = sessionId.ToString(),
            },
            TestContext.CancellationToken);

        GameLaunchResponse response = BadRequestValue<GameLaunchResponse>(result);
        Assert.AreEqual("launch.replay_association.invalid", response.Message);
        Assert.IsNull(launcher.Request);
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
    public async Task DiscoverPropagatesPreciseFloatToleranceToScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = "Float",
                ExpectedValueHex = "0000C842",
                FloatTolerance = 0.25f,
            },
            TestContext.CancellationToken);

        Assert.IsInstanceOfType<Ok<OffsetDiscoveryResponse>>(result);
        Assert.IsNotNull(scanner.LastScanRequest);
        Assert.AreEqual(0.25f, scanner.LastScanRequest.FloatTolerance);
        Assert.IsNull(scanner.LastScanRequest.ToleranceMask);
        // The coordinator resolves the typed ValueKind from FieldType before
        // invoking the scanner; the host endpoint preserves that boundary.
        Assert.AreEqual(MemoryValueKind.Bytes, scanner.LastScanRequest.ValueKind);
    }

    [TestMethod]
    [DataRow("Float", "0000")]
    [DataRow("Int32", "0000")]
    [DataRow("Double", "0000C842")]
    public async Task DiscoverRejectsTypedValuesWithAnInvalidWidth(
        string fieldType,
        string expectedValueHex)
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = fieldType,
                ExpectedValueHex = expectedValueHex,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_value_width", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastScanRequest);
    }

    [TestMethod]
    public async Task DiscoverRejectsInvalidFloatToleranceBeforeCallingScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = "Float",
                ExpectedValueHex = "0000C842",
                FloatTolerance = -0.25f,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_float_tolerance", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastScanRequest);
    }

    [TestMethod]
    public async Task PatternRejectsNumericFloatToleranceBeforeCallingScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverPatternAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "signature",
                ExpectedValueHex = "488B90",
                FloatTolerance = 0.1f,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.float_tolerance_not_supported_for_pattern", response.GetProperty("error").GetString());
        Assert.IsFalse(scanner.PatternCalled);
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
        Assert.IsNotNull(scanner.LastScanRequest);
        Assert.AreEqual(
            MemoryRegionSelection.Default | MemoryRegionSelection.Image,
            scanner.LastScanRequest.RegionSelection);
    }

    [TestMethod]
    public async Task PatternForwardsImageRegionsOnlyToScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverPatternAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "signature",
                ExpectedValueHex = "488B",
                IncludeImageRegions = true,
                ImageRegionsOnly = true,
            },
            TestContext.CancellationToken);

        Assert.IsInstanceOfType<Ok<OffsetDiscoveryResponse>>(result);
        Assert.IsNotNull(scanner.LastScanRequest);
        Assert.AreEqual(MemoryRegionSelection.Image, scanner.LastScanRequest.RegionSelection);
    }

    [TestMethod]
    public async Task DiscoverForwardsImageRegionsOnlyToScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = "Float",
                ExpectedValueHex = "0000C842",
                IncludeImageRegions = true,
                ImageRegionsOnly = true,
            },
            TestContext.CancellationToken);

        Assert.IsInstanceOfType<Ok<OffsetDiscoveryResponse>>(result);
        Assert.IsNotNull(scanner.LastScanRequest);
        Assert.AreEqual(MemoryRegionSelection.Image, scanner.LastScanRequest.RegionSelection);
    }

    [TestMethod]
    public async Task DiscoverRejectsInvalidAlignmentInsteadOfNormalizingIt()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = "Float",
                ExpectedValueHex = "0000C842",
                Alignment = 3,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastScanRequest);
    }

    [TestMethod]
    public async Task DiscoverRejectsTooSmallRegionFilterInsteadOfScanningEverything()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverOffsetsAsync(
            scanner,
            new OffsetDiscoveryRequest
            {
                FieldName = "position",
                FieldType = "Float",
                ExpectedValueHex = "0000C842",
                MinRegionSize = 0,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastScanRequest);
    }

    [TestMethod]
    public async Task SnapshotReturnsThePublicSessionContract()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest { ValueKind = "Int32", ValueSize = 4 },
            TestContext.CancellationToken);

        OffsetSnapshotResponse response = Value<OffsetSnapshotResponse>(result);
        Assert.AreEqual("test", response.SessionId);
    }

    [TestMethod]
    public async Task SnapshotForwardsExplicitByteBudgetToTheScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest
            {
                ValueKind = "Float",
                ValueSize = 4,
                MaxBytes = 64L * 1024 * 1024,
            },
            TestContext.CancellationToken);

        OffsetSnapshotResponse response = Value<OffsetSnapshotResponse>(result);
        Assert.AreEqual("test", response.SessionId);
        Assert.IsNotNull(scanner.LastSnapshotRequest);
        Assert.AreEqual(64L * 1024 * 1024, scanner.LastSnapshotRequest!.MaxBytes);
    }

    [TestMethod]
    public async Task SnapshotRejectsNegativeByteBudget()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest
            {
                ValueKind = "Int32",
                ValueSize = 4,
                MaxBytes = -1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastSnapshotRequest);
    }

    [TestMethod]
    public async Task SnapshotRejectsBudgetAboveTheEngineCeiling()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest
            {
                ValueKind = "Int32",
                ValueSize = 4,
                MaxBytes = OffsetSnapshotRequest.MaximumSnapshotBytes + 1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastSnapshotRequest);
    }

    [TestMethod]
    public void DiscardReturnsThePublicContractAndForwardsTheSessionId()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = GameApiEndpoints.DiscardSessionAsync(scanner, "session-1");

        OffsetDiscardResponse response = Value<OffsetDiscardResponse>(result);
        Assert.AreEqual("session-1", response.Discarded);
        Assert.AreEqual("session-1", scanner.DiscardedSession);
    }

    [TestMethod]
    public async Task CompareRejectsUnknownModeInsteadOfFallingBackToChanged()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest { CompareMode = "not-a-mode" },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_compare_mode", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task SnapshotRejectsInvertedAddressAndFloatRanges()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest
            {
                ValueKind = "Float",
                ValueSize = 4,
                MinAddress = 0x2000,
                MaxAddress = 0x1000,
                FloatMin = 10,
                FloatMax = 1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task SnapshotRejectsNonFiniteFloatBounds()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CreateSnapshotAsync(
            scanner,
            new OffsetSnapshotRequest
            {
                ValueKind = "Float",
                ValueSize = 4,
                FloatMin = float.NaN,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task NeighborhoodRejectsInvalidTypedRanges()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverNeighborhoodAsync(
            scanner,
            new OffsetNeighborhoodRequest
            {
                ReferenceOffset = 0x100,
                FloatMin = 10,
                FloatMax = 1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task NeighborhoodRejectsOutOfRangeWindowInsteadOfClampingIt()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.DiscoverNeighborhoodAsync(
            scanner,
            new OffsetNeighborhoodRequest { ReferenceOffset = 0x100, WindowSize = 16 },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_window_size", response.GetProperty("error").GetString());
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
            new OffsetCompareRequest { CompareMode = "changed", RollingBaseline = true },
            TestContext.CancellationToken);

        JsonElement response = OkAnonymous(result);
        Assert.AreEqual(4, response.GetProperty("CurrentCount").GetInt32());
        Assert.AreEqual(3, response.GetProperty("RetainedCount").GetInt32());
        Assert.IsTrue(response.GetProperty("ComparedAgainstRollingBaseline").GetBoolean());
        Assert.IsTrue(response.GetProperty("Truncated").GetBoolean());
    }

    [TestMethod]
    public async Task CompareRejectsDeltaWithoutBothParameters()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "delta",
                DeltaTarget = 2.5,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_delta_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CompareRejectsDeltaWithNegativeTolerance()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "delta",
                DeltaTarget = 2.5,
                DeltaTolerance = -0.1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_delta_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CompareRejectsDeltaParametersOnNonDeltaMode()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "changed",
                DeltaTarget = 2.5,
                DeltaTolerance = 0.1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.delta_only_with_delta_mode", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CompareForwardsDeltaParametersToScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "delta",
                DeltaTarget = 2.5,
                DeltaTolerance = 0.25,
                RollingBaseline = true,
            },
            TestContext.CancellationToken);

        Assert.IsNotNull(OkAnonymous(result));
        Assert.AreEqual("000001", scanner.LastCompareSessionId);
        Assert.AreEqual("delta", scanner.LastCompareMode);
        Assert.AreEqual(2.5, scanner.LastCompareDeltaTarget);
        Assert.AreEqual(0.25, scanner.LastCompareDeltaTolerance);
    }

    [TestMethod]
    public async Task CompareRejectsExactWithoutTolerance()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "exact",
                DeltaTarget = 60.0,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_exact_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CompareRejectsExactWithNegativeTolerance()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "exact",
                DeltaTarget = 60.0,
                DeltaTolerance = -0.1,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_exact_options", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task CompareForwardsExactParametersToScanner()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CompareSnapshotAsync(
            scanner,
            "000001",
            new OffsetCompareRequest
            {
                CompareMode = "exact",
                DeltaTarget = 60.0,
                DeltaTolerance = 0.05,
                RollingBaseline = true,
            },
            TestContext.CancellationToken);

        Assert.IsNotNull(OkAnonymous(result));
        Assert.AreEqual("000001", scanner.LastCompareSessionId);
        Assert.AreEqual("exact", scanner.LastCompareMode);
        Assert.AreEqual(60.0, scanner.LastCompareDeltaTarget);
        Assert.AreEqual(0.05, scanner.LastCompareDeltaTolerance);
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
            MissingSessionRepository(),
            new GameLaunchRequest { SourceArtifactId = Guid.CreateVersion7().ToString("D") },
            TestContext.CancellationToken);

        GameLaunchResponse response = BadRequestValue<GameLaunchResponse>(result);
        Assert.IsFalse(response.Success);
        Assert.AreEqual("launch.game_unavailable", response.Message);
        Assert.IsFalse(response.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ReadForwardsAddressesAndReportsValues()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest
            {
                Addresses = ["0x7FFA1234"],
                ValueKind = "Float",
                ValueSize = 4,
            },
            TestContext.CancellationToken);

        OffsetReadResponse response = Value<OffsetReadResponse>(result);
        Assert.IsNotNull(scanner.LastReadRequest);
        Assert.HasCount(1, scanner.LastReadRequest!.Addresses);
        Assert.AreEqual(0x7FFA1234L, scanner.LastReadRequest.Addresses[0]);
        Assert.AreEqual(MemoryValueKind.FloatValue, scanner.LastReadRequest.ValueKind);
        Assert.AreEqual(1, response.ReadCount);
        Assert.AreEqual("0x7FFA1234", response.Reads[0].AbsoluteAddress);
        Assert.IsTrue(response.Reads[0].ReadOk);
        Assert.AreEqual("1", response.Reads[0].ValueSummary);
    }

    [TestMethod]
    public async Task ReadRejectsInvalidAddressHex()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest { Addresses = ["not-hex"] },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_address", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastReadRequest);
    }

    [TestMethod]
    public async Task ReadAcceptsOddLengthAddressHex()
    {
        // Scan-produced addresses are unpadded X-format and can be odd-length
        // (e.g. "0x4520000" -> "4520000", 7 chars). IsHexString's even-length
        // byte-pair rule would reject them; addresses must use IsHexAddress.
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest
            {
                Addresses = ["0x4520000", "0x04520000"],
                ValueKind = "Float",
                ValueSize = 4,
            },
            TestContext.CancellationToken);

        OffsetReadResponse response = Value<OffsetReadResponse>(result);
        Assert.IsNotNull(scanner.LastReadRequest);
        Assert.HasCount(2, scanner.LastReadRequest!.Addresses);
        Assert.AreEqual(0x4520000L, scanner.LastReadRequest.Addresses[0]);
        Assert.AreEqual(0x4520000L, scanner.LastReadRequest.Addresses[1]);
    }

    [TestMethod]
    public async Task ReadRejectsAddressBeyondSignedLong()
    {
        // 16-digit hex >= 0x8000000000000000 overflows signed long: the
        // address must still be rejected as invalid, not silently wrapped.
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest
            {
                Addresses = ["0x8000000000000000"],
                ValueKind = "Float",
                ValueSize = 4,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_address", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastReadRequest);
    }

    [TestMethod]
    public async Task ReadRejectsTooManyAddresses()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest
            {
                Addresses = [.. Enumerable.Range(0, 2001).Select(index => "0x" + index.ToString("X", CultureInfo.InvariantCulture))],
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastReadRequest);
    }

    [TestMethod]
    public async Task ReadRejectsKindWidthMismatch()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.ReadOffsetsAsync(
            scanner,
            new OffsetReadRequest
            {
                Addresses = ["0x1000"],
                ValueKind = "Double",
                ValueSize = 4,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastReadRequest);
    }

    [TestMethod]
    public async Task EntityPositionReadProjectsOnlySanitizedEvidence()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityPositionResult = OperationResult.Success(
                new EntityPositionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(7),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    12.5f,
                    3.25f,
                    -44.75f,
                    "primary",
                    null,
                    Attempts: 1,
                    NodesVisited: 3,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: true,
                    ConsistentDoubleRead: true,
                    HardwareAtomicReadProven: false,
                    SameDecodedClockProven: false)),
        };

        IResult result = await GameApiEndpoints.ReadEntityPositionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityPositionReadRequest { EntityId = 4242 },
            TestContext.CancellationToken);

        EntityPositionReadResponse response = Value<EntityPositionReadResponse>(result);
        Assert.AreEqual(4242, scanner.LastEntityPositionRequest?.EntityId);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(12.5f, response.X);
        Assert.IsTrue(response.ModuleRooted);
        Assert.IsTrue(response.EntityIdentityRevalidated);
        Assert.IsTrue(response.ConsistentDoubleRead);
        Assert.IsFalse(response.HardwareAtomicReadProven);
        Assert.IsFalse(response.SameDecodedClockProven);
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        Assert.IsFalse(json.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("observedValue", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PositionPage_ForwardsEntityIdAndProjectsHexAddresses()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityPositionAddressResult = OperationResult.Success(
                new WotBTreader.Application.Game.EntityPositionAddressResult(
                    Type10EntityPositionStatus.Resolved,
                    RecordAddress: 0x25000038,
                    PageAddress: 0x25000000,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 3,
                    ModuleRooted: true)),
        };

        IResult result = await GameApiEndpoints.ResolveEntityPositionAddressAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityPositionAddressRequest { EntityId = 4242 },
            TestContext.CancellationToken);

        EntityPositionAddressResponse response = Value<EntityPositionAddressResponse>(result);
        Assert.AreEqual(4242, scanner.LastEntityPositionAddressRequest?.EntityId);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual("0x25000038", response.RecordAddress);
        Assert.AreEqual("0x25000000", response.PageAddress);
        Assert.IsTrue(response.ModuleRooted);
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        Assert.IsTrue(json.Contains("0x25000038", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PositionPage_FailureReturnsBadRequest()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityPositionAddressResult = OperationResult.Failure<WotBTreader.Application.Game.EntityPositionAddressResult>(
                new ApplicationError(
                    "discover.entity_position.address_unsupported_build",
                    "The running build does not match the exact-build layout.")),
        };

        IResult result = await GameApiEndpoints.ResolveEntityPositionAddressAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityPositionAddressRequest { EntityId = 4242 },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [TestMethod]
    public async Task EntityRegion_ForwardsRequestAndReturnsBase64BytesWithReplayTime()
    {
        byte[] region = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(7),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    ReplayTimeSeconds: 42.5,
                    RegionBytes: region,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 3,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: true)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 6,
                BattleSessionId = "019fa431-5ace-78ce-ba92-cd825ff9911c",
            },
            TestContext.CancellationToken);

        EntityRecordRegionReadResponse response = Value<EntityRecordRegionReadResponse>(result);
        Assert.AreEqual(4242, scanner.LastEntityRegionRequest?.EntityId);
        Assert.AreEqual(6, scanner.LastEntityRegionRequest?.RegionLength);
        Assert.IsNotNull(scanner.LastEntityRegionRequest?.BattleSessionId);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(42.5, response.ReplayTimeSeconds);
        Assert.AreEqual(Convert.ToBase64String(region), response.RegionBase64);
        Assert.IsTrue(response.SameDecodedClockProven);
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        // The raw bytes go out as base64; no absolute address may leak.
        Assert.IsFalse(json.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(json.Contains(Convert.ToBase64String(region), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EntityRegion_TankRecordAnchor_ForwardsToCoordinator()
    {
        byte[] region = [0x11, 0x22, 0x33, 0x44];
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    ReplayTimeSeconds: 1.5,
                    RegionBytes: region,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 0,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: false)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 4,
                RegionAnchor = "entity-tank-record",
            },
            TestContext.CancellationToken);

        EntityRecordRegionReadResponse response = Value<EntityRecordRegionReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(
            EntityRecordRegionAnchor.EntityTankRecord,
            scanner.LastEntityRegionRequest?.RegionAnchor);
        Assert.AreEqual(Convert.ToBase64String(region), response.RegionBase64);
    }

    [TestMethod]
    public async Task EntityRegion_EntityBaseAnchor_ForwardsToCoordinator()
    {
        // The entity-base anchor (the statically-verified HP home at
        // [entity+0xB8] int16) parses and forwards to the coordinator.
        byte[] region = [0x11, 0x22, 0x33, 0x44];
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    ReplayTimeSeconds: 1.5,
                    RegionBytes: region,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 0,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: false)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 4,
                RegionAnchor = "entity-base",
            },
            TestContext.CancellationToken);

        EntityRecordRegionReadResponse response = Value<EntityRecordRegionReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(
            EntityRecordRegionAnchor.EntityBase,
            scanner.LastEntityRegionRequest?.RegionAnchor);
        Assert.AreEqual(Convert.ToBase64String(region), response.RegionBase64);
    }

    [TestMethod]
    public async Task EntityRegion_AvatarStatsAnchor_ForwardsCandidateIndexAndCount()
    {
        // The avatar-stats anchor (L3 damage-dealt pre-stage, 2026-08-12)
        // parses, forwards the candidate index, and echoes the candidate
        // count back so the session driver can loop every candidate.
        byte[] quad = new byte[16];
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    ReplayTimeSeconds: 1.5,
                    RegionBytes: quad,
                    FailureStage: null,
                    Attempts: 1,
                    NodesVisited: 0,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: false,
                    AvatarCandidateCount: 4)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 16,
                RegionAnchor = "avatar-stats",
                AvatarCandidateIndex = 2,
            },
            TestContext.CancellationToken);

        EntityRecordRegionReadResponse response = Value<EntityRecordRegionReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(
            EntityRecordRegionAnchor.AvatarStats,
            scanner.LastEntityRegionRequest?.RegionAnchor);
        Assert.AreEqual(2, scanner.LastEntityRegionRequest?.AvatarCandidateIndex);
        Assert.AreEqual(4, response.AvatarCandidateCount);
        Assert.AreEqual(Convert.ToBase64String(quad), response.RegionBase64);
    }

    [TestMethod]
    public async Task EntityRegion_PenOwnershipWalkAnchor_ForwardsIndexAndEchoesAggregateOnly()
    {
        // The pen-ownership-walk anchor (penetration v0.3 H1) parses, forwards
        // the candidate index, and echoes only the aggregate verdict booleans
        // back — no raw region bytes may leave the endpoint.
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(1),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    ReplayTimeSeconds: 1.5,
                    RegionBytes: null,
                    FailureStage: null,
                    Attempts: 2,
                    NodesVisited: 0,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: false,
                    PenOwnershipRotatorCandidateCount: 1,
                    PenOwnershipOwnerPointerReadable: true,
                    PenOwnershipForwardRoundTripConfirmed: true,
                    PenOwnershipGunVtableConfirmed: true,
                    PenOwnershipEntityHpPlausible: true,
                    PenOwnershipTwoPassStable: true)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 16,
                RegionAnchor = "pen-ownership-walk",
                OwnershipCandidateIndex = 3,
            },
            TestContext.CancellationToken);

        EntityRecordRegionReadResponse response = Value<EntityRecordRegionReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(
            EntityRecordRegionAnchor.PenOwnershipWalk,
            scanner.LastEntityRegionRequest?.RegionAnchor);
        Assert.AreEqual(3, scanner.LastEntityRegionRequest?.OwnershipCandidateIndex);
        Assert.AreEqual(1, response.PenOwnershipRotatorCandidateCount);
        Assert.IsTrue(response.PenOwnershipOwnerPointerReadable);
        Assert.IsTrue(response.PenOwnershipForwardRoundTripConfirmed);
        Assert.IsTrue(response.PenOwnershipGunVtableConfirmed);
        Assert.IsTrue(response.PenOwnershipEntityHpPlausible);
        Assert.IsTrue(response.PenOwnershipTwoPassStable);
        // Privacy: the ownership walk never carries raw region bytes.
        Assert.IsNull(response.RegionBase64);
    }

    [TestMethod]
    public async Task CameraPose_ResolvedReturnsPoseWithIdentityFlags()
    {
        var scanner = new FakeGameMemoryScanner
        {
            CameraPoseResult = OperationResult.Success(
                new CameraPoseReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(2),
                    "11.19.0.10",
                    CameraPoseStatus.Resolved,
                    FailureStage: null,
                    AvatarAddress: 0x10000100,
                    CameraAddress: 0x10000200,
                    CameraStateAddress: 0x10000300,
                    X: 10.5f,
                    Y: 20.25f,
                    Z: -3.5f,
                    YawRadians: 0.7f,
                    PitchRadians: -0.2f,
                    Basis: [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f],
                    AvatarIdentityVerified: true,
                    CameraIdentityVerified: true,
                    CameraStateIdentityVerified: true,
                    ConsistentDoubleRead: true,
                    ModuleRooted: true)),
        };

        IResult result = await GameApiEndpoints.DiscoverCameraPoseAsync(
            scanner,
            TestContext.CancellationToken);

        CameraPoseReadResponse response = Value<CameraPoseReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual("0x10000100", response.AvatarAddress);
        Assert.AreEqual("0x10000200", response.CameraAddress);
        Assert.AreEqual("0x10000300", response.CameraStateAddress);
        // The pose floats widen to double exactly; compare against the
        // widened float so the assertion is exact.
        Assert.AreEqual((double)10.5f, response.X);
        Assert.AreEqual((double)20.25f, response.Y);
        Assert.AreEqual((double)-3.5f, response.Z);
        Assert.AreEqual((double)0.7f, response.YawRadians);
        Assert.AreEqual((double)-0.2f, response.PitchRadians);
        Assert.IsNotNull(response.Basis);
        Assert.HasCount(9, response.Basis!);
        Assert.IsTrue(response.AvatarIdentityVerified);
        Assert.IsTrue(response.CameraIdentityVerified);
        Assert.IsTrue(response.CameraStateIdentityVerified);
        Assert.IsTrue(response.ConsistentDoubleRead);
        Assert.IsTrue(response.ModuleRooted);
        Assert.AreEqual(1, scanner.CreateCameraPoseCallCount);
    }

    [TestMethod]
    public async Task CameraPose_CoordinatorFailureIsPropagated()
    {
        var scanner = new FakeGameMemoryScanner
        {
            CameraPoseResult = OperationResult.Failure<CameraPoseReadResult>(
                new ApplicationError("discover.camera_pose.read_unavailable", "Test failure.")),
        };

        IResult result = await GameApiEndpoints.DiscoverCameraPoseAsync(
            scanner,
            TestContext.CancellationToken);

        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.camera_pose.read_unavailable",
            body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task EntityRegion_InvalidAnchorFailsClosed()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Success(
                new EntityRecordRegionReadResult(
                    DateTimeOffset.UnixEpoch,
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    4242,
                    null,
                    null,
                    null,
                    0,
                    0,
                    ModuleRooted: true,
                    EntityIdentityRevalidated: false,
                    ConsistentDoubleRead: false,
                    SameDecodedClockProven: false)),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 8,
                RegionAnchor = "scan-whole-process",
            },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.entity_region.invalid_anchor",
            response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastEntityRegionRequest);
    }

    [TestMethod]
    public async Task EntityRegion_FailureReturnsBadRequest()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionResult = OperationResult.Failure<EntityRecordRegionReadResult>(
                new ApplicationError(
                    "discover.entity_region.invalid_length",
                    "The region length must be within 1..4096 bytes.")),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRecordRegionReadRequest
            {
                EntityId = 4242,
                RegionLength = 5000,
            },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [TestMethod]
    public async Task EntityRoster_ReturnsAvatarIdsOnly()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRosterResult = OperationResult.Success(
                new EntityRosterReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(5),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    FailureStage: null,
                    CandidatesSeen: 18,
                    FilteredOut: 4,
                    ModuleRooted: true,
                    TraversalLimited: false,
                    EntityIds: [3760578, 3760577, 3760579])),
        };

        IResult result = await GameApiEndpoints.DiscoverEntityRosterAsync(
            scanner,
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.EntityRosterReadResponse response =
            Value<WotBTreader.ApiContracts.EntityRosterReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(18, response.CandidatesSeen);
        Assert.AreEqual(4, response.FilteredOut);
        Assert.IsTrue(response.ModuleRooted);
        Assert.IsFalse(response.TraversalLimited);
        int[] expected = [3760578, 3760577, 3760579];
        CollectionAssert.AreEqual(expected, response.EntityIds.ToArray());
        Assert.AreEqual(1, scanner.EntityRosterCallCount);
        // No absolute address or process id may leak in the serialized body.
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        Assert.IsFalse(json.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LiveFrame_ResponseCarriesReadPassMeasurement()
    {
        DateTimeOffset started = DateTimeOffset.Parse(
            "2026-08-11T10:00:00.000Z", CultureInfo.InvariantCulture);
        DateTimeOffset ended = DateTimeOffset.Parse(
            "2026-08-11T10:00:00.050Z", CultureInfo.InvariantCulture);
        DateTimeOffset clock = DateTimeOffset.Parse(
            "2026-08-11T10:00:00.040Z", CultureInfo.InvariantCulture);
        var scanner = new FakeGameMemoryScanner
        {
            LiveFrameResult = OperationResult.Success(
                new LiveFrameReadResult(
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    FailureStage: null,
                    ReplayTimeSeconds: 150.5,
                    SameDecodedClockProven: true,
                    Camera: null,
                    Tanks:
                    [
                        new LiveFrameTankState(
                            3760578,
                            Type10EntityPositionStatus.Resolved,
                            X: 0,
                            Y: 0,
                            Z: 100,
                            YawRadians: 0.5f,
                            HpCurrent: null,
                            HpMax: null,
                            Alive: null,
                            FailureStage: null,
                            ModuleRooted: true),
                    ],
                    RosterCandidatesSeen: 14,
                    RosterFilteredOut: 2,
                    Measurement: new LiveFrameReadMeasurement(
                        started,
                        ended,
                        clock))),
        };

        IResult result = await GameApiEndpoints.DiscoverLiveFrameAsync(
            scanner,
            new WotBTreader.ApiContracts.LiveFrameReadRequest(),
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.LiveFrameReadResponse response =
            Value<WotBTreader.ApiContracts.LiveFrameReadResponse>(result);
        Assert.IsNotNull(response.Measurement);
        Assert.AreEqual(
            started,
            response.Measurement.FrameStartedAtUtc);
        Assert.AreEqual(ended, response.Measurement.FrameEndedAtUtc);
        Assert.AreEqual(clock, response.Measurement.ClockSnapshotAtUtc);
        Assert.AreEqual(150.5, response.ReplayTimeSeconds!.Value, 1e-9);
        Assert.IsTrue(response.SameDecodedClockProven);
        Assert.AreEqual(1, scanner.LiveFrameCallCount);
        // Honest health: absent because the entity-base read was not
        // exercised in this fixture — and the WHY is surfaced.
        Assert.IsNull(response.Tanks.Single().HpCurrent);
        Assert.IsNull(response.Tanks.Single().HpMax);
        Assert.IsNull(response.Tanks.Single().Alive);
        Assert.IsNull(response.Tanks.Single().HpFailureStage);
    }

    [TestMethod]
    public async Task DiscoverLiveFrame_CarriesL1HealthAndFailureStage()
    {
        var scanner = new FakeGameMemoryScanner
        {
            LiveFrameResult = OperationResult.Success(
                new LiveFrameReadResult(
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    FailureStage: null,
                    ReplayTimeSeconds: 150.5,
                    SameDecodedClockProven: true,
                    Camera: null,
                    Tanks:
                    [
                        new LiveFrameTankState(
                            3760578,
                            Type10EntityPositionStatus.Resolved,
                            X: 0,
                            Y: 0,
                            Z: 100,
                            YawRadians: 0.5f,
                            HpCurrent: 1228,
                            HpMax: 1550,
                            Alive: true,
                            FailureStage: null,
                            ModuleRooted: true),
                        new LiveFrameTankState(
                            3760579,
                            Type10EntityPositionStatus.Resolved,
                            X: 10,
                            Y: 0,
                            Z: 90,
                            YawRadians: 0f,
                            HpCurrent: null,
                            HpMax: null,
                            Alive: null,
                            FailureStage: null,
                            ModuleRooted: true,
                            HpFailureStage: "entity-base-read"),
                    ],
                    RosterCandidatesSeen: 14,
                    RosterFilteredOut: 2)),
        };

        IResult result = await GameApiEndpoints.DiscoverLiveFrameAsync(
            scanner,
            new WotBTreader.ApiContracts.LiveFrameReadRequest(),
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.LiveFrameReadResponse response =
            Value<WotBTreader.ApiContracts.LiveFrameReadResponse>(result);
        Assert.HasCount(2, response.Tanks);
        WotBTreader.ApiContracts.LiveFrameTankResponse healthy = response.Tanks[0];
        Assert.AreEqual(1228.0, healthy.HpCurrent!.Value, 1e-9);
        Assert.AreEqual(1550.0, healthy.HpMax!.Value, 1e-9);
        Assert.IsTrue(healthy.Alive);
        Assert.IsNull(healthy.HpFailureStage);
        WotBTreader.ApiContracts.LiveFrameTankResponse failed = response.Tanks[1];
        Assert.IsNull(failed.HpCurrent);
        Assert.AreEqual("entity-base-read", failed.HpFailureStage);
    }

    [TestMethod]
    public async Task EntityRoster_TraversalLimitedFailsClosed()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRosterResult = OperationResult.Success(
                new EntityRosterReadResult(
                    DateTimeOffset.UnixEpoch,
                    "11.19.0.10",
                    Type10EntityPositionStatus.TraversalLimitExceeded,
                    FailureStage: "tree-traversal",
                    CandidatesSeen: 0,
                    FilteredOut: 0,
                    ModuleRooted: false,
                    TraversalLimited: true,
                    EntityIds: [])),
        };

        IResult result = await GameApiEndpoints.DiscoverEntityRosterAsync(
            scanner,
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.EntityRosterReadResponse response =
            Value<WotBTreader.ApiContracts.EntityRosterReadResponse>(result);
        Assert.AreEqual("TraversalLimitExceeded", response.Status);
        Assert.AreEqual("tree-traversal", response.FailureStage);
        Assert.IsTrue(response.TraversalLimited);
        Assert.IsEmpty(response.EntityIds);
    }

    [TestMethod]
    public async Task EntityRoster_FailureReturnsBadRequest()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRosterResult = OperationResult.Failure<EntityRosterReadResult>(
                new ApplicationError(
                    "discover.entity_roster.read_unavailable",
                    "The guarded roster reader is unavailable.")),
        };

        IResult result = await GameApiEndpoints.DiscoverEntityRosterAsync(
            scanner,
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.entity_roster.read_unavailable",
            body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task EntityRegions_ReturnsBatchResponseWithBase64Bytes()
    {
        byte[] first = [0x10, 0x20, 0x30, 0x40];
        byte[] second = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionsResult = OperationResult.Success(
                new EntityRegionsReadResult(
                    DateTimeOffset.UnixEpoch.AddSeconds(7),
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    ReplayTimeSeconds: 42.5,
                    SameDecodedClockProven: true,
                    [
                        new EntityRegionReadResultItem(
                            4242,
                            Type10EntityPositionStatus.Resolved,
                            ReplayTimeSeconds: 42.5,
                            RegionBytes: first,
                            FailureStage: null,
                            Attempts: 1,
                            NodesVisited: 3,
                            ModuleRooted: true,
                            EntityIdentityRevalidated: false,
                            ConsistentDoubleRead: true,
                            EntityBaseRegionBytes: [0xB8, 0x04],
                            EntityBaseFailureStage: null,
                            EntityBaseAttempts: 1,
                            RegionReadAttempts: 2,
                            RegionTearObserved: true,
                            EntityBaseTearObserved: false),
                        new EntityRegionReadResultItem(
                            4243,
                            Type10EntityPositionStatus.EntityNotFound,
                            ReplayTimeSeconds: 42.5,
                            RegionBytes: null,
                            FailureStage: "entity-lookup",
                            Attempts: 3,
                            NodesVisited: 2,
                            ModuleRooted: true,
                            EntityIdentityRevalidated: false,
                            ConsistentDoubleRead: false),
                    ],
                    Measurement: new EntityRegionsReadMeasurement(
                        DateTimeOffset.UnixEpoch.AddSeconds(1),
                        DateTimeOffset.UnixEpoch.AddSeconds(2),
                        DateTimeOffset.UnixEpoch.AddSeconds(3)))),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionsAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRegionsReadRequest
            {
                Entities =
                [
                    new WotBTreader.ApiContracts.EntityRegionReadItemRequest
                    {
                        EntityId = 4242,
                        RegionLength = 4,
                        RegionAnchor = "entity-base",
                        EntityBaseRegionLength = 0x120,
                    },
                    new WotBTreader.ApiContracts.EntityRegionReadItemRequest
                    {
                        EntityId = 4243,
                        RegionLength = 5,
                    },
                ],
                BattleSessionId = "019fa431-5ace-78ce-ba92-cd825ff9911c",
            },
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.EntityRegionsReadResponse response =
            Value<WotBTreader.ApiContracts.EntityRegionsReadResponse>(result);
        Assert.AreEqual("Resolved", response.Status);
        Assert.AreEqual(42.5, response.ReplayTimeSeconds);
        Assert.IsTrue(response.SameDecodedClockProven);
        Assert.HasCount(2, response.Regions);
        Assert.AreEqual(4242, response.Regions[0].EntityId);
        Assert.AreEqual("Resolved", response.Regions[0].Status);
        Assert.AreEqual(Convert.ToBase64String(first), response.Regions[0].RegionBase64);
        // The L1 entity-base region round-trips with its own bytes/failure.
        Assert.AreEqual(
            Convert.ToBase64String([0xB8, 0x04]),
            response.Regions[0].EntityBaseRegionBase64);
        Assert.IsNull(response.Regions[0].EntityBaseFailureStage);
        Assert.AreEqual(1, response.Regions[0].EntityBaseAttempts);
        Assert.IsTrue(response.Regions[0].ConsistentDoubleRead);
        Assert.AreEqual(2, response.Regions[0].RegionReadAttempts);
        Assert.IsTrue(response.Regions[0].RegionTearObserved);
        Assert.IsFalse(response.Regions[0].EntityBaseTearObserved);
        Assert.IsNull(response.Regions[1].EntityBaseRegionBase64);
        Assert.AreEqual("EntityNotFound", response.Regions[1].Status);
        Assert.AreEqual("entity-lookup", response.Regions[1].FailureStage);
        Assert.IsNull(response.Regions[1].RegionBase64);
        // The read-pass measurement is mapped through.
        Assert.IsNotNull(response.Measurement);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            response.Measurement?.BatchStartedAtUtc);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            response.Measurement?.BatchEndedAtUtc);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch.AddSeconds(3),
            response.Measurement?.ClockSnapshotAtUtc);
        // The batch request forwarded: both entities + the session id.
        WotBTreader.Application.Game.EntityRegionsReadRequest? forwarded =
            scanner.LastEntityRegionsRequest;
        Assert.IsNotNull(forwarded);
        Assert.HasCount(2, forwarded.Entities);
        Assert.AreEqual(
            EntityRecordRegionAnchor.EntityBase,
            forwarded.Entities[0].RegionAnchor);
        Assert.AreEqual(0x120, forwarded.Entities[0].EntityBaseRegionLength);
        Assert.IsNull(forwarded.Entities[1].EntityBaseRegionLength);
        Assert.AreEqual(4243, forwarded.Entities[1].EntityId);
        Assert.IsNotNull(forwarded.BattleSessionId);
        // No absolute address may leak in the serialized response.
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        Assert.IsFalse(json.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(json.Contains(Convert.ToBase64String(first), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EntityRegions_InvalidAnchorFailsClosed()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionsResult = OperationResult.Success(
                new EntityRegionsReadResult(
                    DateTimeOffset.UnixEpoch,
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    null,
                    false,
                    [])),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionsAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRegionsReadRequest
            {
                Entities =
                [
                    new WotBTreader.ApiContracts.EntityRegionReadItemRequest
                    {
                        EntityId = 4242,
                        RegionLength = 8,
                        RegionAnchor = "scan-whole-process",
                    },
                ],
            },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.entity_regions.invalid_anchor",
            body.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastEntityRegionsRequest);
    }

    [TestMethod]
    public async Task EntityRegions_EmptyEntitiesFailsClosed()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionsResult = OperationResult.Success(
                new EntityRegionsReadResult(
                    DateTimeOffset.UnixEpoch,
                    "11.19.0.10",
                    Type10EntityPositionStatus.Resolved,
                    null,
                    false,
                    [])),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionsAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRegionsReadRequest(),
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.entity_regions.invalid_request",
            body.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastEntityRegionsRequest);
    }

    [TestMethod]
    public async Task EntityRegions_FailureReturnsBadRequest()
    {
        var scanner = new FakeGameMemoryScanner
        {
            EntityRegionsResult = OperationResult.Failure<EntityRegionsReadResult>(
                new ApplicationError(
                    "discover.entity_regions.read_unavailable",
                    "The guarded entity-region reader is unavailable.")),
        };

        IResult result = await GameApiEndpoints.ReadEntityRegionsAsync(
            scanner,
            new WotBTreader.ApiContracts.EntityRegionsReadRequest
            {
                Entities =
                [
                    new WotBTreader.ApiContracts.EntityRegionReadItemRequest
                    {
                        EntityId = 4242,
                        RegionLength = 8,
                    },
                ],
            },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.entity_regions.read_unavailable",
            body.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task InstructionSnapshotForwardsOnlyBoundsAndProjectsSafeEvidence()
    {
        DateTimeOffset capturedAt = DateTimeOffset.UnixEpoch.AddSeconds(5);
        var scanner = new FakeGameMemoryScanner
        {
            InstructionSnapshotResult = OperationResult.Success(
                new WotBTreader.Application.Game.InstructionSnapshotResult(
                    DateTimeOffset.UnixEpoch,
                    capturedAt,
                    "completed",
                    "wotblitz.exe",
                    0x22FA78D,
                    InstructionFingerprintMatched: true,
                    CleanupProven: true,
                    Truncated: false,
                    [
                        new WotBTreader.Application.Game.InstructionSnapshotHit(
                            1,
                            "object-01",
                            capturedAt,
                            ReplayEntityIdReadOk: true,
                            ReplayEntityId: 4242,
                            ReadOk: true,
                            Finite: true,
                            1f,
                            2f,
                            3f,
                            SameDebugEvent: true,
                            SingleRead12Bytes: true,
                            ObjectRegisterCaptured: true,
                            HardwareAtomicReadProven: false,
                            SameDecodedClockProven: false,
                            ViewpointIdentityProven: false,
                            StableRootProven: false),
                    ])),
        };

        IResult result = await GameApiEndpoints.CaptureInstructionSnapshotAsync(
            scanner,
            new WotBTreader.ApiContracts.InstructionSnapshotRequest
            {
                DurationMilliseconds = 2_000,
                MaxHits = 8,
            },
            TestContext.CancellationToken);

        InstructionSnapshotResponse response = Value<InstructionSnapshotResponse>(result);
        Assert.AreEqual(2_000, scanner.LastInstructionSnapshotRequest!.DurationMilliseconds);
        Assert.AreEqual(8, scanner.LastInstructionSnapshotRequest.MaxHits);
        Assert.AreEqual("0x22FA78D", response.TargetRva);
        Assert.AreEqual(1, response.HitCount);
        Assert.IsTrue(response.Hits[0].ReplayEntityIdReadOk);
        Assert.AreEqual(4242, response.Hits[0].ReplayEntityId);
        Assert.IsFalse(response.Hits[0].ViewpointIdentityProven);
        string json = JsonSerializer.Serialize(response, CamelCaseJson);
        Assert.IsTrue(json.Contains("replayEntityId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("processId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("objectAddress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("entityAddress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("readAddress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("absoluteAddress", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("instructionHex", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task InstructionSnapshotRejectsOutOfRangeBoundsWithoutCallingPort()
    {
        var scanner = new FakeGameMemoryScanner();

        IResult result = await GameApiEndpoints.CaptureInstructionSnapshotAsync(
            scanner,
            new WotBTreader.ApiContracts.InstructionSnapshotRequest
            {
                DurationMilliseconds = 5_001,
                MaxHits = 65,
            },
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual(
            "discover.instruction_snapshot.invalid_options",
            response.GetProperty("error").GetString());
        Assert.IsNull(scanner.LastInstructionSnapshotRequest);
    }

    [TestMethod]
    public async Task TrajectoryReturnsGroundTruthWithViewpointFlag()
    {
        Guid sessionId = Guid.NewGuid();
        var provider = new FakeTrajectoryProvider(OperationResult.Success(
            new TrajectoryGroundTruth(
                100_000_000,
                [
                    new EntityTrajectory(
                        new ParticipantId(Guid.NewGuid()),
                        EntityId: 42,
                        "T-54",
                        IsViewpoint: true,
                        [new TrajectorySample(0, 1, 2, 3), new TrajectorySample(10_000_000, 5, 6, 7)]),
                ])));

        IResult result = await GameApiEndpoints.GetTrajectoryAsync(
            provider,
            sessionId,
            TestContext.CancellationToken);

        TrajectoryResponse response = Value<TrajectoryResponse>(result);
        Assert.AreEqual(sessionId, response.BattleSessionId);
        Assert.AreEqual(100_000_000L, response.DurationTicks);
        Assert.HasCount(1, response.Entities);
        Assert.IsTrue(response.Entities[0].IsViewpoint);
        Assert.AreEqual(42L, response.Entities[0].EntityId);
        Assert.AreEqual("T-54", response.Entities[0].TankName);
        Assert.HasCount(2, response.Entities[0].Samples);
        Assert.AreEqual(10_000_000L, response.Entities[0].Samples[1].ReplayTimeTicks);
    }

    [TestMethod]
    public async Task ScorePenOffline_ReturnsReportForScoredSession()
    {
        Guid sessionId = Guid.NewGuid();
        var projection = new ReplayDecodeProjection(
            new DecodeRun(
                DecodeRunId.New(),
                SourceArtifactId.New(),
                DecoderId: "test",
                DecoderVersion: "1",
                SchemaVersion: "1",
                DecodeRunStatus.Succeeded,
                ReplayCapability.Positions,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                FailureCode: null,
                FailureSummary: null),
            new BattleSession(
                BattleSessionId.New(),
                DecodeRunId.New(),
                GameVersion: "11.19.0.10",
                ArenaIdentity: null,
                MapId: null,
                MapName: null,
                BattleTimeUtc: null,
                Duration: null,
                ViewpointParticipantId: null,
                SchemaVersion: "1"),
            Participants: [],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
        var sessions = new FakeSessionQueryRepository(
            OperationResult.Success(projection));
        var scorer = new FakePenOfflineScorer(
            new OfflinePenScoreReport(
                SkippedShots: 0,
                new PenValidationReport(
                    TotalShots: 1,
                    PredictedRicochet: 0,
                    RicochetAgreements: 0,
                    RicochetPrecision: 0,
                    ClassifiedShots: 1,
                    BandAgreements: 1,
                    BandAccuracy: 1.0,
                    Rows:
                    [
                        new PenValidationShotRow(
                            Penetrated: true,
                            PredictedRicochet: false,
                            Band: PenetrationBand.Pen,
                            IncidenceDegrees: 0,
                            EffectiveArmorMm: 50,
                            PenetrationMmAtRange: 200),
                    ]),
                Shots: []));

        IResult result = await GameApiEndpoints.ScorePenOfflineAsync(
            scorer,
            sessions,
            sessionId,
            request: null,
            TestContext.CancellationToken);

        OfflinePenScoreReport response = Value<OfflinePenScoreReport>(result);
        Assert.AreEqual(1, response.Validation.TotalShots);
        Assert.AreEqual(1.0, response.Validation.BandAccuracy, 1e-9);
        Assert.IsNotNull(scorer.LastProjection);
        Assert.IsNull(scorer.LastAimOverrides);
        Assert.AreEqual(sessionId, sessions.LastSessionId?.Value);
    }

    [TestMethod]
    public async Task ScorePenOffline_ForwardsAimOverridesToScorer()
    {
        Guid sessionId = Guid.NewGuid();
        var projection = new ReplayDecodeProjection(
            new DecodeRun(
                DecodeRunId.New(),
                SourceArtifactId.New(),
                DecoderId: "test",
                DecoderVersion: "1",
                SchemaVersion: "1",
                DecodeRunStatus.Succeeded,
                ReplayCapability.Positions,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                FailureCode: null,
                FailureSummary: null),
            new BattleSession(
                BattleSessionId.New(),
                DecodeRunId.New(),
                GameVersion: "11.19.0.10",
                ArenaIdentity: null,
                MapId: null,
                MapName: null,
                BattleTimeUtc: null,
                Duration: null,
                ViewpointParticipantId: null,
                SchemaVersion: "1"),
            Participants: [],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
        var sessions = new FakeSessionQueryRepository(OperationResult.Success(projection));
        var scorer = new FakePenOfflineScorer(
            new OfflinePenScoreReport(0, new PenValidationReport(0, 0, 0, 0, 0, 0, 0, []), []));
        var request = new PenOfflineScoreRequest
        {
            AimOverrides =
            [
                new AimSampleRequest
                {
                    ReplayTimeTicks = 10_000_000L,
                    OriginX = 1.0,
                    OriginY = 2.0,
                    OriginZ = 3.0,
                    DirectionX = 0.0,
                    DirectionY = 0.0,
                    DirectionZ = -1.0,
                },
            ],
        };

        IResult result = await GameApiEndpoints.ScorePenOfflineAsync(
            scorer,
            sessions,
            sessionId,
            request,
            TestContext.CancellationToken);

        Assert.IsInstanceOfType<Ok<OfflinePenScoreReport>>(result);
        Assert.IsNotNull(scorer.LastAimOverrides);
        Assert.HasCount(1, scorer.LastAimOverrides);
        AimSample mapped = scorer.LastAimOverrides[0];
        Assert.AreEqual(TimeSpan.FromTicks(10_000_000L), mapped.ReplayTime);
        Assert.AreEqual(1.0, mapped.Aim.OriginX, 1e-9);
        Assert.AreEqual(2.0, mapped.Aim.OriginY, 1e-9);
        Assert.AreEqual(3.0, mapped.Aim.OriginZ, 1e-9);
        Assert.AreEqual(0.0, mapped.Aim.DirectionX, 1e-9);
        Assert.AreEqual(0.0, mapped.Aim.DirectionY, 1e-9);
        Assert.AreEqual(-1.0, mapped.Aim.DirectionZ, 1e-9);
    }

    [TestMethod]
    public async Task ScorePenOffline_RejectsInvalidAimOverrideBeforeLoadingSession()
    {
        var sessions = new FakeSessionQueryRepository(
            OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError("storage.session.not_found", "No such session.", Retryable: false)));
        var scorer = new FakePenOfflineScorer(
            new OfflinePenScoreReport(0, new PenValidationReport(0, 0, 0, 0, 0, 0, 0, []), []));
        var request = new PenOfflineScoreRequest
        {
            AimOverrides =
            [
                new AimSampleRequest
                {
                    ReplayTimeTicks = -1,
                    OriginX = double.NaN,
                    OriginY = 0,
                    OriginZ = 0,
                    DirectionX = 0,
                    DirectionY = 0,
                    DirectionZ = -1,
                },
            ],
        };

        IResult result = await GameApiEndpoints.ScorePenOfflineAsync(
            scorer,
            sessions,
            Guid.NewGuid(),
            request,
            TestContext.CancellationToken);

        JsonElement response = BadRequestAnonymous(result);
        Assert.AreEqual("discover.pen_invalid_aim_overrides", response.GetProperty("error").GetString());
        Assert.IsNull(sessions.LastSessionId);
        Assert.IsNull(scorer.LastProjection);
    }

    [TestMethod]
    public async Task ScorePenOffline_NotFoundForUnknownSession()
    {
        var sessions = new FakeSessionQueryRepository(
            OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError("storage.session.not_found", "No such session.", Retryable: false)));
        var scorer = new FakePenOfflineScorer(
            new OfflinePenScoreReport(0, new PenValidationReport(0, 0, 0, 0, 0, 0, 0, []), []));

        IResult result = await GameApiEndpoints.ScorePenOfflineAsync(
            scorer,
            sessions,
            Guid.NewGuid(),
            request: null,
            TestContext.CancellationToken);

        JsonElement response = NotFoundAnonymous(result);
        Assert.AreEqual("storage.session.not_found", response.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task TrajectoryNotFoundForUnknownSession()
    {
        var provider = new FakeTrajectoryProvider(OperationResult.Failure<TrajectoryGroundTruth>(
            new ApplicationError("storage.not_found", "Battle session not found.", Retryable: false)));

        IResult result = await GameApiEndpoints.GetTrajectoryAsync(
            provider,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        JsonElement response = NotFoundAnonymous(result);
        Assert.AreEqual("storage.not_found", response.GetProperty("error").GetString());
    }

    private static TrajectoryGroundTruth VShapeGroundTruth()
    {
        // Viewpoint entity: x follows a V shape (0 -> 100 at 50s -> 0 at 100s)
        // so phase-shifted linear copies cannot reproduce it; y climbs linearly.
        Guid viewpoint = Guid.NewGuid();
        List<TrajectorySample> samples =
        [
            new(0, 0, 0, 100),
            new(250_000_000, 50, 10, 100),
            new(500_000_000, 100, 20, 100),
            new(750_000_000, 50, 30, 100),
            new(1_000_000_000, 0, 40, 100),
        ];
        return new TrajectoryGroundTruth(
            100_000_000,
            [
                new EntityTrajectory(new ParticipantId(viewpoint), 1, "Viewpoint", true, samples),
                new EntityTrajectory(new ParticipantId(Guid.NewGuid()), 2, "Stationary", false,
                    [new TrajectorySample(0, 7, 7, 7), new TrajectorySample(100_000_000, 7, 7, 7)]),
            ]);
    }

    private static TrajectoryGroundTruth AllAxesGroundTruth()
    {
        // Viewpoint entity with ALL THREE axes moving: x follows a V shape
        // (slope 2/s), y climbs at 0.4/s, z climbs at 0.8/s. Distinct slopes
        // keep each observation series attributable to its own axis, and every
        // axis scores, so the correlate pass yields the clean COMPLETE x/y/z
        // triple (the VShape fixture's z is constant and never scores).
        Guid viewpoint = Guid.NewGuid();
        List<TrajectorySample> samples =
        [
            new(0, 0, 0, 0),
            new(250_000_000, 50, 10, 20),
            new(500_000_000, 100, 20, 40),
            new(750_000_000, 50, 30, 60),
            new(1_000_000_000, 0, 40, 80),
        ];
        return new TrajectoryGroundTruth(
            100_000_000,
            [
                new EntityTrajectory(new ParticipantId(viewpoint), 1, "Viewpoint", true, samples),
                new EntityTrajectory(new ParticipantId(Guid.NewGuid()), 2, "Stationary", false,
                    [new TrajectorySample(0, 7, 7, 7), new TrajectorySample(100_000_000, 7, 7, 7)]),
            ]);
    }

    [TestMethod]
    public async Task CorrelateScoresTheReproducingAddress()
    {
        DateTimeOffset start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var provider = new FakeTrajectoryProvider(OperationResult.Success(VShapeGroundTruth()));
        List<CorrelationSampleRequest> xSamples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            xSamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 2));
        }

        IResult result = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = start,
                TolerancePerAxis = 5,
                MaxTimeShiftSeconds = 8,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1000", xSamples),
                    new CorrelationSeriesRequest("0x2000",
                    [
                        new CorrelationSampleRequest(start.AddSeconds(10), 7),
                        new CorrelationSampleRequest(start.AddSeconds(20), 7),
                        new CorrelationSampleRequest(start.AddSeconds(30), 7),
                    ]),
                    new CorrelationSeriesRequest("0x3000",
                    [
                        new CorrelationSampleRequest(start.AddSeconds(10), 110),
                        new CorrelationSampleRequest(start.AddSeconds(20), 120),
                        new CorrelationSampleRequest(start.AddSeconds(30), 130),
                        new CorrelationSampleRequest(start.AddSeconds(40), 140),
                        new CorrelationSampleRequest(start.AddSeconds(50), 150),
                    ]),
                ],
            },
            TestContext.CancellationToken);

        CorrelateResponse response = Value<CorrelateResponse>(result);
        Assert.HasCount(1, response.Results);
        CorrelateResultItemResponse best = response.Results[0];
        Assert.AreEqual("0x1000", best.Address);
        Assert.AreEqual("x", best.Axis);
        Assert.AreEqual(1, best.Sign);
        Assert.AreEqual(0.0, best.ShiftSeconds, 0.001);
        Assert.AreEqual(5, best.MatchCount);
        Assert.AreEqual(1.0, best.Score, 0.001);
        Assert.AreEqual(1L, best.EntityId);
    }

    [TestMethod]
    public async Task CorrelateFindsSignFlippedAxis()
    {
        DateTimeOffset start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var provider = new FakeTrajectoryProvider(OperationResult.Success(VShapeGroundTruth()));
        // y climbs 0 -> 40 over the battle; a memory copy may store -y.
        List<CorrelationSampleRequest> samples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            samples.Add(new CorrelationSampleRequest(start.AddSeconds(second), -(second * 0.4)));
        }

        IResult result = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = start,
                TolerancePerAxis = 2,
                MaxTimeShiftSeconds = 8,
                Observations =
                [
                    new CorrelationSeriesRequest("0x4000", samples),
                ],
            },
            TestContext.CancellationToken);

        CorrelateResponse response = Value<CorrelateResponse>(result);
        Assert.HasCount(1, response.Results);
        Assert.AreEqual("y", response.Results[0].Axis);
        Assert.AreEqual(-1, response.Results[0].Sign);
    }

    [TestMethod]
    public async Task CorrelateResponseIncludesFamilyMapping()
    {
        DateTimeOffset start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var provider = new FakeTrajectoryProvider(OperationResult.Success(VShapeGroundTruth()));
        List<CorrelationSampleRequest> xSamples = [];
        List<CorrelationSampleRequest> ySamples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            xSamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 2));
            // y(t) = 0.4 * second over the same wall window.
            ySamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 0.4));
        }

        IResult result = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = start,
                TolerancePerAxis = 5,
                MaxTimeShiftSeconds = 8,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1000", xSamples),
                    new CorrelationSeriesRequest("0x1004", ySamples),
                    // A moving far decoy that reproduces nothing (values far
                    // above the ground truth, same walls): must not score and
                    // must not join the family.
                    new CorrelationSeriesRequest("0x9000", xSamples.Select(sample => sample with
                    {
                        Value = sample.Value + 1000,
                    }).ToList()),
                ],
            },
            TestContext.CancellationToken);

        CorrelateResponse response = Value<CorrelateResponse>(result);
        Assert.HasCount(2, response.Results);
        Assert.HasCount(1, response.Families);
        TrajectoryFamilyResponse family = response.Families[0];
        Assert.AreEqual("0x1000", family.BaseAddress);
        Assert.AreEqual(4, family.SpanBytes);
        // The z ground axis is stationary in this fixture, so the family is
        // the x/y pair: reported, but not the clean complete triple.
        Assert.IsFalse(family.Complete);
        Assert.IsTrue(family.AxesCovered.SequenceEqual(["x", "y"]));
        Assert.HasCount(2, family.Members);
        Assert.AreEqual("0x1000", family.Members[0].Address);
        Assert.AreEqual(0, family.Members[0].OffsetBytes);
        Assert.AreEqual("x", family.Members[0].Axis);
        Assert.AreEqual("0x1004", family.Members[1].Address);
        Assert.AreEqual(4, family.Members[1].OffsetBytes);
        Assert.AreEqual("y", family.Members[1].Axis);
    }

    [TestMethod]
    public async Task CompleteFamilyReportMatchesTheWriteTraceParseContract()
    {
        // The od-048 correlate response that feeds the M2 write-trace driver:
        // one COMPLETE family (x/y/z triple). Uppercase-hex observation
        // addresses are preserved verbatim into the family members, which is
        // exactly the mixed case x64dbg-write-trace.ps1 must handle.
        DateTimeOffset start = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var provider = new FakeTrajectoryProvider(OperationResult.Success(AllAxesGroundTruth()));
        List<CorrelationSampleRequest> xSamples = [];
        List<CorrelationSampleRequest> ySamples = [];
        List<CorrelationSampleRequest> zSamples = [];
        for (int index = 0; index < 5; index++)
        {
            int second = 10 + (index * 10);
            xSamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 2));
            ySamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 0.4));
            zSamples.Add(new CorrelationSampleRequest(start.AddSeconds(second), second * 0.8));
        }

        IResult result = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = start,
                TolerancePerAxis = 2,
                MaxTimeShiftSeconds = 8,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1A2B3000", xSamples),
                    new CorrelationSeriesRequest("0x1A2B3004", ySamples),
                    new CorrelationSeriesRequest("0x1A2B3008", zSamples),
                ],
            },
            TestContext.CancellationToken);

        CorrelateResponse response = Value<CorrelateResponse>(result);
        Assert.HasCount(3, response.Results);
        Assert.HasCount(1, response.Families);

        // Serialize the way the wire does (camelCase, exactly the JSON the
        // od-048 report's `families` section is built from and the write-trace
        // script parses with ConvertFrom-Json).
        JsonElement wire = JsonSerializer.SerializeToElement(response, CamelCaseJson);
        JsonElement family = wire.GetProperty("families")[0];

        // Family-level fields the report carries: baseAddress (mixed-case hex
        // from the :X format the od-048 report serializes), spanBytes,
        // complete, axesCovered.
        Assert.AreEqual("0x1A2B3000", family.GetProperty("baseAddress").GetString());
        Assert.AreEqual(8, family.GetProperty("spanBytes").GetInt32());
        Assert.IsTrue(family.GetProperty("complete").GetBoolean());
        string[] axes = family.GetProperty("axesCovered")
            .EnumerateArray().Select(element => element.GetString()!).ToArray();
        Assert.IsTrue(axes.SequenceEqual(["x", "y", "z"]));

        // Member-level shape: the script reads address (ConvertTo-HexToken),
        // offsetBytes (arm-plan ordering), axis, score, sign, edgeAligned.
        JsonElement members = family.GetProperty("members");
        Assert.AreEqual(3, members.GetArrayLength());
        string[] addresses = ["0x1A2B3000", "0x1A2B3004", "0x1A2B3008"];
        int[] offsets = [0, 4, 8];
        string[] memberAxes = ["x", "y", "z"];
        for (int index = 0; index < 3; index++)
        {
            JsonElement member = members[index];
            // Address preserved verbatim (scorer -> builder -> wire).
            Assert.AreEqual(addresses[index], member.GetProperty("address").GetString());
            Assert.AreEqual(offsets[index], member.GetProperty("offsetBytes").GetInt32());
            Assert.AreEqual(memberAxes[index], member.GetProperty("axis").GetString());
            Assert.IsGreaterThan(0.0, member.GetProperty("score").GetDouble());
            Assert.AreEqual(1, member.GetProperty("sign").GetInt32());
            // No edge-aligned member: the triple stays the clean artifact.
            Assert.IsFalse(member.GetProperty("edgeAligned").GetBoolean());
        }

        // Every serialized address must parse with the write-trace script's
        // ConvertTo-HexToken regex: ^(0x)?([0-9a-fA-F]{4,16})$ — 0x-prefixed,
        // 4-16 hex digits, any case.
        foreach (JsonElement member in members.EnumerateArray())
        {
            StringAssert.Matches(
                member.GetProperty("address").GetString()!,
                WriteTraceAddressRegex);
        }

        // Case-insensitive hit-address matching: the script keys both the
        // armed address and the hit filename addr with ToLowerInvariant
        // (Get-FamilyArmPlan dedup + $parts[0].ToLowerInvariant() -eq
        // $addrKey), so a savedata hit that rendered the address in a
        // different case must still attribute to the same member, and a
        // different member must never collide.
        Assert.AreEqual(
            WriteTraceAddressKey("0x1A2B3000"),
            WriteTraceAddressKey("0x1a2b3000"));   // flipped case, same address
        Assert.AreEqual(
            WriteTraceAddressKey("0x1A2B3004"),
            WriteTraceAddressKey("0x1a2b3004"));
        Assert.AreNotEqual(
            WriteTraceAddressKey("0x1A2B3000"),
            WriteTraceAddressKey("0x1a2b3004"));   // neighbor must not collide
    }

    [TestMethod]
    public async Task CorrelateRejectsInvalidOptions()
    {
        var provider = new FakeTrajectoryProvider(OperationResult.Success(VShapeGroundTruth()));

        IResult nanTolerance = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.UtcNow,
                TolerancePerAxis = double.NaN,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1000",
                        [new CorrelationSampleRequest(DateTimeOffset.UtcNow, 1)]),
                ],
            },
            TestContext.CancellationToken);
        JsonElement response = BadRequestAnonymous(nanTolerance);
        Assert.AreEqual("discover.invalid_options", response.GetProperty("error").GetString());

        IResult empty = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.UtcNow,
                Observations = [],
            },
            TestContext.CancellationToken);
        Assert.AreEqual("discover.invalid_options",
            BadRequestAnonymous(empty).GetProperty("error").GetString());

        // A default (epoch) anchor would silently clamp every sample to the
        // last ground-truth value — meaningless evidence. It must be rejected.
        IResult defaultAnchor = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.MinValue,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1000",
                        [new CorrelationSampleRequest(DateTimeOffset.UtcNow, 1)]),
                ],
            },
            TestContext.CancellationToken);
        Assert.AreEqual("discover.invalid_options",
            BadRequestAnonymous(defaultAnchor).GetProperty("error").GetString());

        // A null series address must be a 400, not a 500: validation must
        // null-check before any member access.
        IResult nullAddress = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.UtcNow,
                Observations =
                [
                    new CorrelationSeriesRequest(null!,
                        [new CorrelationSampleRequest(DateTimeOffset.UtcNow, 1)]),
                ],
            },
            TestContext.CancellationToken);
        Assert.AreEqual("discover.invalid_options",
            BadRequestAnonymous(nullAddress).GetProperty("error").GetString());

        // A non-hex series address must be rejected, matching /discover/read.
        IResult nonHexAddress = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.UtcNow,
                Observations =
                [
                    new CorrelationSeriesRequest("not-an-address",
                        [new CorrelationSampleRequest(DateTimeOffset.UtcNow, 1)]),
                ],
            },
            TestContext.CancellationToken);
        Assert.AreEqual("discover.invalid_options",
            BadRequestAnonymous(nonHexAddress).GetProperty("error").GetString());

        // A non-positive shift step must be rejected.
        IResult badShiftStep = await GameApiEndpoints.CorrelateAsync(
            provider,
            new CorrelateRequest
            {
                GroundTruthSessionId = Guid.NewGuid(),
                ReplayStartWallTimeUtc = DateTimeOffset.UtcNow,
                ShiftStepSeconds = 0,
                Observations =
                [
                    new CorrelationSeriesRequest("0x1000",
                        [new CorrelationSampleRequest(DateTimeOffset.UtcNow, 1)]),
                ],
            },
            TestContext.CancellationToken);
        Assert.AreEqual("discover.invalid_options",
            BadRequestAnonymous(badShiftStep).GetProperty("error").GetString());
    }

    // Mirrors x64dbg-write-trace.ps1 ConvertTo-HexToken: ^(0x)?([0-9a-fA-F]{4,16})$
    // (case-insensitive, 0x-prefix optional, 4-16 hex digits) — the same regex
    // the script uses to normalize every family member and survivor address.
    private static readonly Regex WriteTraceAddressRegex =
        new(@"^(0x)?([0-9a-fA-F]{4,16})$", RegexOptions.Compiled);

    // Mirrors the script's address KEYING for dedup and hit attribution:
    // normalize with ConvertTo-HexToken semantics then compare lowercased
    // (Get-FamilyArmPlan $seen.ContainsKey($addr.ToLowerInvariant()) and the
    // family report's $parts[0].ToLowerInvariant() -eq $addrKey).
    private static string WriteTraceAddressKey(string address)
    {
        Match match = WriteTraceAddressRegex.Match(address);
        Assert.IsTrue(match.Success, $"address not parseable by write-trace regex: {address}");
        return ("0x" + match.Groups[2].Value).ToLowerInvariant();
    }

    [TestMethod]
    public async Task AppendClockSegment_ValidRequestAppendsAndMapsResponse()
    {
        var clock = new FakeReplayClockSource();
        BattleSessionId sessionId = BattleSessionId.New();
        DateTimeOffset anchor = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var request = new AppendClockSegmentRequest
        {
            BattleSessionId = sessionId.ToString(),
            Sequence = 0,
            SourceAnchorUtc = anchor,
            ReplayAnchorTicks = TimeSpan.FromSeconds(30).Ticks,
            Speed = 1.0,
            Source = "CaptureLog",
            UncertaintyTicks = TimeSpan.FromMilliseconds(500).Ticks,
        };

        IResult result = await GameApiEndpoints.AppendClockSegmentAsync(
            clock, request, TestContext.CancellationToken);

        AppendClockSegmentResponse response = Value<AppendClockSegmentResponse>(result);
        Assert.AreEqual(sessionId.ToString(), response.BattleSessionId);
        Assert.AreEqual(0, response.Sequence);
        Assert.AreEqual(anchor, response.SourceAnchorUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(30).Ticks, response.ReplayAnchorTicks);
        Assert.AreEqual(1.0, response.Speed);
        Assert.AreEqual("CaptureLog", response.Source);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500).Ticks, response.UncertaintyTicks);
        Assert.IsNotNull(clock.Appended);
        Assert.AreEqual(sessionId, clock.Appended!.BattleSessionId);
        Assert.AreEqual(TimeSpan.FromSeconds(30), clock.Appended!.ReplayAnchor);
        Assert.AreEqual(TelemetrySourceKind.CaptureLog, clock.Appended!.Source);
        Assert.IsTrue(clock.Appended!.CreatedAtUtc != default);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-guid")]
    public async Task AppendClockSegment_InvalidSessionIdRejected(string? sessionId)
    {
        var clock = new FakeReplayClockSource();

        IResult result = await GameApiEndpoints.AppendClockSegmentAsync(
            clock,
            new AppendClockSegmentRequest { BattleSessionId = sessionId },
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsNull(clock.Appended);
    }

    [TestMethod]
    public async Task AppendClockSegment_InvalidValuesRejected()
    {
        var clock = new FakeReplayClockSource();
        var request = new AppendClockSegmentRequest
        {
            BattleSessionId = BattleSessionId.New().ToString(),
            ReplayAnchorTicks = -1,
        };

        IResult result = await GameApiEndpoints.AppendClockSegmentAsync(
            clock, request, TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsNull(clock.Appended);
    }

    [TestMethod]
    public async Task AppendClockSegment_ClockSourceFailureMapsToBadRequest()
    {
        var clock = new FakeReplayClockSource(OperationResult.Failure<ReplayClockSegment>(
            new ApplicationError("clock.speed.invalid", "Replay-clock speed must be finite and greater than zero.")));
        var request = new AppendClockSegmentRequest
        {
            BattleSessionId = BattleSessionId.New().ToString(),
            Sequence = 0,
            SourceAnchorUtc = DateTimeOffset.UtcNow,
            ReplayAnchorTicks = 0,
            Speed = 1.0,
            Source = "CaptureLog",
            UncertaintyTicks = 0,
        };

        IResult result = await GameApiEndpoints.AppendClockSegmentAsync(
            clock, request, TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.IsNotNull(clock.Appended);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-guid")]
    public async Task CapturePenetrationAsync_InvalidDecodeRunId_ReturnsBadRequest(string decodeRunId)
    {
        var capture = new FakePenetrationCapture();

        IResult result = await GameApiEndpoints.CapturePenetrationAsync(
            capture,
            new WotBTreader.ApiContracts.PenetrationCaptureRequest { DecodeRunId = decodeRunId },
            TestContext.CancellationToken);

        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual(
            "pen_capture.invalid_decode_run_id",
            body.GetProperty("error").GetString());
        Assert.IsNull(capture.LastRequest);
    }

    [TestMethod]
    public async Task CapturePenetrationAsync_ValidRequest_MapsEvaluation()
    {
        Guid decodeRunId = Guid.NewGuid();
        var evaluation = new PenetrationCaptureEvaluation(
            PenetrationCaptureStatus.Rejected,
            PenetrationCaptureReason.OwnerNotUnique,
            [
                PenetrationCaptureReason.OwnerNotUnique,
                PenetrationCaptureReason.OwnerUnstable,
            ],
            ExactWeaponOwnerProven: false,
            ExactLoadedShellProven: false,
            ExactGunRayProven: false,
            new PenetrationCaptureSummary(
                OwnerCandidateCount: 14,
                ShellStatesObserved: 0,
                ShellIdentityMatches: 0,
                AimSamples: 0,
                RaySamples: 0,
                JoinedRaySamples: 0));
        var capture = new FakePenetrationCapture(
            OperationResult.Success(evaluation));

        IResult result = await GameApiEndpoints.CapturePenetrationAsync(
            capture,
            new WotBTreader.ApiContracts.PenetrationCaptureRequest
            {
                DecodeRunId = decodeRunId.ToString(),
            },
            TestContext.CancellationToken);

        WotBTreader.ApiContracts.PenetrationCaptureResponse response =
            Value<WotBTreader.ApiContracts.PenetrationCaptureResponse>(result);
        Assert.AreEqual("Rejected", response.Status);
        Assert.AreEqual("OwnerNotUnique", response.PrimaryReason);
        Assert.HasCount(2, response.Reasons);
        Assert.IsFalse(response.ExactWeaponOwnerProven);
        Assert.IsFalse(response.ExactLoadedShellProven);
        Assert.IsFalse(response.ExactGunRayProven);
        Assert.AreEqual(14, response.OwnerCandidateCount);
        Assert.IsNotNull(capture.LastRequest);
        Assert.AreEqual(decodeRunId, capture.LastRequest!.DecodeRunId.Value);
        Assert.AreEqual(TestContext.CancellationToken, capture.LastCancellationToken);
    }

    [TestMethod]
    public async Task CapturePenetrationAsync_BuildMismatch_PointsAtRecoveryModule()
    {
        var capture = new FakePenetrationCapture(
            OperationResult.Failure<PenetrationCaptureEvaluation>(
                new ApplicationError(
                    "capture.decode_build_mismatch",
                    "The decoded session build does not match the authorized process build.",
                    Retryable: false)));

        IResult result = await GameApiEndpoints.CapturePenetrationAsync(
            capture,
            new WotBTreader.ApiContracts.PenetrationCaptureRequest
            {
                DecodeRunId = Guid.NewGuid().ToString(),
            },
            TestContext.CancellationToken);

        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual("capture.decode_build_mismatch", body.GetProperty("error").GetString());
        Assert.IsTrue(
            body.GetProperty("reason").GetString()!.Contains("RECOVERY/README.md", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CapturePenetrationAsync_SourceFailure_ReturnsError()
    {
        var capture = new FakePenetrationCapture(
            OperationResult.Failure<PenetrationCaptureEvaluation>(
                new ApplicationError("capture.gate_not_satisfied", "gate", Retryable: false)));

        IResult result = await GameApiEndpoints.CapturePenetrationAsync(
            capture,
            new WotBTreader.ApiContracts.PenetrationCaptureRequest
            {
                DecodeRunId = Guid.NewGuid().ToString(),
            },
            TestContext.CancellationToken);

        JsonElement body = BadRequestAnonymous(result);
        Assert.AreEqual("capture.gate_not_satisfied", body.GetProperty("error").GetString());
    }

    private sealed class FakePenetrationCapture(
        OperationResult<PenetrationCaptureEvaluation>? result = null)
        : IPenetrationCapture
    {
        public WotBTreader.Application.Game.PenetrationCaptureRequest? LastRequest { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<OperationResult<PenetrationCaptureEvaluation>> CaptureAsync(
            WotBTreader.Application.Game.PenetrationCaptureRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(result ?? OperationResult.Success(
                new PenetrationCaptureEvaluation(
                    PenetrationCaptureStatus.Rejected,
                    PenetrationCaptureReason.OwnerNotUnique,
                    [PenetrationCaptureReason.OwnerNotUnique],
                    ExactWeaponOwnerProven: false,
                    ExactLoadedShellProven: false,
                    ExactGunRayProven: false,
                    new PenetrationCaptureSummary(0, 0, 0, 0, 0, 0))));
        }
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

    private static JsonElement NotFoundAnonymous(IResult result)
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
        public string? DiscardedSession { get; private set; }
        public MemoryScanRequest? LastScanRequest { get; private set; }
        public MemorySnapshotRequest? LastSnapshotRequest { get; private set; }
        public string? LastCompareSessionId { get; private set; }
        public string? LastCompareMode { get; private set; }
        public double? LastCompareDeltaTarget { get; private set; }
        public double? LastCompareDeltaTolerance { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public MemoryReadRequest? LastReadRequest { get; private set; }
        public WotBTreader.Application.Game.EntityPositionReadRequest? LastEntityPositionRequest { get; private set; }
        public WotBTreader.Application.Game.EntityPositionAddressRequest? LastEntityPositionAddressRequest { get; private set; }
        public WotBTreader.Application.Game.InstructionSnapshotRequest? LastInstructionSnapshotRequest { get; private set; }
        public OperationResult<MemoryReadResult> ReadResult { get; init; } = OperationResult.Success(
            new MemoryReadResult(DateTimeOffset.UnixEpoch,
            [
                new MemoryReadItem(0x7FFA1234, true, [0x00, 0x00, 0x80, 0x3F], "1"),
            ]));
        public OperationResult<WotBTreader.Application.Game.InstructionSnapshotResult> InstructionSnapshotResult { get; init; } =
            OperationResult.Failure<WotBTreader.Application.Game.InstructionSnapshotResult>(
                new ApplicationError("discover.instruction_snapshot.not_configured", "Test default."));
        public OperationResult<CameraPoseReadResult> CameraPoseResult { get; init; } =
            OperationResult.Failure<CameraPoseReadResult>(
                new ApplicationError("discover.camera_pose.not_configured", "Test default."));
        public int CreateCameraPoseCallCount { get; private set; }
        public OperationResult<EntityPositionReadResult> EntityPositionResult { get; init; } =
            OperationResult.Failure<EntityPositionReadResult>(
                new ApplicationError("discover.entity_position.not_configured", "Test default."));
        public OperationResult<EntityRecordRegionReadResult> EntityRegionResult { get; init; } =
            OperationResult.Failure<EntityRecordRegionReadResult>(
                new ApplicationError("discover.entity_region.not_configured", "Test default."));
        public OperationResult<EntityRegionsReadResult> EntityRegionsResult { get; init; } =
            OperationResult.Failure<EntityRegionsReadResult>(
                new ApplicationError("discover.entity_regions.not_configured", "Test default."));
        public OperationResult<WotBTreader.Application.Game.EntityPositionAddressResult> EntityPositionAddressResult { get; init; } =
            OperationResult.Failure<WotBTreader.Application.Game.EntityPositionAddressResult>(
                new ApplicationError("discover.entity_position.address_not_configured", "Test default."));
        public WotBTreader.Application.Game.EntityRecordRegionReadRequest? LastEntityRegionRequest { get; private set; }
        public WotBTreader.Application.Game.EntityRegionsReadRequest? LastEntityRegionsRequest { get; private set; }
        public OperationResult<MemoryCompareResult> CompareResult { get; init; } = OperationResult.Success(
            new MemoryCompareResult(DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0, [], false, false, 0));
        public OperationResult<MemoryPointerChainResult> PointerChainResult { get; init; } = OperationResult.Success(
            new MemoryPointerChainResult(DateTimeOffset.UnixEpoch, [], 0));
        public OperationResult<MemoryScanResult> PatternResult { get; init; } = OperationResult.Success(
            new MemoryScanResult(DateTimeOffset.UnixEpoch, 0x140000000, 0, 0, [], 0));

        public ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
            MemoryScanRequest request, CancellationToken cancellationToken)
        {
            LastScanRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(PatternResult);
        }

        public ValueTask<OperationResult<MemoryScanResult>> ScanPatternAsync(
            MemoryScanRequest request, CancellationToken cancellationToken)
        {
            PatternCalled = true;
            LastScanRequest = request;
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
            MemorySnapshotRequest request, CancellationToken cancellationToken)
        {
            LastSnapshotRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(OperationResult.Success("test"));
        }

        public ValueTask<OperationResult<MemoryCompareResult>> CompareAsync(
            string sessionId, string compareMode, int maxCandidates,
            CancellationToken cancellationToken, bool advanceBaseline = false,
            double? deltaTarget = null, double? deltaTolerance = null)
        {
            LastCompareSessionId = sessionId;
            LastCompareMode = compareMode;
            LastCompareDeltaTarget = deltaTarget;
            LastCompareDeltaTolerance = deltaTolerance;
            return ValueTask.FromResult(CompareResult);
        }

        public void DiscardSession(string sessionId) => DiscardedSession = sessionId;

        public ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
            MemoryNeighborhoodRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PatternResult);

        public ValueTask<OperationResult<MemoryReadResult>> ReadAddressesAsync(
            MemoryReadRequest request, CancellationToken cancellationToken)
        {
            LastReadRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(ReadResult);
        }

        public ValueTask<OperationResult<EntityPositionReadResult>> ReadEntityPositionAsync(
            WotBTreader.Application.Game.EntityPositionReadRequest request,
            CancellationToken cancellationToken)
        {
            LastEntityPositionRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(EntityPositionResult);
        }

        public ValueTask<OperationResult<EntityRecordRegionReadResult>> ReadEntityRegionAsync(
            WotBTreader.Application.Game.EntityRecordRegionReadRequest request,
            CancellationToken cancellationToken)
        {
            LastEntityRegionRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(EntityRegionResult);
        }

        public OperationResult<EntityRosterReadResult> EntityRosterResult { get; init; } =
            OperationResult.Failure<EntityRosterReadResult>(
                new ApplicationError("discover.entity_roster.not_configured", "Test default."));
        public int EntityRosterCallCount { get; private set; }

        public ValueTask<OperationResult<EntityRosterReadResult>> EnumerateEntitiesAsync(
            CancellationToken cancellationToken)
        {
            EntityRosterCallCount++;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(EntityRosterResult);
        }

        public OperationResult<LiveFrameReadResult> LiveFrameResult { get; init; } =
            OperationResult.Failure<LiveFrameReadResult>(
                new ApplicationError("discover.live_frame.not_configured", "Test default."));
        public int LiveFrameCallCount { get; private set; }
        public WotBTreader.Application.Game.LiveFrameReadRequest? LastLiveFrameRequest { get; private set; }

        public ValueTask<OperationResult<LiveFrameReadResult>> ReadLiveFrameAsync(
            WotBTreader.Application.Game.LiveFrameReadRequest request,
            CancellationToken cancellationToken)
        {
            LiveFrameCallCount++;
            LastLiveFrameRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(LiveFrameResult);
        }

        public ValueTask<OperationResult<EntityRegionsReadResult>> ReadEntityRegionsAsync(
            WotBTreader.Application.Game.EntityRegionsReadRequest request,
            CancellationToken cancellationToken)
        {
            LastEntityRegionsRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(EntityRegionsResult);
        }

        public ValueTask<OperationResult<WotBTreader.Application.Game.EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
            WotBTreader.Application.Game.EntityPositionAddressRequest request,
            CancellationToken cancellationToken)
        {
            LastEntityPositionAddressRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(EntityPositionAddressResult);
        }

        public ValueTask<OperationResult<WotBTreader.Application.Game.InstructionSnapshotResult>> CaptureInstructionSnapshotAsync(
            WotBTreader.Application.Game.InstructionSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            LastInstructionSnapshotRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(InstructionSnapshotResult);
        }

        public ValueTask<OperationResult<CameraPoseReadResult>> ReadCameraPoseAsync(
            CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            CreateCameraPoseCallCount++;
            return ValueTask.FromResult(CameraPoseResult);
        }
    }

    private sealed class FakeTrajectoryProvider(
        OperationResult<TrajectoryGroundTruth> result) : ITrajectoryGroundTruthProvider
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<OperationResult<TrajectoryGroundTruth>> GetAsync(
            BattleSessionId sessionId,
            CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(result);
        }
    }

    private static ReplayDecodeProjection LaunchProjection(
        SourceArtifactId sourceArtifactId,
        BattleSessionId battleSessionId)
    {
        DecodeRun run = new(
            DecodeRunId.New(),
            sourceArtifactId,
            "test",
            "1",
            "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Metadata,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            FailureCode: null,
            FailureSummary: null);
        BattleSession session = new(
            battleSessionId,
            run.Id,
            "11.19.0.10",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: null,
            ViewpointParticipantId: null,
            SchemaVersion: "1");
        return new ReplayDecodeProjection(
            run,
            session,
            Participants: [],
            Positions: [],
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private static FakeSessionQueryRepository MissingSessionRepository() => new(
        OperationResult.Failure<ReplayDecodeProjection>(
            new ApplicationError("session.not_found", "Session not found.")));

    private sealed class FakeSessionQueryRepository(OperationResult<ReplayDecodeProjection> result)
        : ISessionQueryRepository
    {
        public BattleSessionId? LastSessionId { get; private set; }

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DecodeRunSummary>>([]);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken)
        {
            LastSessionId = battleSessionId;
            return ValueTask.FromResult(result);
        }

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }

    private sealed class FakePenOfflineScorer(OfflinePenScoreReport report) : IPenOfflineScorer
    {
        public ReplayDecodeProjection? LastProjection { get; private set; }

        public IReadOnlyList<AimSample>? LastAimOverrides { get; private set; }

        public ValueTask<OfflinePenScoreReport> ScoreAsync(
            ReplayDecodeProjection projection,
            IReadOnlyList<AimSample>? aimOverrides,
            CancellationToken cancellationToken)
        {
            LastProjection = projection;
            LastAimOverrides = aimOverrides;
            return ValueTask.FromResult(report);
        }
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

    private sealed class FakeReplayClockSource(
        OperationResult<ReplayClockSegment>? appendResult = null) : IReplayClockSource
    {
        public ReplayClockSegment? Appended { get; private set; }

        public ValueTask<OperationResult<ReplayClockSnapshot>> GetSnapshotAsync(
            BattleSessionId battleSessionId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<ReplayClockSegment>> AddSegmentAsync(
            ReplayClockSegment segment,
            CancellationToken cancellationToken)
        {
            Appended = segment;
            return ValueTask.FromResult(appendResult ?? OperationResult.Success(segment));
        }

        public ValueTask<OperationResult<ReplayClockSnapshot>> MarkStaleAsync(
            BattleSessionId battleSessionId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
