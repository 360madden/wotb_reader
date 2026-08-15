using System.IO;
using System.Net;
using System.Net.Http;
using WotBTreader.ApiContracts;
using WotBTreader.Overlay.Discovery;
using WotBTreader.Overlay.Services;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid BattleSessionId = new("3fa85f64-5717-4562-b3fc-2c963f66afa6");

    private string _tempDir = null!;
    private string _rendezvousPath = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wotb-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _rendezvousPath = Path.Combine(_tempDir, "rendezvous.json");
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void GameWindowState_DefaultsToWaitingAndIsNotTracked()
    {
        MainViewModel viewModel = CreateViewModel();

        Assert.AreEqual(HudGameWindowState.NotFound, viewModel.GameWindowState);
        Assert.IsFalse(viewModel.IsGameWindowTracked);
        Assert.AreEqual(
            "Game window: waiting for WoT Blitz",
            viewModel.GameWindowStatusLabel);
        Assert.AreEqual(
            System.Windows.Media.Colors.Gold,
            ((System.Windows.Media.SolidColorBrush)viewModel.GameWindowStatusAccent).Color);
    }

    [TestMethod]
    public void GameWindowState_TrackingAndFailureStatesPublishSafePresentation()
    {
        MainViewModel viewModel = CreateViewModel();
        List<string> changed = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };

        viewModel.SetGameWindowState(HudGameWindowState.Tracking);

        Assert.IsTrue(viewModel.IsGameWindowTracked);
        Assert.AreEqual("Game window: aligned", viewModel.GameWindowStatusLabel);
        Assert.AreEqual(
            System.Windows.Media.Colors.LimeGreen,
            ((System.Windows.Media.SolidColorBrush)viewModel.GameWindowStatusAccent).Color);
        CollectionAssert.Contains(changed, nameof(MainViewModel.GameWindowState));
        CollectionAssert.Contains(changed, nameof(MainViewModel.IsGameWindowTracked));
        CollectionAssert.Contains(changed, nameof(MainViewModel.GameWindowStatusLabel));
        CollectionAssert.Contains(changed, nameof(MainViewModel.GameWindowStatusAccent));

        viewModel.SetGameWindowState(HudGameWindowState.BoundsUnavailable);

        Assert.IsFalse(viewModel.IsGameWindowTracked);
        Assert.AreEqual(
            "Game window: bounds unavailable",
            viewModel.GameWindowStatusLabel);
        Assert.AreEqual(
            System.Windows.Media.Colors.OrangeRed,
            ((System.Windows.Media.SolidColorBrush)viewModel.GameWindowStatusAccent).Color);

        viewModel.SetGameWindowState(HudGameWindowState.Ambiguous);

        Assert.IsFalse(viewModel.IsGameWindowTracked);
        Assert.AreEqual("Game window: multiple matches", viewModel.GameWindowStatusLabel);
    }

    [TestMethod]
    public async Task RenderHealth_ReportsFrameAgeLatencyAndRenderedCollections()
    {
        const string frameJson = """
            {
              "replayTimeSeconds": 3.0,
              "tanks": [],
              "beacons": [],
              "pips": []
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        FakeTimeProvider clock = new(Now);
        MainViewModel viewModel = CreateViewModel(handler, timeProvider: clock);

        Assert.AreEqual("Frame age: — · refresh: —", viewModel.FrameHealthLabel);
        Assert.AreEqual("Render: waiting for frame", viewModel.RenderHealthLabel);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId,
            "Test Map",
            null,
            Now,
            0,
            0);
        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        StringAssert.StartsWith(viewModel.FrameHealthLabel, "Frame age: 0.0s · refresh: ");
        Assert.AreEqual(
            "Render: 0 nameplates · 0 minimap dots · 0 beacons",
            viewModel.RenderHealthLabel);

        clock.Advance(TimeSpan.FromSeconds(3.2));
        viewModel.RefreshRenderHealth();

        StringAssert.StartsWith(viewModel.FrameHealthLabel, "Frame age: 3.2s · refresh: ");
    }

    [TestMethod]
    public void RefreshRenderHealth_OnlyRefreshesDiagnosticBindings()
    {
        MainViewModel viewModel = CreateViewModel();
        List<string> changed = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };
        HudRuntimeState stateBefore = viewModel.RuntimeState;
        TimeSpan timeBefore = viewModel.CurrentTime;
        bool playingBefore = viewModel.IsPlaying;

        viewModel.RefreshRenderHealth();

        CollectionAssert.AreEquivalent(
            new[] { nameof(MainViewModel.FrameHealthLabel), nameof(MainViewModel.RenderHealthLabel) },
            changed);
        Assert.AreEqual(stateBefore, viewModel.RuntimeState);
        Assert.AreEqual(timeBefore, viewModel.CurrentTime);
        Assert.AreEqual(playingBefore, viewModel.IsPlaying);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_MissingRendezvous_ReportsWaitingWithNoSessions()
    {
        MainViewModel viewModel = CreateViewModel();

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.Status));
        Assert.IsTrue(
            viewModel.Status.Contains("wait", StringComparison.OrdinalIgnoreCase),
            "Status should mention waiting for the rendezvous record.");
        Assert.AreEqual(HudRuntimeState.WaitingForHost, viewModel.RuntimeState);
        Assert.AreEqual("Waiting for host", viewModel.RuntimeStateLabel);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_StaleRendezvous_ReportsStatusWithNoSessions()
    {
        WriteRendezvousRecord(Now.AddMinutes(-10), Now.AddMinutes(-5));
        MainViewModel viewModel = CreateViewModel();

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.Status));
        Assert.AreEqual(HudRuntimeState.HostRecordStale, viewModel.RuntimeState);
        Assert.AreEqual("Host record stale", viewModel.RuntimeStateLabel);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_ValidRendezvous_PopulatesSessionsFromApi()
    {
        string sessionsJson = """
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {
                    "decodeRunId": "0f8fad5b-d9cb-469f-a165-70867728950e",
                    "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
                    "decoderId": "wotb-replay",
                    "decoderVersion": "1.2.3",
                    "schemaVersion": "1.0",
                    "status": "succeeded",
                    "capabilities": [ "battle-session" ],
                    "startedAtUtc": "2026-07-26T10:00:00+00:00",
                    "completedAtUtc": "2026-07-26T10:00:05+00:00",
                    "failureCode": null,
                    "failureSummary": null
                  },
                  "session": {
                    "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "gameVersion": "11.4.0",
                    "arenaIdentity": "arena-1",
                    "mapId": "maps/test",
                    "mapName": "Test Map",
                    "battleTimeUtc": "2026-07-26T09:55:00+00:00",
                    "duration": "0:05:00",
                    "viewpointParticipantId": "p1"
                  },
                  "participantCount": 14,
                  "positionCount": 500,
                  "eventCount": 10,
                  "rawRecordCount": 100
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(sessionsJson)));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(1, viewModel.Sessions.Count);
        Assert.AreEqual("Test Map", viewModel.Sessions[0].MapLabel);
        Assert.AreEqual(BattleSessionId, viewModel.Sessions[0].BattleSessionId);
        Assert.AreEqual(14, viewModel.Sessions[0].ParticipantCount);
        Assert.AreEqual(500, viewModel.Sessions[0].PositionCount);
        Assert.AreEqual("aa10bb20-cc30-dd40-ee50-ff60aa70bb80", viewModel.Sessions[0].SourceArtifactId);
        Assert.AreEqual("1 session(s)", viewModel.Status);
        Assert.AreEqual(HudRuntimeState.NoSessionSelected, viewModel.RuntimeState);
        Assert.IsFalse(viewModel.HasSessionListEmptyState);
    }

    [TestMethod]
    public async Task SearchText_NoMatchShowsActionableEmptyState()
    {
        string sessionsJson = $$"""
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{BattleSessionId:D}}",
                    "mapName": "Synthetic Ridge"
                  }
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(sessionsJson)));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        Assert.IsFalse(viewModel.HasSessionListEmptyState);

        viewModel.SearchText = "missing-map";

        Assert.IsTrue(viewModel.HasSessionListEmptyState);
        Assert.AreEqual("No matching sessions", viewModel.SessionListEmptyStateTitle);
        Assert.AreEqual("Clear the map filter to show all sessions.", viewModel.SessionListEmptyStateDetail);

        viewModel.SearchText = string.Empty;
        Assert.IsFalse(viewModel.HasSessionListEmptyState);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_ApiReturnsNull_PreservesExistingState()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse("null")));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_FallsBackToMapIdWhenMapNameIsNull()
    {
        string json = """
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {
                    "decodeRunId": "0f8fad5b-d9cb-469f-a165-70867728950e",
                    "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
                    "decoderId": "wotb-replay",
                    "decoderVersion": "1.2.3",
                    "schemaVersion": "1.0",
                    "status": "succeeded",
                    "capabilities": [],
                    "startedAtUtc": "2026-07-26T10:00:00+00:00",
                    "completedAtUtc": null,
                    "failureCode": null,
                    "failureSummary": null
                  },
                  "session": {
                    "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "gameVersion": null,
                    "arenaIdentity": null,
                    "mapId": "maps/unknown_map",
                    "mapName": null,
                    "battleTimeUtc": "2026-07-26T09:55:00+00:00",
                    "duration": "0:00:00",
                    "viewpointParticipantId": null
                  },
                  "participantCount": 0,
                  "positionCount": 0,
                  "eventCount": 0,
                  "rawRecordCount": 0
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(json)));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(1, viewModel.Sessions.Count);
        Assert.AreEqual("maps/unknown_map", viewModel.Sessions[0].MapLabel);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_MissingSession_SkipsUnidentifiedRow()
    {
        const string json = """
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": null,
                  "participantCount": 0,
                  "positionCount": 0,
                  "eventCount": 0,
                  "rawRecordCount": 0
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(json)));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.AreEqual("0 session(s)", viewModel.Status);
        Assert.AreEqual(HudRuntimeState.NoSessions, viewModel.RuntimeState);
        Assert.IsTrue(viewModel.HasSessionListEmptyState);
        Assert.AreEqual("No replay sessions", viewModel.SessionListEmptyStateTitle);
        Assert.AreEqual("Import a replay, then refresh this HUD.", viewModel.SessionListEmptyStateDetail);
        Assert.AreEqual("No sessions", viewModel.RuntimeStateLabel);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_SelectedSessionDisappears_ClearsReplayState()
    {
        string sessionsJson = $$"""
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{BattleSessionId:D}}",
                    "gameVersion": "11.18.0.7",
                    "mapName": "Synthetic Ridge"
                  },
                  "participantCount": 1,
                  "positionCount": 1,
                  "eventCount": 0,
                  "rawRecordCount": 1
                }
              ]
            }
            """;
        const string missingSessionJson = """
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": null,
                  "participantCount": 0,
                  "positionCount": 0,
                  "eventCount": 0,
                  "rawRecordCount": 0
                }
              ]
            }
            """;
        string detailJson = $$"""
            {
              "decodeRun": {},
              "session": {
                "battleSessionId": "{{BattleSessionId:D}}",
                "gameVersion": "11.18.0.7",
                "mapName": "Synthetic Ridge",
                "duration": "0:00:10"
              },
              "participants": [
                {
                  "participantId": "participant-1",
                  "teamNumber": 1,
                  "botStatus": "unknown"
                }
              ],
              "positions": [
                {
                  "participantId": "participant-1",
                  "sequence": 1,
                  "replayTime": "0:00:01",
                  "rawX": 10,
                  "rawY": 0,
                  "rawZ": 20,
                  "rawCoordinateSpace": "replay-world"
                }
              ],
              "positionsTruncated": false,
              "totalPositionCount": 1,
              "eventCount": 0,
              "rawRecordCount": 1
            }
            """;
        bool sessionDisappeared = false;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(
                BattleSessionId.ToString("D"),
                StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(detailJson));
            }

            return Task.FromResult(JsonResponse(
                sessionDisappeared ? missingSessionJson : sessionsJson));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = viewModel.Sessions[0];
        await WaitForConditionAsync(() => viewModel.Points.Count == 1, TimeSpan.FromSeconds(2));

        sessionDisappeared = true;
        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.IsNull(viewModel.SelectedSession);
        Assert.AreEqual(0, viewModel.Points.Count);
        Assert.AreEqual(0, viewModel.Participants.Count);
        Assert.IsNull(viewModel.MapName);
        Assert.AreEqual(TimeSpan.Zero, viewModel.Duration);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_MissingBattleTime_PreservesUnknownTimestamp()
    {
        string json = $$"""
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{BattleSessionId:D}}",
                    "gameVersion": "11.18.0.7",
                    "battleTimeUtc": null,
                    "duration": null
                  },
                  "participantCount": 0,
                  "positionCount": 0,
                  "eventCount": 0,
                  "rawRecordCount": 0
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(json)));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(1, viewModel.Sessions.Count);
        Assert.IsNull(viewModel.Sessions[0].BattleTimeUtc);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_HttpError_SetsErrorStatus()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual("Host unreachable", viewModel.Status);
        Assert.AreEqual(0, viewModel.Sessions.Count);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_CancelledBeforeCall_PreservesState()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        MainViewModel viewModel = CreateViewModel();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        try
        {
            await viewModel.RefreshSessionsAsync(cts.Token);
            Assert.Fail("Expected OperationCanceledException when token is already cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Expected — cancellation is checked before any work is done.
        }

        Assert.AreEqual("", viewModel.Status);
        Assert.AreEqual(0, viewModel.Sessions.Count);
    }

    [TestMethod]
    public async Task SelectSession_LoadsPositionDataIntoPoints()
    {
        string detailJson = """
            {
              "decodeRun": {
                "decodeRunId": "6b1c9e52-3d4a-4f7b-9c0d-1e2f3a4b5c6d",
                "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
                "decoderId": "wotb-replay",
                "decoderVersion": "1.2.3",
                "schemaVersion": "1.0",
                "status": "succeeded",
                "capabilities": [],
                "startedAtUtc": "2026-07-26T10:00:00+00:00",
                "completedAtUtc": null,
                "failureCode": null,
                "failureSummary": null
              },
              "session": {
                "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                "gameVersion": null,
                "arenaIdentity": null,
                "mapId": null,
                "mapName": null,
                "battleTimeUtc": "2026-07-26T09:55:00+00:00",
                "duration": "0:00:10",
                "viewpointParticipantId": null
              },
              "participants": [
                { "participantId": "p1", "entityId": 100, "teamNumber": 1, "playerName": "Alpha", "clanTag": null, "tankId": null, "tankName": null, "tankClass": null, "botStatus": "unknown", "botStatusConfidence": null }
              ],
              "positions": [
                { "participantId": "p1", "entityId": 100, "sequence": 1, "replayTime": "0:00:01", "rawX": 10.0, "rawY": 0.0, "rawZ": 20.0, "normalizedX": null, "normalizedY": null, "rawCoordinateSpace": "replay-world", "normalizedCoordinateSpace": null },
                { "participantId": "p1", "entityId": 100, "sequence": 2, "replayTime": "0:00:02", "rawX": 15.0, "rawY": 0.0, "rawZ": 25.0, "normalizedX": null, "normalizedY": null, "rawCoordinateSpace": "replay-world", "normalizedCoordinateSpace": null }
              ],
              "positionsTruncated": false,
              "totalPositionCount": 2,
              "eventCount": 0,
              "rawRecordCount": 0,
              "warnings": []
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(detailJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":1,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);
        List<string> changed = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };

        // Populate _client via a refresh so the SelectedSession load has a client to use
        await viewModel.RefreshSessionsAsync();

        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        // Poll until the fire-and-forget detail load completes.
        await WaitForConditionAsync(() => viewModel.Points.Count > 0, TimeSpan.FromSeconds(2));

        Assert.AreEqual(TimeSpan.FromSeconds(10), viewModel.CurrentTime);
        Assert.IsTrue(
            changed.Contains(nameof(MainViewModel.CurrentTimeSeconds)),
            "Selecting a session must publish the initial timeline position so the W2S frame, including the pen badge, loads immediately.");
        Assert.AreEqual(2, viewModel.Points.Count);
        Assert.AreEqual(10.0, viewModel.Points[0].X);
        Assert.AreEqual(20.0, viewModel.Points[0].Y);
        Assert.AreEqual(1, viewModel.Points[0].TeamNumber);
        Assert.AreEqual("p1", viewModel.Points[0].ParticipantId);
        Assert.AreEqual(15.0, viewModel.Points[1].X);
        Assert.AreEqual(25.0, viewModel.Points[1].Y);
        Assert.AreEqual(1, viewModel.Participants.Count);
        Assert.AreEqual("Alpha", viewModel.Participants[0].PlayerName);
        Assert.AreEqual(0, viewModel.EventCount);
        Assert.AreEqual(0, viewModel.Events.Count);
    }

    [TestMethod]
    public async Task SelectSession_NullDetail_NoPointsLoaded()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse("null")));
        MainViewModel viewModel = CreateViewModel(handler);

        // Populate client via refresh
        await viewModel.RefreshSessionsAsync();

        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test", null, Now, 1, 1);

        // The detail load fires asynchronously, but returns null immediately.
        // Wait for it to settle, then verify no points were loaded.
        await WaitForConditionAsync(
            () => viewModel.Status is not ("" or "Host unreachable"),
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, viewModel.Points.Count,
            "Null detail response must not populate points.");
    }

    [TestMethod]
    public void SelectSession_NoClientSet_NoErrorThrown()
    {
        MainViewModel viewModel = CreateViewModel();

        // Must not throw even though _client is null and SelectedSession is set.
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test", null, Now, 1, 1);

        Assert.AreEqual(0, viewModel.Points.Count);
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_SelectedSessionGuard_PreventsCascade()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));

        // Block the HTTP handler so the refresh stays in-flight long enough
        // to observe the _isRefreshingSessions guard.
        TaskCompletionSource<HttpResponseMessage> blockTcs = new();
        int detailRequestCount = 0;
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/sessions/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref detailRequestCount);
            }

            return blockTcs.Task;
        });
        MainViewModel viewModel = CreateViewModel(handler);

        // Start a refresh — it blocks inside RefreshSessionsCoreAsync on the HTTP call.
        Task refreshTask = viewModel.RefreshSessionsAsync();

        // Give the refresh a moment to enter the core method and set the flag.
        await Task.Delay(100);

        // Simulate what WPF binding does when Sessions.Clear() fires: it sets
        // SelectedSession to null. The _isRefreshingSessions guard must
        // suppress the resulting detail load.
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Cascade Test", null, Now, 1, 1);

        // Unblock and await the refresh.
        blockTcs.SetResult(JsonResponse(
            """{"offset":0,"limit":200,"count":0,"items":[]}"""));
        await refreshTask;

        Assert.AreEqual(
            0, detailRequestCount,
            "The _isRefreshingSessions guard must suppress the " +
            "SelectedSession setter cascade during refresh.");
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_SelectionChangesToRetainedSession_LoadsNewDetailOnce()
    {
        Guid secondSessionId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string firstPageJson = $$"""
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{BattleSessionId:D}}",
                    "gameVersion": "11.18.0.7",
                    "mapName": "Session A"
                  }
                }
              ]
            }
            """;
        string refreshedPageJson = $$"""
            {
              "offset": 0,
              "limit": 200,
              "count": 2,
              "items": [
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{BattleSessionId:D}}",
                    "gameVersion": "11.18.0.7",
                    "mapName": "Session A"
                  }
                },
                {
                  "decodeRun": {},
                  "session": {
                    "battleSessionId": "{{secondSessionId:D}}",
                    "gameVersion": "11.18.0.7",
                    "mapName": "Session B"
                  }
                }
              ]
            }
            """;
        string firstDetailJson = $$"""
            {
              "decodeRun": {},
              "session": {
                "battleSessionId": "{{BattleSessionId:D}}",
                "gameVersion": "11.18.0.7",
                "mapName": "Session A"
              },
              "positions": [
                {
                  "sequence": 1,
                  "replayTime": "0:00:01",
                  "rawX": 1,
                  "rawY": 0,
                  "rawZ": 1,
                  "rawCoordinateSpace": "replay-world"
                }
              ]
            }
            """;
        string secondDetailJson = $$"""
            {
              "decodeRun": {},
              "session": {
                "battleSessionId": "{{secondSessionId:D}}",
                "gameVersion": "11.18.0.7",
                "mapName": "Session B"
              },
              "positions": [
                {
                  "sequence": 1,
                  "replayTime": "0:00:01",
                  "rawX": 2,
                  "rawY": 0,
                  "rawZ": 2,
                  "rawCoordinateSpace": "replay-world"
                }
              ]
            }
            """;
        TaskCompletionSource<HttpResponseMessage> blockedRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> refreshStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int listRequestCount = 0;
        int secondDetailRequestCount = 0;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sessions", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref listRequestCount) == 1)
                {
                    return Task.FromResult(JsonResponse(firstPageJson));
                }

                refreshStarted.TrySetResult(true);
                return blockedRefresh.Task;
            }

            if (path.Contains(secondSessionId.ToString("D"), StringComparison.Ordinal))
            {
                Interlocked.Increment(ref secondDetailRequestCount);
                return Task.FromResult(JsonResponse(secondDetailJson));
            }

            return Task.FromResult(JsonResponse(firstDetailJson));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = viewModel.Sessions[0];
        await WaitForConditionAsync(
            () => viewModel.MapName == "Session A" && viewModel.Points.Count == 1,
            TimeSpan.FromSeconds(2));

        Task refreshTask = viewModel.RefreshSessionsAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedSession = new SessionRow(
            secondSessionId, "Session B", null, null, 0, 1);
        blockedRefresh.SetResult(JsonResponse(refreshedPageJson));
        await refreshTask;
        await WaitForConditionAsync(
            () => viewModel.MapName == "Session B" && viewModel.Points.Count == 1,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(secondSessionId, viewModel.SelectedSession?.BattleSessionId);
        Assert.AreEqual(2.0, viewModel.Points[0].X);
        Assert.AreEqual(1, secondDetailRequestCount);
    }

    [TestMethod]
    public async Task StreamService_SessionListChanged_TriggersRefresh()
    {
        string sessionsJson = """
            {
              "offset": 0,
              "limit": 200,
              "count": 1,
              "items": [
                {
                  "decodeRun": {
                    "decodeRunId": "0f8fad5b-d9cb-469f-a165-70867728950e",
                    "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
                    "decoderId": "wotb-replay",
                    "decoderVersion": "1.2.3",
                    "schemaVersion": "1.0",
                    "status": "succeeded",
                    "capabilities": [],
                    "startedAtUtc": "2026-07-26T10:00:00+00:00",
                    "completedAtUtc": null,
                    "failureCode": null,
                    "failureSummary": null
                  },
                  "session": {
                    "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                    "gameVersion": "11.4.0",
                    "arenaIdentity": "arena-1",
                    "mapId": "maps/test",
                    "mapName": "Stream Map",
                    "battleTimeUtc": "2026-07-26T09:55:00+00:00",
                    "duration": "0:05:00",
                    "viewpointParticipantId": "p1"
                  },
                  "participantCount": 14,
                  "positionCount": 500,
                  "eventCount": 10,
                  "rawRecordCount": 100
                }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(sessionsJson)));
        MockTelemetryStreamService streamService = new();
        MainViewModel viewModel = CreateViewModel(handler, streamService);

        // Initial refresh populates sessions and connects the stream.
        await viewModel.RefreshSessionsAsync();
        Assert.AreEqual(1, viewModel.Sessions.Count);
        Assert.AreEqual(1, streamService.ConnectCallCount);

        // Simulate a stream event arriving.
        streamService.RaiseSessionListChanged();

        // The stream event fires a fire-and-forget refresh. Wait for it.
        await WaitForConditionAsync(
            () => viewModel.Status is not ("" or "Host unreachable") && viewModel.Sessions.Count > 0,
            TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, viewModel.Sessions.Count);
    }

    [TestMethod]
    public async Task StreamService_ConnectAsync_CalledWithCorrectBaseUri()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(
            """{"offset":0,"limit":200,"count":0,"items":[]}""")));
        MockTelemetryStreamService streamService = new();
        MainViewModel viewModel = CreateViewModel(handler, streamService);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(1, streamService.ConnectCallCount);
        Assert.IsNotNull(streamService.LastConnectedUri);
        Assert.AreEqual("http://127.0.0.1:8123/", streamService.LastConnectedUri!.ToString());
    }

    [TestMethod]
    public async Task SelectSession_WithDamageEvents_ComputesTeamStats()
    {
        string detailJson = """
            {
              "decodeRun": {
                "decodeRunId": "6b1c9e52-3d4a-4f7b-9c0d-1e2f3a4b5c6d",
                "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
                "decoderId": "wotb-replay",
                "decoderVersion": "1.2.3",
                "schemaVersion": "1.0",
                "status": "succeeded",
                "capabilities": [],
                "startedAtUtc": "2026-07-26T10:00:00+00:00",
                "completedAtUtc": null,
                "failureCode": null,
                "failureSummary": null
              },
              "session": {
                "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                "gameVersion": null,
                "arenaIdentity": null,
                "mapId": null,
                "mapName": null,
                "battleTimeUtc": "2026-07-26T09:55:00+00:00",
                "duration": "0:05:00",
                "viewpointParticipantId": null
              },
              "participants": [
                { "participantId": "p1", "entityId": 100, "teamNumber": 1, "playerName": "Alpha", "clanTag": null, "tankId": null, "tankName": null, "tankClass": null, "botStatus": "unknown", "botStatusConfidence": null },
                { "participantId": "p2", "entityId": 200, "teamNumber": 2, "playerName": "Bravo", "clanTag": null, "tankId": null, "tankName": null, "tankClass": null, "botStatus": "unknown", "botStatusConfidence": null }
              ],
              "positions": [],
              "positionsTruncated": false,
              "totalPositionCount": 0,
              "eventCount": 4,
              "rawRecordCount": 0,
              "warnings": [],
              "events": [
                { "kind": "Damage", "replayTime": "0:00:10", "participantId": "p1", "summary": "Damage: 300 HP" },
                { "kind": "Damage", "replayTime": "0:00:20", "participantId": "p1", "summary": "Damage: 150 HP" },
                { "kind": "Damage", "replayTime": "0:00:30", "participantId": "p2", "summary": "Damage: 500 HP" },
                { "kind": "Destroyed", "replayTime": "0:01:00", "participantId": "p2", "summary": "Destroyed" }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
                return Task.FromResult(JsonResponse(detailJson));
            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":1,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(BattleSessionId, "Stats Test", null, Now, 2, 0);

        await WaitForConditionAsync(() => viewModel.Events.Count > 0, TimeSpan.FromSeconds(2));

        Assert.AreEqual(4, viewModel.Events.Count);
        Assert.AreEqual(450, viewModel.DamageTeam1, "Team 1 received 300+150=450 damage");
        Assert.AreEqual(500, viewModel.DamageTeam2, "Team 2 received 500 damage");
        Assert.AreEqual(0, viewModel.KillsTeam1);
        Assert.AreEqual(1, viewModel.KillsTeam2, "Team 2 had 1 destroyed");
        Assert.AreEqual(4.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("4×", viewModel.SpeedLabel);
        Assert.IsNull(viewModel.MapName, "Map name should be null when session has no map name");
    }

    [TestMethod]
    public void PlaybackSpeed_DefaultsToFour()
    {
        MainViewModel viewModel = CreateViewModel();

        Assert.AreEqual(4.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("4×", viewModel.SpeedLabel);
    }

    [TestMethod]
    public void HudUiVersionLabel_UsesIndependentPresentationVersion()
    {
        MainViewModel viewModel = CreateViewModel();

        Assert.AreEqual("HUD UI v0.6.0-alpha", viewModel.HudUiVersionLabel);
    }

    [TestMethod]
    public void RuntimeState_DefaultsToStartingWithSafeDiagnostics()
    {
        MainViewModel viewModel = CreateViewModel();

        Assert.AreEqual(HudRuntimeState.Starting, viewModel.RuntimeState);
        Assert.AreEqual("Starting", viewModel.RuntimeStateLabel);
        Assert.AreEqual("Preparing overlay", viewModel.RuntimeStateDetail);
        Assert.AreEqual("No frame received", viewModel.FrameStatusLabel);
        Assert.IsNotNull(viewModel.RuntimeStateAccent);
    }

    [TestMethod]
    public void SpeedLabel_FormatsAllSpeedsCorrectly()
    {
        MainViewModel viewModel = CreateViewModel();

        // Cycle through all speeds and verify each label.
        viewModel.CycleSpeedCommand.Execute(null); // 4→8
        Assert.AreEqual(8.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("8×", viewModel.SpeedLabel);

        viewModel.CycleSpeedCommand.Execute(null); // 8→0.5
        Assert.AreEqual(0.5, viewModel.PlaybackSpeed);
        Assert.AreEqual("0.5×", viewModel.SpeedLabel);

        viewModel.CycleSpeedCommand.Execute(null); // 0.5→1
        Assert.AreEqual(1.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("1×", viewModel.SpeedLabel);

        viewModel.CycleSpeedCommand.Execute(null); // 1→2
        Assert.AreEqual(2.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("2×", viewModel.SpeedLabel);

        viewModel.CycleSpeedCommand.Execute(null); // 2→4
        Assert.AreEqual(4.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("4×", viewModel.SpeedLabel);
    }

    [TestMethod]
    public void JumpToStart_SetsCurrentTimeToZero()
    {
        MainViewModel viewModel = CreateViewModel();

        // Set to a non-zero time first, then jump to start.
        viewModel.CurrentTime = TimeSpan.FromSeconds(30);
        Assert.AreEqual(30.0, viewModel.CurrentTimeSeconds);

        viewModel.JumpToStartCommand.Execute(null);
        Assert.AreEqual(TimeSpan.Zero, viewModel.CurrentTime);
        Assert.AreEqual(0.0, viewModel.CurrentTimeSeconds);
    }

    [TestMethod]
    public void CurrentTimeSeconds_TwoWayBinding_RoundTrips()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.CurrentTimeSeconds = 45.5;
        Assert.AreEqual(TimeSpan.FromSeconds(45.5), viewModel.CurrentTime);
        Assert.AreEqual(45.5, viewModel.CurrentTimeSeconds);

        viewModel.CurrentTime = TimeSpan.FromSeconds(120);
        Assert.AreEqual(120.0, viewModel.CurrentTimeSeconds);
    }

    [TestMethod]
    public void JumpToEnd_ZeroDuration_IsNoOp()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.CurrentTime = TimeSpan.FromSeconds(10);
        viewModel.JumpToEndCommand.Execute(null);

        // Duration is zero, so JumpToEnd should be a no-op.
        Assert.AreEqual(TimeSpan.FromSeconds(10), viewModel.CurrentTime);
    }

    [TestMethod]
    public void TogglePlayPause_ZeroDuration_IsNoOp()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.PlayPauseCommand.Execute(null);

        // Duration is zero, so IsPlaying stays false.
        Assert.IsFalse(viewModel.IsPlaying);
    }

    [TestMethod]
    public void ScrubRelative_ZeroDuration_IsNoOp()
    {
        MainViewModel viewModel = CreateViewModel();

        // No session loaded, duration is zero — should be no-op.
        viewModel.ScrubRelative(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TimeSpan.Zero, viewModel.CurrentTime);
    }

    [TestMethod]
    public void SetPlaybackSpeed_AcceptsDefinedSpeeds()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.SetPlaybackSpeed(8.0);
        Assert.AreEqual(8.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("8×", viewModel.SpeedLabel);

        viewModel.SetPlaybackSpeed(0.5);
        Assert.AreEqual(0.5, viewModel.PlaybackSpeed);
        Assert.AreEqual("0.5×", viewModel.SpeedLabel);

        viewModel.SetPlaybackSpeed(2.0);
        Assert.AreEqual(2.0, viewModel.PlaybackSpeed);
        Assert.AreEqual("2×", viewModel.SpeedLabel);
    }

    [TestMethod]
    public void SetPlaybackSpeed_RejectsUndefinedSpeeds()
    {
        MainViewModel viewModel = CreateViewModel();

        viewModel.SetPlaybackSpeed(3.0);
        Assert.AreEqual(4.0, viewModel.PlaybackSpeed, "Should stay at default 4.0");

        viewModel.SetPlaybackSpeed(7.0);
        Assert.AreEqual(4.0, viewModel.PlaybackSpeed, "Should stay at default 4.0");
    }

    [TestMethod]
    public async Task StreamService_NullStreamService_NoCrashOnRefresh()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) => Task.FromResult(JsonResponse(
            """{"offset":0,"limit":200,"count":0,"items":[]}""")));
        MainViewModel viewModel = CreateViewModel(handler, streamService: null);

        // Must not throw.
        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual("0 session(s)", viewModel.Status);
        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.AreEqual(HudRuntimeState.NoSessions, viewModel.RuntimeState);
        Assert.IsTrue(viewModel.HasSessionListEmptyState);
        Assert.AreEqual("No replay sessions", viewModel.SessionListEmptyStateTitle);
        Assert.AreEqual("Import a replay, then refresh this HUD.", viewModel.SessionListEmptyStateDetail);
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Condition not met within {timeout.TotalSeconds:F1} s.");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private void WriteRendezvousRecord(DateTimeOffset issuedAtUtc, DateTimeOffset expiresAtUtc)
    {
        string json = $$"""
            {
              "schemaVersion": "1.0",
              "instanceId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "processId": 1234,
              "baseUri": "http://127.0.0.1:8123",
              "capability": "cap-token-test",
              "issuedAtUtc": "{{issuedAtUtc:O}}",
              "expiresAtUtc": "{{expiresAtUtc:O}}"
            }
            """;
        File.WriteAllText(_rendezvousPath, json);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesNameplatesAndExcludesNonVisible()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 1, "playerName": "Self", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 0.0, "screenX": 960.0, "screenY": 540.0, "depth": 1.0, "inViewport": true },
                { "entityId": 2, "playerName": "Alpha", "tankName": "TankA", "clanTag": null, "teamNumber": 2, "hpFraction": 0.5, "alive": true, "distanceMeters": 120.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true, "screenHeadingDegrees": -35.0 },
                { "entityId": 3, "playerName": "Behind", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 50.0, "screenX": null, "screenY": null, "depth": null, "inViewport": false },
                { "entityId": 4, "playerName": "Offscreen", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 200.0, "screenX": 5000.0, "screenY": 5000.0, "depth": 10.0, "inViewport": false },
                { "entityId": 5, "playerName": "Wreck", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 0.0, "alive": false, "distanceMeters": 90.0, "screenX": 700.0, "screenY": 350.0, "depth": 60.0, "inViewport": true }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // Own tank (distance 0), behind-camera, and off-viewport are excluded.
        Assert.AreEqual(HudRuntimeState.ReplayPaused, viewModel.RuntimeState);
        Assert.AreEqual("Replay paused", viewModel.RuntimeStateLabel);
        Assert.AreEqual("Frame @ 200.0s", viewModel.FrameStatusLabel);
        Assert.AreEqual(2, viewModel.Nameplates.Count);
        NameplateItem alpha = viewModel.Nameplates.Single(item => item.EntityId == 2);
        Assert.AreEqual("Alpha", alpha.Label);
        Assert.AreEqual(800.0, alpha.ScreenX, 1e-9);
        Assert.AreEqual(400.0, alpha.ScreenY, 1e-9);
        Assert.AreEqual(2, alpha.TeamNumber);
        Assert.AreEqual(0.5, alpha.HpFraction, 1e-9);
        Assert.IsTrue(alpha.Alive);
        Assert.IsNotNull(alpha.ScreenHeadingDegrees);
        Assert.AreEqual(-35.0, alpha.ScreenHeadingDegrees!.Value, 1e-9);
        Assert.IsTrue(viewModel.Nameplates.Any(item => item.EntityId == 5 && !item.Alive));
        Assert.AreEqual(200.0, viewModel.LastFrameReplayTimeSeconds!.Value, 1e-9);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_NotifiesSelectedShellWhenServerProvidesDefault()
    {
        // The server supplies the stock shell as the active default when the
        // user has not chosen one. The bound ComboBox must receive a
        // SelectedPenShellName notification or it can remain visually blank
        // even though the badge is scored with the stock shell.
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [],
              "penShells": [
                { "name": "ap_shell", "kind": "ArmorPiercing" },
                { "name": "he_shell", "kind": "HighExplosive" }
              ],
              "penShell": "ap_shell"
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);
        List<string> changed = [];
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual("ap_shell", viewModel.SelectedPenShellName);
        Assert.AreEqual("ap_shell", viewModel.PenShell);
        Assert.IsTrue(
            changed.Contains(nameof(MainViewModel.SelectedPenShellName)),
            "The shell selector must be notified when the server's default becomes active.");
        Assert.IsTrue(
            changed.Contains(nameof(MainViewModel.PenShell)),
            "The active shell property must notify bindings when the server changes it.");

        int selectedShellNotifications = changed.Count(
            name => name == nameof(MainViewModel.SelectedPenShellName));
        int activeShellNotifications = changed.Count(
            name => name == nameof(MainViewModel.PenShell));

        // A stable server default must not notify again: MainWindow responds
        // to SelectedPenShellName by fetching a frame, so repeated
        // notifications would create an endless refresh loop.
        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual(
            selectedShellNotifications,
            changed.Count(name => name == nameof(MainViewModel.SelectedPenShellName)));
        Assert.AreEqual(
            activeShellNotifications,
            changed.Count(name => name == nameof(MainViewModel.PenShell)));
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_NotReadyAssessmentSuppressesLegacyColoredBadge()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [],
              "penBadge": {
                "aimedEntityId": 2,
                "face": "Front",
                "band": "Pen",
                "effectiveArmorMm": 90.0,
                "penetrationMmAtRange": 120.0,
                "ricochet": false
              },
              "penetration": {
                "status": "not_ready",
                "primaryReason": "team.target_unknown",
                "reasons": ["team.target_unknown"],
                "modelVersion": "penetration/0.3.0-alpha",
                "badge": {
                  "aimedEntityId": 2,
                  "face": "Front",
                  "band": "Pen",
                  "effectiveArmorMm": 90.0,
                  "penetrationMmAtRange": 120.0,
                  "ricochet": false
                }
              }
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);
        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.IsNull(viewModel.PenBadge);
        Assert.AreEqual("PEN — TARGET TEAM UNKNOWN", viewModel.PenReadinessLabel);
        Assert.IsTrue(viewModel.HasPenReadiness);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_MalformedReadyAssessmentFailsClosed()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [],
              "penetration": {
                "status": "ready",
                "primaryReason": "none",
                "reasons": [],
                "modelVersion": "penetration/0.3.0-alpha",
                "badge": {
                  "aimedEntityId": 2,
                  "face": "Unknown",
                  "band": "Pen",
                  "effectiveArmorMm": 90.0,
                  "penetrationMmAtRange": 120.0,
                  "ricochet": false
                }
              }
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);
        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.IsNull(viewModel.PenBadge);
        Assert.AreEqual("PEN — INVALID INPUT", viewModel.PenReadinessLabel);
    }

    [TestMethod]
    [DataRow("Pen", true)]
    [DataRow("Marginal", true)]
    [DataRow("NoPen", true)]
    [DataRow("Unknown", false)]
    [DataRow("", false)]
    [DataRow("pen", false)]
    public void IsRenderablePenetrationBand_RejectsUnknownOrMalformedValues(
        string band,
        bool expected) =>
        Assert.AreEqual(expected, MainViewModel.IsRenderablePenetrationBand(band));

    [TestMethod]
    [DataRow("Pen", "Front", false, true)]
    [DataRow("Marginal", "Side", false, true)]
    [DataRow("NoPen", "Back", true, true)]
    [DataRow("Pen", "Unknown", false, false)]
    [DataRow("Pen", "Front", true, false)]
    [DataRow("NoPen", "Front", false, true)]
    public void IsRenderablePenetrationBadge_RejectsContradictoryModernPayloads(
        string band,
        string face,
        bool ricochet,
        bool expected)
    {
        OverlayPenBadgeResponse badge = new()
        {
            AimedEntityId = 42,
            Band = band,
            Face = face,
            Ricochet = ricochet,
            EffectiveArmorMm = 90,
            PenetrationMmAtRange = 120,
        };

        Assert.AreEqual(
            expected,
            MainViewModel.IsRenderablePenetrationBadge(badge, requireKnownFace: true));
    }

    [TestMethod]
    [DataRow("session.association_missing", "PEN — SESSION NOT BOUND")]
    [DataRow("build.replay_mismatch", "PEN — BUILD MISMATCH")]
    [DataRow("armor.surface_miss", "PEN — ARMOR SURFACE UNKNOWN")]
    [DataRow("unexpected.reason", "PEN — NOT READY")]
    public void PenetrationReadinessLabel_MapsStableNeutralText(
        string reason,
        string expected) =>
        Assert.AreEqual(expected, MainViewModel.PenetrationReadinessLabel(reason));

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveFailureClearsPriorPenetrationVerdict()
    {
        string readyFrame = """
            {
              "replayTimeSeconds": 150.5,
              "tanks": [],
              "penShells": [{ "name": "ap_shell", "kind": "ArmorPiercing" }],
              "penShell": "ap_shell",
              "penetration": {
                "status": "ready",
                "primaryReason": "none",
                "reasons": [],
                "modelVersion": "penetration/0.3.0-alpha",
                "badge": {
                  "aimedEntityId": 2,
                  "face": "Front",
                  "band": "Pen",
                  "effectiveArmorMm": 90.0,
                  "penetrationMmAtRange": 120.0,
                  "ricochet": false
                }
              }
            }
            """;
        int liveCalls = 0;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                "/live/frame",
                StringComparison.OrdinalIgnoreCase))
            {
                liveCalls++;
                return Task.FromResult(liveCalls == 1
                    ? JsonResponse(readyFrame)
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(JsonResponse(
                """{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);
        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);
        Assert.AreEqual(HudRuntimeState.LiveReady, viewModel.RuntimeState);
        Assert.IsNotNull(viewModel.PenBadge);
        Assert.AreEqual("ap_shell", viewModel.PenShell);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual(HudRuntimeState.LiveUnavailable, viewModel.RuntimeState);
        Assert.AreEqual("Live unavailable", viewModel.RuntimeStateLabel);
        Assert.AreEqual("Last live frame retained", viewModel.FrameStatusLabel);
        Assert.IsNull(viewModel.PenBadge);
        Assert.AreEqual("PEN — LIVE FRAME UNAVAILABLE", viewModel.PenReadinessLabel);
        Assert.IsNull(viewModel.PenShell);
        Assert.IsEmpty(viewModel.PenShells);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_ReplayFailureMarksLastGoodFrameStale()
    {
        const string frameJson = """
            {
              "replayTimeSeconds": 42.0,
              "tanks": []
            }
            """;
        int frameCalls = 0;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                frameCalls++;
                return Task.FromResult(frameCalls == 1
                    ? JsonResponse(frameJson)
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """{"session":{"battleSessionId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","mapName":"Test Map"},"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);
        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(BattleSessionId, "Test Map", null, Now, 0, 0);
        await WaitForConditionAsync(
            () => viewModel.RuntimeState == HudRuntimeState.ReplayReady,
            TimeSpan.FromSeconds(2));

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);
        Assert.AreEqual(HudRuntimeState.ReplayPaused, viewModel.RuntimeState);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual(HudRuntimeState.FrameStale, viewModel.RuntimeState);
        Assert.AreEqual("Frame stale", viewModel.RuntimeStateLabel);
        Assert.AreEqual("Last replay frame retained", viewModel.FrameStatusLabel);
        Assert.AreEqual(42.0, viewModel.LastFrameReplayTimeSeconds);
    }

    [TestMethod]
    public async Task SelectingNoSession_ClearsPriorPenetrationFrameImmediately()
    {
        string readyFrame = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 2, "playerName": "Enemy", "tankName": "Tank", "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 50.0, "screenX": 800.0, "screenY": 400.0, "depth": 40.0, "inViewport": true }
              ],
              "penetration": {
                "status": "ready",
                "primaryReason": "none",
                "reasons": [],
                "modelVersion": "penetration/0.3.0-alpha",
                "badge": {
                  "aimedEntityId": 2,
                  "face": "Front",
                  "band": "Pen",
                  "effectiveArmorMm": 90.0,
                  "penetrationMmAtRange": 120.0,
                  "ricochet": false
                }
              }
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(readyFrame));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                    {"session":{"battleSessionId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","mapName":"Test Map","duration":"00:01:00"},"participants":[],"positions":[],"events":[]}
                    """));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);
        await viewModel.RefreshOverlayFrameAsync(1920, 1080);
        Assert.IsNotNull(viewModel.PenBadge);
        Assert.HasCount(1, viewModel.Nameplates);

        viewModel.SelectedSession = null;

        Assert.IsNull(viewModel.PenBadge);
        Assert.IsEmpty(viewModel.Nameplates);
        Assert.IsEmpty(viewModel.PenShells);
        Assert.AreEqual(HudRuntimeState.NoSessionSelected, viewModel.RuntimeState);
        Assert.AreEqual("PEN — SESSION NOT SELECTED", viewModel.PenReadinessLabel);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveModeCarriesL1HealthIntoNameplate()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 3760578, "playerName": null, "tankName": null, "clanTag": null, "teamNumber": null, "hpFraction": 0.792, "alive": true, "distanceMeters": 120.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true, "maxHealth": 1550, "currentHealth": 1228 }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/live/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // The L1 entity-base HP flows through the projected live frame into
        // the nameplate: exact readout values, fraction, and the alive byte.
        NameplateItem tank = viewModel.Nameplates.Single();
        Assert.AreEqual(3760578, tank.EntityId);
        Assert.AreEqual(1228L, tank.CurrentHealth);
        Assert.AreEqual(1550L, tank.MaxHealth);
        Assert.AreEqual(0.792, tank.HpFraction, 1e-6);
        Assert.IsTrue(tank.Alive);
        // Live mode keeps the decode feed empty.
        Assert.AreEqual(0, viewModel.KillFeed.Count);
        Assert.AreEqual(0, viewModel.Scoreboard.Count);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveModeUsesSelectionOnlyAsAssociationAssertion()
    {
        // Live mode is auto-bound by the server's managed replay association.
        // A selected session is sent only as an assertion so the host can join
        // decoded identity; it must never choose a different live source.
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 100, "playerName": "pilot", "tankName": null, "clanTag": "TAG", "teamNumber": 1, "hpFraction": 0.0, "alive": true, "distanceMeters": 120.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true, "maxHealth": 0, "currentHealth": 0 }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        Uri? liveFrameUri = null;
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/live/frame", StringComparison.OrdinalIgnoreCase))
            {
                liveFrameUri = request.RequestUri;
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;
        viewModel.SelectedSession = new SessionRow(BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.IsNotNull(liveFrameUri);
        Assert.IsTrue(
            liveFrameUri!.Query.Contains($"sessionId={BattleSessionId:D}", StringComparison.OrdinalIgnoreCase));
        NameplateItem tank = viewModel.Nameplates.Single();
        Assert.AreEqual("pilot", tank.Label);
        Assert.AreEqual(1, tank.TeamNumber);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveModeSuppressesOwnNameplateByIdentity()
    {
        // Own-nameplate refinement (name-join design step 4): live mode
        // identifies the player's own tank via the decoded viewpoint id on
        // the frame (OwnEntityId) — the CAM-001 chase eye sits at the
        // turret-level aim point ~1.9 m above the hull center, so the
        // <1.0 m distance heuristic can't catch it. The own tank still
        // reaches the minimap; only its nameplate is suppressed.
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 1.9, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "ownEntityId": 3760577,
              "tanks": [
                { "entityId": 3760577, "playerName": "mrkool1138", "tankName": "GB08_Churchill_I", "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 1.9, "worldX": 0.0, "worldZ": 0.0, "screenX": 960.0, "screenY": 620.0, "depth": 0.5, "inViewport": true },
                { "entityId": 3760575, "playerName": "sother_XD", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 0.206, "alive": true, "distanceMeters": 33.6, "worldX": 50.0, "worldZ": -50.0, "screenX": 800.0, "screenY": 400.0, "depth": 60.0, "inViewport": true }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/live/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.EndsWith("/maps/boundaries", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    [ { "mapId": "test-map", "minX": -100.0, "maxX": 100.0, "minZ": -100.0, "maxZ": 100.0 } ]
                    """));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","mapId":"test-map","mapName":"Test Map"},"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", "test-map", Now, 1, 2);

        // The boundary fetch is fire-and-forget; wait for it to land so the
        // minimap normalization has a real extent to map against.
        for (int i = 0; i < 100 && viewModel.WorldMinX == 0; i++)
        {
            await Task.Delay(10);
        }

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // Exactly one nameplate: the enemy's. The own tank (1.9 m, beyond
        // the old distance heuristic) is suppressed via OwnEntityId but
        // still flows to the minimap — suppression is nameplate-only.
        NameplateItem nameplate = viewModel.Nameplates.Single();
        Assert.AreEqual(3760575, nameplate.EntityId);
        Assert.AreEqual("sother_XD", nameplate.Label);
        Assert.IsTrue(viewModel.MinimapItems.Any(item => item.EntityId == 3760577));
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveModeShowsOwnEdgeMarker_WhenOwnTankOffViewport()
    {
        // Own-nameplate refinement, the "honest self marker" half: when the
        // decoded viewpoint tank projects OFF the viewport (the capture's
        // chase eye puts the hull below the 640x360 rect at screenY ~500),
        // the HUD gets one clamped edge marker pointing back at the hull.
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 1.9, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "ownEntityId": 3760577,
              "tanks": [
                { "entityId": 3760577, "playerName": "mrkool1138", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 2.3, "worldX": 0.0, "worldZ": 0.0, "screenX": 358.9, "screenY": 500.0, "depth": 0.5, "inViewport": false },
                { "entityId": 3760575, "playerName": "sother_XD", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 0.2, "alive": true, "distanceMeters": 33.6, "worldX": 50.0, "worldZ": -50.0, "screenX": 800.0, "screenY": 400.0, "depth": 60.0, "inViewport": true }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/live/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;

        // The 640x360 viewport mirrors the live captures where the chase
        // eye puts the own hull BELOW the rect (screenY 500 > 360): the
        // server projects at the client's viewport, so InViewport:false is
        // consistent with this rect and the marker clamps to the bottom edge.
        await viewModel.RefreshOverlayFrameAsync(640, 360);

        OwnMarkerItem marker = viewModel.OwnMarkers.Single();
        Assert.AreEqual(358.9, marker.ScreenX, 1e-9);
        Assert.AreEqual(360.0 - OwnMarkerMath.Margin, marker.ScreenY, 1e-9);
        Assert.AreEqual(Math.PI / 2.0, marker.AngleRadians, 1e-9);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_LiveModeOmitsOwnMarker_WhenOwnTankInViewport()
    {
        // The own tank on-screen needs no marker (the player sees the hull).
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 1.9, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "ownEntityId": 3760577,
              "tanks": [
                { "entityId": 3760577, "playerName": "mrkool1138", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 2.3, "worldX": 0.0, "worldZ": 0.0, "screenX": 358.9, "screenY": 250.0, "depth": 0.5, "inViewport": true }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/live/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.IsLiveMode = true;

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.HasCount(0, viewModel.OwnMarkers);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_ReplayFrameHasNoOwnMarker()
    {
        // Without an OwnEntityId (replay path) the marker is never guessed.
        string frameJson = """
            {
              "replayTimeSeconds": 150.5,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 3760577, "playerName": "mrkool1138", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 2.3, "worldX": 0.0, "worldZ": 0.0, "screenX": 358.9, "screenY": 500.0, "depth": 0.5, "inViewport": false }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.HasCount(0, viewModel.OwnMarkers);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_SortsNameplatesFarToNear()
    {
        // Depth = distance along the view axis; larger = farther from the
        // camera. WPF draws later children on top, so the collection must be
        // far-to-near: a nearer tank's nameplate wins when two overlap.
        string frameJson = """
            {
              "replayTimeSeconds": 100.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.0, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 10, "playerName": "Near", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 15.0, "screenX": 100.0, "screenY": 100.0, "depth": 10.0, "inViewport": true },
                { "entityId": 30, "playerName": "Far", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 200.0, "screenX": 200.0, "screenY": 200.0, "depth": 100.0, "inViewport": true },
                { "entityId": 20, "playerName": "Mid", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 90.0, "screenX": 300.0, "screenY": 300.0, "depth": 50.0, "inViewport": true },
                { "entityId": 40, "playerName": "Unknown", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 60.0, "screenX": 400.0, "screenY": 400.0, "depth": null, "inViewport": true }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual(4, viewModel.Nameplates.Count);
        // Far (100) → mid (50) → near (10), unknown depth last (never hidden).
        CollectionAssert.AreEqual(new long[] { 30, 20, 10, 40 },
            viewModel.Nameplates.Select(item => item.EntityId).ToArray());
        Assert.AreEqual(100.0, viewModel.Nameplates[0].Depth, 1e-9);
        Assert.AreEqual(10.0, viewModel.Nameplates[2].Depth, 1e-9);
        Assert.AreEqual(double.MaxValue, viewModel.Nameplates[3].Depth);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesGodViewMinimapFromBoundary()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 1, "playerName": "Self", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 0.0, "worldX": -100.0, "worldZ": -100.0, "screenX": 960.0, "screenY": 540.0, "depth": 1.0, "inViewport": true },
                { "entityId": 2, "playerName": "Alpha", "tankName": "TankA", "clanTag": null, "teamNumber": 2, "hpFraction": 0.5, "alive": true, "distanceMeters": 120.0, "worldX": 0.0, "worldZ": 0.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true },
                { "entityId": 5, "playerName": "Wreck", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 0.0, "alive": false, "distanceMeters": 90.0, "worldX": 100.0, "worldZ": 100.0, "screenX": null, "screenY": null, "depth": null, "inViewport": false }
              ],
              "beacons": [
                { "name": "Flag", "color": "#FFD700", "distanceMeters": 50.0, "worldX": 50.0, "worldZ": -50.0, "screenX": null, "screenY": null, "depth": null, "inViewport": false }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.EndsWith("/maps/boundaries", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse("""
                    [ { "mapId": "test-map", "minX": -100.0, "maxX": 100.0, "minZ": -100.0, "maxZ": 100.0 } ]
                    """));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","mapId":"test-map","mapName":"Test Map"},"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", "test-map", Now, 1, 2);

        // The boundary fetch is fire-and-forget; wait for it to land so the
        // minimap normalization has a real extent to map against.
        for (int i = 0; i < 100 && viewModel.WorldMinX == 0; i++)
        {
            await Task.Delay(10);
        }

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.IsTrue(viewModel.WorldMinX == -100.0, "Boundary should be applied before the minimap builds.");
        // God-view: all three tanks appear regardless of viewport or distance,
        // normalized across the [-100,100] x [-100,100] boundary.
        Assert.HasCount(3, viewModel.MinimapItems);
        MinimapItem self = viewModel.MinimapItems.Single(item => item.EntityId == 1);
        Assert.AreEqual(0.0, self.NormalizedX, 1e-9);
        Assert.AreEqual(0.0, self.NormalizedZ, 1e-9);
        MinimapItem alpha = viewModel.MinimapItems.Single(item => item.EntityId == 2);
        Assert.AreEqual(0.5, alpha.NormalizedX, 1e-9);
        Assert.AreEqual(0.5, alpha.NormalizedZ, 1e-9);
        Assert.IsTrue(alpha.Alive);
        Assert.AreEqual(2, alpha.TeamNumber);
        MinimapItem wreck = viewModel.MinimapItems.Single(item => item.EntityId == 5);
        Assert.AreEqual(1.0, wreck.NormalizedX, 1e-9);
        Assert.AreEqual(1.0, wreck.NormalizedZ, 1e-9);
        Assert.IsFalse(wreck.Alive);
        // The beacon normalizes onto the same boundary even though it is
        // behind the camera (god-view): world (50, -50) -> (0.75, 0.25).
        MinimapBeaconItem flag = viewModel.MinimapBeacons.Single();
        Assert.AreEqual("Flag", flag.Name);
        Assert.AreEqual("#FFD700", flag.Color);
        Assert.AreEqual(0.75, flag.NormalizedX, 1e-9);
        Assert.AreEqual(0.25, flag.NormalizedZ, 1e-9);
        // The camera ring is drawn at the normalized position (panel center
        // for raw world 0,0), not at raw meters scaled by pixels.
        Assert.AreEqual(0.5, viewModel.MinimapCameraX!.Value, 1e-9);
        Assert.AreEqual(0.5, viewModel.MinimapCameraZ!.Value, 1e-9);
        // The camera facing flows through for the minimap direction tick.
        Assert.AreEqual(0.5, viewModel.MinimapCameraYawRadians!.Value, 1e-9);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesKillFeedNewestFirst()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 2, "playerName": "Alpha", "tankName": "TankA", "clanTag": null, "teamNumber": 2, "hpFraction": 1.0, "alive": true, "distanceMeters": 120.0, "worldX": 0.0, "worldZ": 0.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true },
                { "entityId": 3, "playerName": "Bravo", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 0.0, "alive": false, "distanceMeters": 90.0, "worldX": 10.0, "worldZ": 10.0, "screenX": 700.0, "screenY": 350.0, "depth": 60.0, "inViewport": true }
              ],
              "kills": [
                { "victimEntityId": 3, "killerEntityId": 2, "replayTimeSeconds": 100.0 },
                { "victimEntityId": 4, "killerEntityId": null, "replayTimeSeconds": 150.0 }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // Newest first; names resolved from the frame's tanks; the
        // environmental kill (no attacker in roster) falls back to "—".
        Assert.HasCount(2, viewModel.KillFeed);
        KillItem newest = viewModel.KillFeed[0];
        Assert.AreEqual(4, newest.VictimEntityId);
        Assert.AreEqual("—", newest.KillerLabel);
        Assert.AreEqual("Tank 4", newest.VictimLabel);
        Assert.AreEqual(150.0, newest.ReplayTimeSeconds, 1e-9);
        KillItem older = viewModel.KillFeed[1];
        Assert.AreEqual(3, older.VictimEntityId);
        Assert.AreEqual("Bravo", older.VictimLabel);
        Assert.AreEqual("Alpha", older.KillerLabel);
        Assert.AreEqual(100.0, older.ReplayTimeSeconds, 1e-9);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesScoreboardSortedByDamage()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 2, "playerName": "Alpha", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 0.5, "alive": true, "distanceMeters": 120.0, "worldX": 0.0, "worldZ": 0.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true, "damageDealt": 1200, "damageTaken": 500, "kills": 2 },
                { "entityId": 3, "playerName": "Bravo", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 0.0, "alive": false, "distanceMeters": 90.0, "worldX": 10.0, "worldZ": 10.0, "screenX": 700.0, "screenY": 350.0, "depth": 60.0, "inViewport": true, "damageDealt": 2500, "damageTaken": 900, "kills": 3 },
                { "entityId": 4, "playerName": "Charlie", "tankName": null, "clanTag": null, "teamNumber": 1, "hpFraction": 1.0, "alive": true, "distanceMeters": 300.0, "worldX": 50.0, "worldZ": 0.0, "screenX": 500.0, "screenY": 300.0, "depth": 200.0, "inViewport": true, "damageDealt": 800, "damageTaken": 100, "kills": 0 }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // Sorted by damage dealt desc; names resolved; dead tanks listed greyed.
        Assert.HasCount(3, viewModel.Scoreboard);
        ScoreboardItem top = viewModel.Scoreboard[0];
        Assert.AreEqual(3, top.EntityId);
        Assert.AreEqual("Bravo", top.PlayerName);
        Assert.AreEqual(2500, top.DamageDealt);
        Assert.AreEqual(900, top.DamageTaken);
        Assert.AreEqual(3, top.Kills);
        Assert.IsFalse(top.Alive);
        ScoreboardItem second = viewModel.Scoreboard[1];
        Assert.AreEqual("Alpha", second.PlayerName);
        Assert.AreEqual(1200, second.DamageDealt);
        Assert.AreEqual(500, second.DamageTaken);
        Assert.AreEqual(2, second.Kills);
        ScoreboardItem last = viewModel.Scoreboard[2];
        Assert.AreEqual("Charlie", last.PlayerName);
        Assert.AreEqual(800, last.DamageDealt);
        Assert.AreEqual(100, last.DamageTaken);
        Assert.AreEqual(0, last.Kills);

        // The same totals ride onto in-viewport nameplates for the totals line.
        NameplateItem alpha = viewModel.Nameplates.Single(plate => plate.EntityId == 2);
        Assert.AreEqual(1200, alpha.DamageDealt);
        Assert.AreEqual(2, alpha.Kills);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesEventPips()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 2, "playerName": "Alpha", "tankName": null, "clanTag": null, "teamNumber": 2, "hpFraction": 0.5, "alive": true, "distanceMeters": 120.0, "screenX": 800.0, "screenY": 400.0, "depth": 80.0, "inViewport": true }
              ],
              "beacons": [],
              "pips": [
                { "entityId": 2, "kind": "Damage", "damage": 60, "screenX": 800.0, "screenY": 400.0 },
                { "entityId": 2, "kind": "Destroyed", "damage": 0, "screenX": 800.0, "screenY": 400.0 }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.HasCount(2, viewModel.Pips);
        PipItem damage = viewModel.Pips.Single(pip => pip.Kind == "Damage");
        Assert.AreEqual(2, damage.EntityId);
        Assert.AreEqual(60, damage.Damage);
        Assert.AreEqual(800.0, damage.ScreenX, 1e-9);
        Assert.IsTrue(viewModel.Pips.Any(pip => pip.Kind == "Destroyed"));
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_PopulatesVisibleBeaconsAndSkipsHiddenOnes()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 200.0,
              "cameraX": 0.0, "cameraY": 0.0, "cameraZ": 0.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [],
              "beacons": [
                { "name": "Flag", "color": "#FFD700", "distanceMeters": 100.0, "screenX": 960.0, "screenY": 540.0, "depth": 90.0, "inViewport": true },
                { "name": "Rear", "color": "#FF0000", "distanceMeters": 50.0, "screenX": null, "screenY": null, "depth": null, "inViewport": false },
                { "name": "Off", "color": "#00FF00", "distanceMeters": 200.0, "screenX": 5000.0, "screenY": 5000.0, "depth": 10.0, "inViewport": false }
              ]
            }
            """;
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/frame", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(frameJson));
            }

            if (path.Contains(BattleSessionId.ToString("D"), StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"session":null,"participants":[],"positions":[],"events":[]}"""));
            }

            return Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}"""));
        });
        MainViewModel viewModel = CreateViewModel(handler);

        await viewModel.RefreshSessionsAsync();
        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        // Behind-camera and off-viewport beacons are never drawn.
        Assert.AreEqual(1, viewModel.Beacons.Count);
        BeaconItem flag = viewModel.Beacons.Single(beacon => beacon.Name == "Flag");
        Assert.AreEqual("#FFD700", flag.Color);
        Assert.AreEqual(960.0, flag.ScreenX, 1e-9);
        Assert.AreEqual(540.0, flag.ScreenY, 1e-9);
        Assert.AreEqual(100.0, flag.DistanceMeters, 1e-9);
    }

    [TestMethod]
    public async Task RefreshOverlayFrameAsync_NoSessionSelected_LeavesNameplatesEmpty()
    {
        WriteRendezvousRecord(Now.AddMinutes(-1), Now.AddMinutes(5));
        FakeHttpMessageHandler handler = new((_, _) =>
            Task.FromResult(JsonResponse("""{"offset":0,"limit":200,"count":0,"items":[]}""")));
        MainViewModel viewModel = CreateViewModel(handler);
        await viewModel.RefreshSessionsAsync();

        await viewModel.RefreshOverlayFrameAsync(1920, 1080);

        Assert.AreEqual(0, viewModel.Nameplates.Count);
    }

    private MainViewModel CreateViewModel(
        FakeHttpMessageHandler? handler = null,
        MockTelemetryStreamService? streamService = null,
        TimeProvider? timeProvider = null)
    {
        RendezvousLocator locator = new(new FakeTimeProvider(Now), _rendezvousPath, isProcessAlive: _ => true);
        Func<Uri, string?, TreaderApiClient> factory = handler is not null
            ? (baseUri, capability) => new TreaderApiClient(baseUri, handler, capability)
            : FailFactory;
        return new MainViewModel(
            locator,
            factory,
            streamService,
            timeProvider: timeProvider);
    }

    private static TreaderApiClient FailFactory(Uri baseUri, string? capability) =>
        throw new InvalidOperationException("The API client factory must not be invoked without a usable rendezvous record.");

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class MockTelemetryStreamService : ITelemetryStreamService
    {
        public int ConnectCallCount { get; private set; }
        public Uri? LastConnectedUri { get; private set; }
        public event EventHandler? SessionListChanged;
        public event EventHandler<GameMemoryResponse>? MemoryObservationReceived;

        public Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            return ConnectAsync(baseUri, capability: null, cancellationToken);
        }

        public Task ConnectAsync(Uri baseUri, string? capability, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectedUri = baseUri;
            return Task.CompletedTask;
        }

        public void RaiseSessionListChanged()
        {
            SessionListChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseMemoryObservation(GameMemoryResponse observation)
        {
            MemoryObservationReceived?.Invoke(this, observation);
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
