using System.IO;
using System.Text.Json;
using WotBTreader.Overlay.Discovery;
using WotBTreader.Overlay.Logging;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class HudLoggingTests
{
    private string _directory = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "wotb-overlay-log-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public void Logger_WritesStructuredRecordsAndRedactsSensitiveValues()
    {
        using JsonLineHudLogger logger = CreateLogger();

        logger.Information(
            "hud.synthetic.event",
            ("capability", "synthetic-token"),
            ("sessionId", "synthetic-session"),
            ("count", 3));
        logger.Failure(
            "hud.synthetic.failure",
            new InvalidOperationException("synthetic-secret"));
        logger.Dispose();

        string[] lines = ReadLines();
        Assert.AreEqual(2, lines.Length);
        Assert.IsTrue(lines[0].Contains("\"event\":\"hud.synthetic.event\"", StringComparison.Ordinal));
        Assert.IsTrue(lines[0].Contains("\"component\":\"overlay\"", StringComparison.Ordinal));
        Assert.IsFalse(string.Join(Environment.NewLine, lines).Contains("synthetic-token", StringComparison.Ordinal));
        Assert.IsFalse(string.Join(Environment.NewLine, lines).Contains("synthetic-session", StringComparison.Ordinal));
        Assert.IsFalse(string.Join(Environment.NewLine, lines).Contains("synthetic-secret", StringComparison.Ordinal));

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement properties = document.RootElement.GetProperty("properties");
        Assert.AreEqual("[REDACTED]", properties.GetProperty("capability").GetString());
        Assert.AreEqual("[REDACTED]", properties.GetProperty("sessionId").GetString());
        Assert.AreEqual(3, properties.GetProperty("count").GetInt32());

        using JsonDocument failure = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(
            nameof(InvalidOperationException),
            failure.RootElement.GetProperty("exceptionType").GetString());
    }

    [TestMethod]
    public async Task ViewModel_LogsSafeRendezvousFailure()
    {
        RecordingHudLogger logger = new();
        string missingRecord = Path.Combine(_directory, "missing.json");
        MainViewModel viewModel = new(
            new RendezvousLocator(rendezvousPath: missingRecord),
            static (_, _) => throw new InvalidOperationException("not used"),
            logger: logger);

        await viewModel.RefreshSessionsAsync();

        Assert.AreEqual("Waiting for host…", viewModel.Status);
        CollectionAssert.Contains(logger.Events, "hud.host.rendezvous_unavailable");
    }

    [TestMethod]
    public void Logger_RateLimitsHighVolumeEventsAndReportsSuppressedCount()
    {
        TestTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        using JsonLineHudLogger logger = CreateLogger(timeProvider);

        logger.Information("hud.frame.loaded", ("frameNumber", 1));
        logger.Information("hud.frame.loaded", ("frameNumber", 2));
        logger.Information("hud.frame.loaded", ("frameNumber", 3));
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        logger.Information("hud.frame.loaded", ("frameNumber", 4));
        logger.Dispose();

        string[] lines = ReadLines();
        Assert.AreEqual(2, lines.Length);

        using JsonDocument document = JsonDocument.Parse(lines[1]);
        JsonElement properties = document.RootElement.GetProperty("properties");
        Assert.AreEqual(4, properties.GetProperty("frameNumber").GetInt32());
        Assert.AreEqual(2, properties.GetProperty("suppressedCount").GetInt32());
    }

    [TestMethod]
    public void Logger_RateLimitsMissingGameWindowDiagnostics()
    {
        TestTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        using JsonLineHudLogger logger = CreateLogger(timeProvider);

        logger.Warning("hud.game_window.not_found");
        logger.Warning("hud.game_window.not_found");
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        logger.Warning("hud.game_window.not_found");
        logger.Dispose();

        string[] lines = ReadLines();
        Assert.AreEqual(2, lines.Length);
        using JsonDocument document = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(
            1,
            document.RootElement.GetProperty("properties").GetProperty("suppressedCount").GetInt32());
    }

    private JsonLineHudLogger CreateLogger(TestTimeProvider? timeProvider = null) =>
        new(new HudLoggerOptions
        {
            DirectoryPath = _directory,
            FileSizeLimitBytes = 4096,
            RetainedFileCount = 3,
            HighVolumeMinimumInterval = TimeSpan.FromSeconds(5),
            TimeProvider = timeProvider ?? TimeProvider.System,
        });

    private string[] ReadLines() =>
        Directory.GetFiles(_directory, "hud-*.jsonl")
            .SelectMany(File.ReadLines)
            .ToArray();

    private sealed class RecordingHudLogger : IHudLogger
    {
        public List<string> Events { get; } = [];

        public void Debug(string eventName, params (string Key, object? Value)[] properties) =>
            Events.Add(eventName);

        public void Information(string eventName, params (string Key, object? Value)[] properties) =>
            Events.Add(eventName);

        public void Warning(string eventName, params (string Key, object? Value)[] properties) =>
            Events.Add(eventName);

        public void Failure(
            string eventName,
            Exception? exception = null,
            params (string Key, object? Value)[] properties) =>
            Events.Add(eventName);
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan amount) => _current += amount;
    }
}
