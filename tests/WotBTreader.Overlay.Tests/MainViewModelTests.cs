using System.IO;
using System.Net;
using System.Net.Http;
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
    public async Task RefreshSessionsAsync_MissingRendezvous_ReportsWaitingWithNoSessions()
    {
        MainViewModel viewModel = CreateViewModel();

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.Status));
        Assert.IsTrue(
            viewModel.Status.Contains("wait", StringComparison.OrdinalIgnoreCase),
            "Status should mention waiting for the rendezvous record.");
    }

    [TestMethod]
    public async Task RefreshSessionsAsync_StaleRendezvous_ReportsStatusWithNoSessions()
    {
        WriteRendezvousRecord(Now.AddMinutes(-10), Now.AddMinutes(-5));
        MainViewModel viewModel = CreateViewModel();

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual(0, viewModel.Sessions.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(viewModel.Status));
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
        Assert.AreEqual("1 session(s)", viewModel.Status);
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
                "duration": "0:00:00",
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

        // Populate _client via a refresh so the SelectedSession load has a client to use
        await viewModel.RefreshSessionsAsync();

        viewModel.SelectedSession = new SessionRow(
            BattleSessionId, "Test Map", null, Now, 1, 2);

        // Poll until the fire-and-forget detail load completes.
        await WaitForConditionAsync(() => viewModel.Points.Count > 0, TimeSpan.FromSeconds(2));

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

    private MainViewModel CreateViewModel(FakeHttpMessageHandler? handler = null, MockTelemetryStreamService? streamService = null)
    {
        RendezvousLocator locator = new(new FakeTimeProvider(Now), _rendezvousPath);
        Func<Uri, TreaderApiClient> factory = handler is not null
            ? baseUri => new TreaderApiClient(baseUri, handler)
            : FailFactory;
        return new MainViewModel(locator, factory, streamService);
    }

    private static TreaderApiClient FailFactory(Uri baseUri) =>
        throw new InvalidOperationException("The API client factory must not be invoked without a usable rendezvous record.");

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MockTelemetryStreamService : ITelemetryStreamService
    {
        public int ConnectCallCount { get; private set; }
        public Uri? LastConnectedUri { get; private set; }
        public event EventHandler? SessionListChanged;

        public Task ConnectAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            LastConnectedUri = baseUri;
            return Task.CompletedTask;
        }

        public void RaiseSessionListChanged()
        {
            SessionListChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
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
