using System.Net;
using System.Text;
using System.Text.Json;

namespace WotBTreader.GameHarness.Tests;

[TestClass]
public sealed class OffsetCampaignTests
{
    [TestMethod]
    public void DefaultsFitInsideTheAuthorizationWaitBudget()
    {
        bool parsed = OffsetCampaignOptions.TryParse(
            ["discover-campaign"],
            out OffsetCampaignOptions? options,
            out string? error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(options);
        Assert.AreEqual(2, options.Comparisons);
        Assert.AreEqual(2, options.IntervalSeconds);
        Assert.AreEqual(16, options.SpanMiB);
        Assert.AreEqual(-500, options.FloatMin);
        Assert.AreEqual(500, options.FloatMax);
        Assert.AreEqual("changed", options.CompareMode);
    }

    [TestMethod]
    public void ParserAcceptsAnExplicitByteBudget()
    {
        bool parsed = OffsetCampaignOptions.TryParse(
            ["campaign", "--max-bytes", "67108864"],
            out OffsetCampaignOptions? options,
            out string? error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(options);
        Assert.AreEqual(64L * 1024 * 1024, options.MaxBytes);
    }

    [TestMethod]
    public void ParserRejectsNegativeOrOverCeilingByteBudget()
    {
        Assert.IsFalse(OffsetCampaignOptions.TryParse(
            ["campaign", "--max-bytes", "-1"],
            out _,
            out string? negativeError));
        StringAssert.Contains(negativeError, "byte budget");

        Assert.IsFalse(OffsetCampaignOptions.TryParse(
            ["campaign", "--max-bytes", "536870913"],
            out _,
            out string? ceilingError));
        StringAssert.Contains(ceilingError, "byte budget");
    }

    [TestMethod]
    public void ParserRejectsAnOverlongOrInvertedCampaign()
    {
        Assert.IsFalse(OffsetCampaignOptions.TryParse(
            ["campaign", "--comparisons", "4", "--interval-seconds", "3"],
            out _,
            out string? waitError));
        StringAssert.Contains(waitError, "at most 8 total wait seconds");

        Assert.IsFalse(OffsetCampaignOptions.TryParse(
            ["campaign", "--float-min", "2", "--float-max", "1"],
            out _,
            out string? boundsError));
        StringAssert.Contains(boundsError, "ordered finite float bounds");
    }

    [TestMethod]
    public async Task RunnerSuppressesCandidateDataAndAlwaysDiscardsTheSession()
    {
        var handler = new CampaignHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:9182/"),
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var delays = new List<TimeSpan>();
        var runner = new OffsetCampaignRunner(
            client,
            output,
            error,
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        int exitCode = await runner.RunAsync(new OffsetCampaignOptions(
            Comparisons: 2,
            IntervalSeconds: 2,
            SpanMiB: 16,
            FloatMin: -500,
            FloatMax: 500,
            CompareMode: "changed"));

        Assert.AreEqual(0, exitCode, error.ToString());
        Assert.HasCount(2, delays);
        Assert.IsTrue(handler.Discarded);
        Assert.AreEqual(64L, handler.NeighborhoodBody.GetProperty("referenceOffset").GetInt64());
        Assert.HasCount(2, handler.CompareBodies);
        Assert.IsTrue(handler.SnapshotBody.TryGetProperty("maxBytes", out JsonElement budget)
            && budget.GetInt64() == 0);
        Assert.IsTrue(handler.CompareBodies.All(body =>
            body.GetProperty("rollingBaseline").GetBoolean()
            && body.GetProperty("maxCandidates").GetInt32() == 1));

        string rendered = output + error.ToString();
        StringAssert.Contains(rendered, "aggregate variability evidence only");
        StringAssert.Contains(rendered, "Scanner session discarded.");
        Assert.IsFalse(rendered.Contains(CampaignHandler.SessionId, StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("0xDEADBEEF", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("PRIVATE-VALUE-BYTES", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RunnerAttemptsDiscardWhenAComparisonFails()
    {
        var handler = new CampaignHandler(failComparison: true);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:9182/"),
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new OffsetCampaignRunner(
            client,
            output,
            error,
            (_, _) => Task.CompletedTask);

        int exitCode = await runner.RunAsync(new OffsetCampaignOptions(
            Comparisons: 1,
            IntervalSeconds: 1,
            SpanMiB: 1,
            FloatMin: -1,
            FloatMax: 1,
            CompareMode: "changed"));

        Assert.AreEqual((int)HarnessExitCode.ConflictOrBusy, exitCode);
        Assert.IsTrue(handler.Discarded);
        Assert.IsFalse((output + error.ToString()).Contains(
            CampaignHandler.SessionId,
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RunnerForwardsTheConfiguredByteBudget()
    {
        var handler = new CampaignHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:9182/"),
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new OffsetCampaignRunner(
            client,
            output,
            error,
            (_, _) => Task.CompletedTask);

        int exitCode = await runner.RunAsync(new OffsetCampaignOptions(
            Comparisons: 1,
            IntervalSeconds: 1,
            SpanMiB: 16,
            FloatMin: -500,
            FloatMax: 500,
            CompareMode: "changed",
            MaxBytes: 32L * 1024 * 1024));

        Assert.AreEqual(0, exitCode, error.ToString());
        Assert.IsTrue(handler.SnapshotBody.TryGetProperty("maxBytes", out JsonElement budget)
            && budget.GetInt64() == 32L * 1024 * 1024);
        Assert.IsTrue(handler.Discarded);
    }

    private sealed class CampaignHandler(bool failComparison = false) : HttpMessageHandler
    {
        public const string SessionId = "PRIVATE-SCANNER-SESSION";

        public bool Discarded { get; private set; }

        public JsonElement NeighborhoodBody { get; private set; }

        public JsonElement SnapshotBody { get; private set; }

        public List<JsonElement> CompareBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post
                && path.EndsWith("/discover/neighborhood", StringComparison.Ordinal))
            {
                using JsonDocument body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                NeighborhoodBody = body.RootElement.Clone();
                return Json(HttpStatusCode.OK, new
                {
                    baseAddress = "0x10000000",
                    moduleSize = 32 * 1024 * 1024,
                    candidates = new[]
                    {
                        new
                        {
                            absoluteAddress = "0xDEADBEEF",
                            observedValueHex = "PRIVATE-VALUE-BYTES",
                        },
                    },
                });
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/discover/snapshot", StringComparison.Ordinal))
            {
                using JsonDocument body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                SnapshotBody = body.RootElement.Clone();
                return Json(HttpStatusCode.OK, new { sessionId = SessionId });
            }

            if (request.Method == HttpMethod.Post
                && path.Contains("/discover/compare/", StringComparison.Ordinal))
            {
                using JsonDocument body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                CompareBodies.Add(body.RootElement.Clone());
                if (failComparison)
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "synthetic" });
                }

                return Json(HttpStatusCode.OK, new
                {
                    previousCount = 100,
                    currentCount = 25,
                    changedCount = 25,
                    unchangedCount = 75,
                    increasedCount = 14,
                    decreasedCount = 11,
                    truncated = true,
                    candidates = new[]
                    {
                        new
                        {
                            absoluteAddress = "0xDEADBEEF",
                            observedValueHex = "PRIVATE-VALUE-BYTES",
                        },
                    },
                });
            }

            if (request.Method == HttpMethod.Delete
                && path.Contains("/discover/session/", StringComparison.Ordinal))
            {
                Discarded = true;
                return Json(HttpStatusCode.OK, new { discarded = SessionId });
            }

            return Json(HttpStatusCode.NotFound, new { error = "unexpected" });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
