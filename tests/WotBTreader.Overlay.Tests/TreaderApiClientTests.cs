using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotBTreader.ApiContracts;
using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class TreaderApiClientTests
{
    [TestMethod]
    public void Ctor_NonLoopbackIp_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TreaderApiClient(new Uri("http://192.168.1.5")));
    }

    [TestMethod]
    public void Ctor_ExternalHost_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TreaderApiClient(new Uri("http://example.com")));
    }

    [TestMethod]
    public void Ctor_LoopbackHosts_AreAccepted()
    {
        using TreaderApiClient ipv4 = new(new Uri("http://127.0.0.1:8123"));
        using TreaderApiClient localhost = new(new Uri("http://localhost:8123"));
        using TreaderApiClient ipv6 = new(new Uri("http://[::1]:8123"));

        Assert.IsNotNull(ipv4);
        Assert.IsNotNull(localhost);
        Assert.IsNotNull(ipv6);
    }

    [TestMethod]
    public async Task LaunchGameAsync_SendsTheManagedSourceArtifactId()
    {
        CapturingHandler handler = new();
        using var client = new TreaderApiClient(new Uri("http://127.0.0.1:8123"), handler);

        await client.LaunchGameAsync("aa10bb20-cc30-dd40-ee50-ff60aa70bb80");

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/api/v1/game/launch", handler.Path);
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        Assert.AreEqual(
            "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
            document.RootElement.GetProperty("sourceArtifactId").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("replayPath", out _));
        Assert.IsFalse(
            handler.Headers.ContainsKey("X-WotBTreader-Capability"),
            "Capability header must not be sent when no capability is provided.");
    }

    [TestMethod]
    public async Task LaunchGameAsync_WithCapability_SendsCapabilityHeader()
    {
        CapturingHandler handler = new();
        using var client = new TreaderApiClient(
            new Uri("http://127.0.0.1:8123"), handler, capability: "cap-test-token");

        await client.LaunchGameAsync("aa10bb20-cc30-dd40-ee50-ff60aa70bb80");

        Assert.IsTrue(handler.Headers.TryGetValue("X-WotBTreader-Capability", out string? headerValue));
        Assert.AreEqual("cap-test-token", headerValue);
    }

    [TestMethod]
    public async Task GetOverlayFrameAsync_BuildsUrlAndDeserializes()
    {
        string frameJson = """
            {
              "replayTimeSeconds": 42.5,
              "cameraX": 1.0, "cameraY": 2.0, "cameraZ": 3.0,
              "cameraYawRadians": 0.5, "cameraPitchRadians": 0.0,
              "tanks": [
                { "entityId": 7, "playerName": "Alpha", "tankName": "TankA", "clanTag": null, "teamNumber": 2, "hpFraction": 0.6, "alive": true, "distanceMeters": 100.0, "screenX": 800.0, "screenY": 400.0, "depth": 90.0, "inViewport": true }
              ],
              "beacons": [
                { "name": "Flag", "color": "#FFD700", "distanceMeters": 100.0, "screenX": 900.0, "screenY": 450.0, "depth": 80.0, "inViewport": true }
              ]
            }
            """;
        CapturingHandler handler = new(frameJson);
        using var client = new TreaderApiClient(new Uri("http://127.0.0.1:8123"), handler);

        OverlayFrameResponse? frame = await client.GetOverlayFrameAsync(
            Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            replayTimeSeconds: 42.5,
            verticalFovDegrees: 75,
            viewportWidth: 1280,
            viewportHeight: 720);

        Assert.IsNotNull(frame);
        Assert.AreEqual(42.5, frame.ReplayTimeSeconds, 1e-9);
        Assert.AreEqual(0.5, frame.CameraYawRadians!.Value, 1e-9);
        Assert.HasCount(1, frame.Tanks);
        Assert.AreEqual(7, frame.Tanks[0].EntityId);
        Assert.AreEqual(800.0, frame.Tanks[0].ScreenX!.Value, 1e-9);
        Assert.IsTrue(frame.Tanks[0].InViewport);
        Assert.HasCount(1, frame.Beacons);
        Assert.AreEqual("Flag", frame.Beacons[0].Name);
        Assert.AreEqual("#FFD700", frame.Beacons[0].Color);
        Assert.AreEqual(900.0, frame.Beacons[0].ScreenX!.Value, 1e-9);
        Assert.AreEqual(
            "/api/v1/sessions/3fa85f64-5717-4562-b3fc-2c963f66afa6/frame",
            handler.Path);
        Assert.IsTrue(handler.PathAndQuery!.Contains("timeSeconds=42.5", StringComparison.Ordinal));
        Assert.IsTrue(handler.PathAndQuery.Contains("fov=75", StringComparison.Ordinal));
        Assert.IsTrue(handler.PathAndQuery.Contains("width=1280", StringComparison.Ordinal));
        Assert.IsTrue(handler.PathAndQuery.Contains("height=720", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetOverlayFrameAsync_WithShell_AppendsShellQuery()
    {
        CapturingHandler handler = new("{}");
        using var client = new TreaderApiClient(new Uri("http://127.0.0.1:8123"), handler);

        await client.GetOverlayFrameAsync(
            Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            replayTimeSeconds: 10,
            shell: "_2pdr_HE_Mk.1");

        Assert.IsTrue(
            handler.PathAndQuery!.Contains("shell=_2pdr_HE_Mk.1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetOverlayFrameAsync_WithoutShell_NoShellQuery()
    {
        CapturingHandler handler = new("{}");
        using var client = new TreaderApiClient(new Uri("http://127.0.0.1:8123"), handler);

        await client.GetOverlayFrameAsync(
            Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"),
            replayTimeSeconds: 10);

        Assert.IsFalse(handler.PathAndQuery!.Contains("shell=", StringComparison.Ordinal));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string? _responseJson;

        public CapturingHandler(string? responseJson = null)
        {
            _responseJson = responseJson;
        }

        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? PathAndQuery { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers.Clear();
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(", ", header.Value);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                // A benign default so callers that deserialize the response
                // (e.g. LaunchGameAsync) never see a null/empty body.
                Content = new StringContent(_responseJson ?? "{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
