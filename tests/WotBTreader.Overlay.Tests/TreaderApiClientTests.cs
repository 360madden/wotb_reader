using System.Net;
using System.Net.Http;
using System.Text.Json;
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
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
                Content = new StringContent("{\"success\":true,\"message\":\"launch.accepted\"}"),
            };
        }
    }
}
