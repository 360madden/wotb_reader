using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Endpoints;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// Covers the read API's paging bounds, error mapping, payload capping, and the
/// fields it deliberately does or does not expose.
/// </summary>
[TestClass]
public sealed class ReadApiEndpointsTests
{
    [TestMethod]
    public async Task OverlayFrame_ProjectsVisibleTanksAndDropsBehindCamera()
    {
        // Camera at the origin facing +Z (yaw 0); one tank 100m ahead, one
        // behind the camera.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, 0, 0, 100, 0.1, 1.0, true, 1, "Alpha", null, "TankA", "Heavy", 100),
                new OverlayTankState(2, 0, 0, -100, 0.1, 0.5, false, 2, "Behind", null, "TankB", "Heavy", 100),
            },
            [],
            []));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.AreEqual(10.0, frame.ReplayTimeSeconds, 1e-9);
        Assert.AreEqual(0.0, frame.CameraYawRadians!.Value, 1e-9);
        Assert.HasCount(2, frame.Tanks);
        OverlayTankResponse front = frame.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(960.0, front.ScreenX!.Value, 1e-6);
        Assert.AreEqual(540.0, front.ScreenY!.Value, 1e-6);
        Assert.IsTrue(front.InViewport);
        Assert.AreEqual("Alpha", front.PlayerName);
        // World position rides through for the minimap (god-view), even for
        // the behind-camera tank whose screen projection is null.
        Assert.AreEqual(0.0, front.WorldX, 1e-9);
        Assert.AreEqual(100.0, front.WorldZ, 1e-9);
        OverlayTankResponse behind = frame.Tanks.Single(tank => tank.EntityId == 2);
        Assert.IsNull(behind.ScreenX);
        Assert.IsFalse(behind.InViewport);
        Assert.AreEqual(0.0, behind.WorldX, 1e-9);
        Assert.AreEqual(-100.0, behind.WorldZ, 1e-9);
        // Scoreboard totals ride through (0 is the default in this fixture).
        Assert.AreEqual(0, front.DamageDealt);
        Assert.AreEqual(0, front.DamageTaken);
        Assert.AreEqual(0, front.Kills);
    }

    [TestMethod]
    public async Task OverlayFrame_ExactHealthRidesThrough()
    {
        // The decoded ledger's exact health (max from the type-5 spawn
        // broadcast, current = max − damage received) must reach the API
        // contract for the HUD nameplate readout.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(
                    1, 0, 0, 100, 0.1, HpFraction: 0.6, true, 1, "Alpha", null,
                    "TankA", "Heavy", 100,
                    DamageDealt: 1200, DamageTaken: 280, Kills: 2,
                    MaxHealth: 700, CurrentHealth: 420),
                new OverlayTankState(
                    2, 0, 0, -100, 0.1, HpFraction: 1.0, true, 2, "NoMax", null,
                    "TankB", "Heavy", 100),
            },
            [],
            []));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        OverlayTankResponse alpha = frame.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(700, alpha.MaxHealth);
        Assert.AreEqual(420, alpha.CurrentHealth);
        Assert.AreEqual(0.6, alpha.HpFraction, 1e-9);
        // Unknown max health fails closed to 0 (never guessed).
        OverlayTankResponse noMax = frame.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(0, noMax.MaxHealth);
        Assert.AreEqual(0, noMax.CurrentHealth);
    }

    [TestMethod]
    public async Task OverlayFrame_TankHeadingIsProjectedWhenRotationKnown()
    {
        // Camera at the origin facing +Z; a tank 100m ahead facing +X (yaw
        // pi/2): its nose points screen-right, so the heading is +90 degrees
        // (clockwise from screen-up). A tank with no yaw evidence carries
        // null.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, 0, 0, 100, Math.PI / 2, 1.0, true, 1, "Alpha", null, "TankA", "Heavy", 100),
                new OverlayTankState(2, 0, 0, 100, null, 1.0, true, 2, "NoYaw", null, "TankB", "Heavy", 100),
            },
            [],
            []));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        OverlayTankResponse alpha = frame.Tanks.Single(tank => tank.EntityId == 1);
        Assert.IsNotNull(alpha.ScreenHeadingDegrees);
        Assert.AreEqual(90.0, alpha.ScreenHeadingDegrees!.Value, 1e-6);
        OverlayTankResponse noYaw = frame.Tanks.Single(tank => tank.EntityId == 2);
        Assert.IsNull(noYaw.ScreenHeadingDegrees);
    }

    [TestMethod]
    public async Task OverlayFrame_ReturnsKillFeedWithAttribution()
    {
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(20),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            [],
            new[]
            {
                new OverlayKill(50, 70, TimeSpan.FromSeconds(20)),
                new OverlayKill(51, null, TimeSpan.FromSeconds(30)),
            }));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 20,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.HasCount(2, frame.Kills);
        Assert.AreEqual(50, frame.Kills[0].VictimEntityId);
        Assert.AreEqual(70, frame.Kills[0].KillerEntityId);
        Assert.AreEqual(20.0, frame.Kills[0].ReplayTimeSeconds, 1e-9);
        Assert.IsNull(frame.Kills[1].KillerEntityId);
    }

    [TestMethod]
    public async Task OverlayFrame_ReturnsEventPipsForVisibleTanks()
    {
        // Damage + destroyed pips for an in-viewport tank come through with
        // the tank's pixel; a pip for a behind-camera tank is dropped.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            new[]
            {
                new OverlayTankState(1, 0, 0, 100, 0.1, 0.6, true, 1, "Alpha", null, "TankA", "Heavy", 100),
                new OverlayTankState(2, 0, 0, -100, 0.1, 0.0, false, 2, "Behind", null, "TankB", "Heavy", 100),
            },
            new[]
            {
                new OverlayEventPip(1, CanonicalEventKind.Damage, 60, TimeSpan.FromSeconds(9.5)),
                new OverlayEventPip(2, CanonicalEventKind.Damage, 90, TimeSpan.FromSeconds(9.5)),
            },
            []));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.HasCount(1, frame.Pips);
        OverlayPipResponse pip = frame.Pips.Single();
        Assert.AreEqual(1, pip.EntityId);
        Assert.AreEqual("Damage", pip.Kind);
        Assert.AreEqual(60, pip.Damage);
        Assert.AreEqual(960.0, pip.ScreenX, 1e-6);
        Assert.AreEqual(540.0, pip.ScreenY, 1e-6);
    }

    [TestMethod]
    public async Task OverlayFrame_RejectsInvalidQueryParameters()
    {
        FakeOverlayFrames frames = new();

        IResult badTime = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(), frames, new FakeBeaconStore(), Guid.NewGuid(),
            timeSeconds: -1, fov: 90, width: 1920, height: 1080,
            TestContext.CancellationToken);
        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(badTime));

        IResult badFov = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(), frames, new FakeBeaconStore(), Guid.NewGuid(),
            timeSeconds: 0, fov: 200, width: 1920, height: 1080,
            TestContext.CancellationToken);
        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(badFov));
    }

    [TestMethod]
    public async Task OverlayFrame_SessionFailureBecomesNotFound()
    {
        FakeOverlayFrames frames = new(
            error: new ApplicationError("storage.session.not_found", "No such session."));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(), frames, new FakeBeaconStore(), Guid.NewGuid(),
            timeSeconds: 0, fov: 90, width: 1920, height: 1080,
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [TestMethod]
    public async Task OverlayFrame_ResolvedMemoryCameraReplacesViewpoint()
    {
        // CAM-005 seam: a gate-verified GameCamera pose read is mapped to the
        // overlay camera and threaded into the frame source.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            [],
            []));
        CameraScannerStub scanner = new(OperationResult.Success(
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
                ModuleRooted: true)));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken,
            scanner);

        OverlayCamera? overrideCamera = frames.LastCameraOverride;
        Assert.IsNotNull(overrideCamera);
        Assert.AreEqual(10.5, overrideCamera!.X);
        Assert.AreEqual(20.25, overrideCamera.Y);
        Assert.AreEqual(-3.5, overrideCamera.Z);
        Assert.AreEqual(0.7, overrideCamera.YawRadians!.Value, 1e-6);
        Assert.AreEqual(-0.2, overrideCamera.PitchRadians!.Value, 1e-6);
        Assert.IsNull(overrideCamera.RollRadians);
        Assert.AreEqual(1, scanner.CameraPoseCallCount);

        // The projected response rides the memory camera.
        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.AreEqual(10.5, frame.CameraX!.Value, 1e-6);
        Assert.AreEqual(0.7, frame.CameraYawRadians!.Value, 1e-6);
    }

    [TestMethod]
    public async Task OverlayFrame_MemoryCameraFailureFallsBackToViewpoint()
    {
        // Fail-closed: an unresolved/failed camera read yields no override and
        // the frame uses the decoded viewpoint camera.
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(10),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            [],
            []));
        CameraScannerStub scanner = new(OperationResult.Failure<CameraPoseReadResult>(
            new ApplicationError("discover.gate_not_satisfied", "No gate.")));

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            new FakeBeaconStore(),
            Guid.NewGuid(),
            timeSeconds: 10,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken,
            scanner);

        Assert.IsNull(frames.LastCameraOverride);
        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.AreEqual(0.0, frame.CameraX!.Value, 1e-9);
        Assert.AreEqual(1, scanner.CameraPoseCallCount);
    }

    [TestMethod]
    public async Task SessionsPageIsReturnedWithItsRequestedWindow()
    {
        FakeSessionQueries sessions = new([Summary()]);

        IResult result = await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset: 5,
            limit: 25,
            TestContext.CancellationToken);

        SessionPageResponse page = Value<SessionPageResponse>(result);
        Assert.AreEqual(5, page.Offset);
        Assert.AreEqual(25, page.Limit);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual((5, 25), sessions.LastRequest);
    }

    [TestMethod]
    public async Task SessionsAppliesTheDefaultWindowWhenNoneIsSupplied()
    {
        FakeSessionQueries sessions = new([]);

        await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset: null,
            limit: null,
            TestContext.CancellationToken);

        Assert.AreEqual((0, ReadApiEndpoints.DefaultPageSize), sessions.LastRequest);
    }

    [TestMethod]
    [DataRow(-1, 10)]
    [DataRow(0, 0)]
    [DataRow(0, -5)]
    [DataRow(0, ReadApiEndpoints.MaximumPageSize + 1)]
    public async Task OutOfRangePagingIsRejectedWithoutQueryingStorage(int offset, int limit)
    {
        FakeSessionQueries sessions = new([]);

        IResult result = await ReadApiEndpoints.ListSessionsAsync(
            new DefaultHttpContext(),
            sessions,
            offset,
            limit,
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.IsNull(sessions.LastRequest, "An invalid window must never reach storage.");
    }

    [TestMethod]
    public async Task PositionSeriesIsCappedAndReportsTruncation()
    {
        int total = ReadApiEndpoints.MaximumPositionSamples + 25;
        FakeSessionQueries sessions = new([], Projection(positionCount: total));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        SessionDetailResponse detail = Value<SessionDetailResponse>(result);
        Assert.HasCount(ReadApiEndpoints.MaximumPositionSamples, detail.Positions);
        Assert.IsTrue(detail.PositionsTruncated);
        Assert.AreEqual(total, detail.TotalPositionCount);
    }

    [TestMethod]
    public async Task ShortPositionSeriesIsNotMarkedTruncated()
    {
        FakeSessionQueries sessions = new([], Projection(positionCount: 3));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        SessionDetailResponse detail = Value<SessionDetailResponse>(result);
        Assert.HasCount(3, detail.Positions);
        Assert.IsFalse(detail.PositionsTruncated);
    }

    [TestMethod]
    public async Task MissingSessionBecomesANotFoundProblem()
    {
        FakeSessionQueries sessions = new(
            [],
            error: new ApplicationError("storage.session.not_found", "No such session."));

        IResult result = await ReadApiEndpoints.GetSessionAsync(
            new DefaultHttpContext(),
            sessions,
            Guid.NewGuid(),
            TestContext.CancellationToken);

        Assert.AreEqual(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [TestMethod]
    [DataRow("storage.session.not_found", StatusCodes.Status404NotFound)]
    [DataRow("storage.conflict", StatusCodes.Status409Conflict)]
    [DataRow("storage.busy", StatusCodes.Status409Conflict)]
    [DataRow("request.invalid", StatusCodes.Status400BadRequest)]
    [DataRow("replay.malformed", StatusCodes.Status400BadRequest)]
    [DataRow("decoder.unsupported", StatusCodes.Status501NotImplemented)]
    [DataRow("internal.unknown", StatusCodes.Status500InternalServerError)]
    public void ErrorCodesMapToStableStatusCodes(string code, int expected) =>
        Assert.AreEqual(expected, ReadApiEndpoints.MapStatusCode(code));

    [TestMethod]
    public void ParticipantResponseNeverCarriesTheAccountIdentifier()
    {
        Participant participant = ParticipantFixture() with { AccountId = 987654321 };

        ParticipantResponse response = participant.ToResponse();

        Assert.AreEqual("pilot", response.PlayerName);
        Assert.IsFalse(
            response.ToString().Contains("987654321", StringComparison.Ordinal),
            "The durable account identifier must not reach an API client.");
    }

    [TestMethod]
    public void BotStatusIsPassedThroughWithItsConfidence()
    {
        Participant participant = ParticipantFixture() with
        {
            BotStatus = BotStatus.Unknown,
            BotStatusConfidence = EvidenceConfidence.Unknown,
        };

        ParticipantResponse response = participant.ToResponse();

        Assert.AreEqual("Unknown", response.BotStatus);
        Assert.AreEqual("Unknown", response.BotStatusConfidence);
    }

    [TestMethod]
    public void BattleStatsAreMappedFieldByField()
    {
        Participant participant = ParticipantFixture() with
        {
            BattleStats = new BattleStats(
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
        };

        ParticipantResponse response = participant.ToResponse();

        Assert.IsNotNull(response.BattleStats);
        Assert.AreEqual(1200, response.BattleStats.CreditsEarned);
        Assert.AreEqual(850, response.BattleStats.BaseXp);
        Assert.AreEqual(2340, response.BattleStats.DamageDealt);
        Assert.AreEqual(300, response.BattleStats.DamageAssisted1);
        Assert.AreEqual(120, response.BattleStats.DamageAssisted2);
        Assert.AreEqual(2575.5f, response.BattleStats.MmRating);
        Assert.AreEqual(410, response.BattleStats.DamageBlocked);
        Assert.AreEqual(1, response.BattleStats.EnemiesDestroyed);
        Assert.AreEqual(20, response.BattleStats.VictoryPointsSeized);
    }

    [TestMethod]
    public void MissingBattleStatsMapToNull()
    {
        Participant participant = ParticipantFixture() with { BattleStats = null };

        ParticipantResponse response = participant.ToResponse();

        Assert.IsNull(response.BattleStats);
    }

    [TestMethod]
    public void CapabilityFlagsAreExpandedIntoNames()
    {
        DecodeRun run = DecodeRunFixture() with
        {
            Capabilities = ReplayCapability.Metadata | ReplayCapability.Positions,
        };

        DecodeRunResponse response = run.ToResponse();

        Assert.HasCount(2, response.Capabilities);
        Assert.Contains("Metadata", response.Capabilities);
        Assert.Contains("Positions", response.Capabilities);
    }

    [TestMethod]
    public void IdentifiersAreRenderedAsPlainStrings()
    {
        DecodeRunResponse response = DecodeRunFixture().ToResponse();

        Assert.IsTrue(
            Guid.TryParse(response.DecodeRunId, out _),
            "Clients must receive an identifier they can use without unwrapping an object.");
    }

    public TestContext TestContext { get; set; } = null!;

    private static EvidenceReference EvidenceFixture() =>
        new(
            SourceArtifactId.New(),
            "data.wotreplay",
            0,
            1,
            new ContentHash(new string('a', 64)));

    private static T Value<T>(IResult result)
    {
        Assert.IsInstanceOfType<Ok<T>>(result);
        T? value = ((Ok<T>)result).Value;
        Assert.IsNotNull(value);
        return value;
    }

    private static int StatusOf(IResult result)
    {
        Assert.IsInstanceOfType<ProblemHttpResult>(result);
        return ((ProblemHttpResult)result).StatusCode;
    }

    private static DecodeRun DecodeRunFixture() =>
        new(
            DecodeRunId.New(),
            SourceArtifactId.New(),
            "wotb-11.x-strict",
            "0.1.0",
            "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Metadata,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            null,
            null);

    private static Participant ParticipantFixture() =>
        new(
            ParticipantId.New(),
            BattleSessionId.New(),
            AccountId: null,
            EntityId: 100,
            TeamNumber: 1,
            PlayerName: "pilot",
            ClanTag: "TAG",
            VehicleCompactDescriptor: 2897,
            TankId: null,
            TankName: null,
            TankClass.Unknown,
            BotStatus.Unknown,
            EvidenceConfidence.Unknown,
            BattleStats: null,
            EvidenceFixture());

    private static DecodeRunSummary Summary() =>
        new(DecodeRunFixture(), Session: null, 2, 4, 6, 8);

    private static ReplayDecodeProjection Projection(int positionCount)
    {
        BattleSessionId sessionId = BattleSessionId.New();
        PositionSample[] positions = [.. Enumerable.Range(0, positionCount).Select(index =>
            new PositionSample(
                PositionSampleId.New(),
                sessionId,
                ParticipantId: null,
                EntityId: 100,
                index,
                TimeSpan.FromMilliseconds(index),
                index,
                0,
                0,
                null,
                null,
                CoordinateSpace.ReplayRaw,
                null,
                EvidenceFixture()))];

        return new ReplayDecodeProjection(
            DecodeRunFixture(),
            Session: null,
            Participants: [],
            positions,
            Events: [],
            RawRecords: [],
            Warnings: []);
    }

    private sealed class FakeSessionQueries(
        IReadOnlyList<DecodeRunSummary> page,
        ReplayDecodeProjection? projection = null,
        ApplicationError? error = null) : ISessionQueryRepository
    {
        public (int Offset, int Limit)? LastRequest { get; private set; }

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            LastRequest = (offset, limit);
            return ValueTask.FromResult(page);
        }

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(projection is null
                ? OperationResult.Failure<ReplayDecodeProjection>(
                    error ?? new ApplicationError("storage.session.not_found", "No such session."))
                : OperationResult.Success(projection));
    }

    [TestMethod]
    public async Task OverlayFrame_ProjectsVisibleBeaconsAndFiltersByTimeWindow()
    {
        FakeOverlayFrames frames = new(new OverlayFrame(
            TimeSpan.FromSeconds(50),
            new OverlayCamera(0, 0, 0, YawRadians: 0, PitchRadians: 0, RollRadians: 0),
            [],
            [],
            []));
        FakeBeaconStore beacons = new(new[]
        {
            new OverlayBeacon("Flag", 0, 0, 100, "#FFD700", null, null),
            new OverlayBeacon("Gone", 0, 0, 100, "#FF0000", null, TimeSpan.FromSeconds(10)),
        });

        IResult result = await ReadApiEndpoints.GetOverlayFrameAsync(
            new DefaultHttpContext(),
            frames,
            beacons,
            Guid.NewGuid(),
            timeSeconds: 50,
            fov: 90,
            width: 1920,
            height: 1080,
            TestContext.CancellationToken);

        OverlayFrameResponse frame = Value<OverlayFrameResponse>(result);
        Assert.HasCount(1, frame.Beacons);
        OverlayBeaconResponse flag = frame.Beacons.Single(beacon => beacon.Name == "Flag");
        Assert.AreEqual("#FFD700", flag.Color);
        Assert.AreEqual(960.0, flag.ScreenX!.Value, 1e-6);
        Assert.AreEqual(540.0, flag.ScreenY!.Value, 1e-6);
        Assert.IsTrue(flag.InViewport);
        // World coords ride through for the minimap.
        Assert.AreEqual(0.0, flag.WorldX, 1e-9);
        Assert.AreEqual(100.0, flag.WorldZ, 1e-9);
    }

    private sealed class FakeBeaconStore(IReadOnlyList<OverlayBeacon>? beacons = null) : IBeaconStore
    {
        public Task<IReadOnlyList<OverlayBeacon>> GetBeaconsAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OverlayBeacon>>(beacons ?? []);

        public Task AddBeaconAsync(
            BattleSessionId battleSessionId,
            OverlayBeacon beacon,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RemoveBeaconAsync(
            BattleSessionId battleSessionId,
            string name,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class CameraScannerStub(
        OperationResult<CameraPoseReadResult> poseResult) : IGameMemoryScanner
    {
        public int CameraPoseCallCount { get; private set; }

        public ValueTask<OperationResult<CameraPoseReadResult>> ReadCameraPoseAsync(
            CancellationToken cancellationToken)
        {
            CameraPoseCallCount++;
            return ValueTask.FromResult(poseResult);
        }

        public ValueTask<OperationResult<MemoryScanResult>> ScanAsync(
            MemoryScanRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<MemoryScanResult>> ScanPatternAsync(
            MemoryScanRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<MemoryPointerChainResult>> ResolvePointerChainAsync(
            MemoryPointerChainRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<string>> CreateSnapshotAsync(
            MemorySnapshotRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<MemoryCompareResult>> CompareAsync(
            string sessionId, string compareMode, int maxCandidates,
            CancellationToken cancellationToken, bool advanceBaseline = false,
            double? deltaTarget = null, double? deltaTolerance = null) =>
            throw new NotSupportedException();

        public void DiscardSession(string sessionId) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<MemoryScanResult>> ScanNeighborhoodAsync(
            MemoryNeighborhoodRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<MemoryReadResult>> ReadAddressesAsync(
            MemoryReadRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityPositionReadResult>> ReadEntityPositionAsync(
            WotBTreader.Application.Game.EntityPositionReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityRecordRegionReadResult>> ReadEntityRegionAsync(
            WotBTreader.Application.Game.EntityRecordRegionReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityRegionsReadResult>> ReadEntityRegionsAsync(
            WotBTreader.Application.Game.EntityRegionsReadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityRosterReadResult>> EnumerateEntitiesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<EntityPositionAddressResult>> ResolveEntityPositionAddressAsync(
            WotBTreader.Application.Game.EntityPositionAddressRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<OperationResult<InstructionSnapshotResult>> CaptureInstructionSnapshotAsync(
            WotBTreader.Application.Game.InstructionSnapshotRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeOverlayFrames(
        OverlayFrame? frame = null,
        ApplicationError? error = null) : IOverlayFrameSource
    {
        public OverlayCamera? LastCameraOverride { get; private set; }

        public ValueTask<OperationResult<OverlayFrame>> GetFrameAsync(
            BattleSessionId battleSessionId,
            TimeSpan replayTime,
            CancellationToken cancellationToken,
            OverlayCamera? cameraOverride = null)
        {
            LastCameraOverride = cameraOverride;
            OverlayFrame? effective = frame is null
                ? null
                : cameraOverride is null
                    ? frame
                    : frame with { Camera = cameraOverride };
            return ValueTask.FromResult(effective is null
                ? OperationResult.Failure<OverlayFrame>(
                    error ?? new ApplicationError("storage.session.not_found", "No such session."))
                : OperationResult.Success(effective));
        }
    }
}
