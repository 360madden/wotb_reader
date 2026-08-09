using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
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
        public WotBTreader.Application.Game.InstructionSnapshotRequest? LastInstructionSnapshotRequest { get; private set; }
        public OperationResult<MemoryReadResult> ReadResult { get; init; } = OperationResult.Success(
            new MemoryReadResult(DateTimeOffset.UnixEpoch,
            [
                new MemoryReadItem(0x7FFA1234, true, [0x00, 0x00, 0x80, 0x3F], "1"),
            ]));
        public OperationResult<WotBTreader.Application.Game.InstructionSnapshotResult> InstructionSnapshotResult { get; init; } =
            OperationResult.Failure<WotBTreader.Application.Game.InstructionSnapshotResult>(
                new ApplicationError("discover.instruction_snapshot.not_configured", "Test default."));
        public OperationResult<EntityPositionReadResult> EntityPositionResult { get; init; } =
            OperationResult.Failure<EntityPositionReadResult>(
                new ApplicationError("discover.entity_position.not_configured", "Test default."));
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

        public ValueTask<OperationResult<WotBTreader.Application.Game.InstructionSnapshotResult>> CaptureInstructionSnapshotAsync(
            WotBTreader.Application.Game.InstructionSnapshotRequest request,
            CancellationToken cancellationToken)
        {
            LastInstructionSnapshotRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(InstructionSnapshotResult);
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
