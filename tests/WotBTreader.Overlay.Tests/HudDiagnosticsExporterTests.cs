using System.IO;
using System.Text.Json;
using WotBTreader.Overlay.Logging;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class HudDiagnosticsExporterTests
{
    private string _directory = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "wotb-overlay-diagnostics-tests",
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
    public void Export_SummarizesSafeEventsWithoutCopyingRawProperties()
    {
        string logPath = Path.Combine(_directory, "hud-test.jsonl");
        File.WriteAllLines(
            logPath,
            [
                "{\"timestampUtc\":\"2026-08-15T12:00:00Z\",\"level\":\"Information\",\"event\":\"hud.frame.loaded\",\"properties\":{\"token\":\"secret-token\",\"sessionId\":\"private-session\"}}",
                "{\"timestampUtc\":\"2026-08-15T12:00:01Z\",\"level\":\"Warning\",\"event\":\"hud.game_window.not_found\",\"properties\":{\"path\":\"private-path\"}}",
                "{\"timestampUtc\":\"2026-08-15T12:00:02Z\",\"level\":\"Information\",\"event\":\"hud.frame.loaded\",\"properties\":{\"playerName\":\"public-but-not-needed\"}}",
            ]);
        string destination = Path.Combine(_directory, "export", "diagnostics.json");
        HudDiagnosticsSnapshot snapshot = new(
            "HUD UI v0.5.0-alpha",
            "ReplayPaused",
            "Replay is paused",
            "Frame @ 12.0s",
            "Game window: waiting for World of Tanks Blitz",
            "Frame age: 0.4s · refresh: 12 ms",
            "Render: 2 nameplates · 14 minimap dots · 0 beacons",
            "replay",
            3,
            true);

        HudDiagnosticsExportResult result = HudDiagnosticsExporter.Export(
            destination,
            snapshot,
            _directory);

        Assert.AreEqual(1, result.LogFileCount);
        Assert.AreEqual(3, result.LogRecordCount);
        Assert.AreEqual(2, result.EventTypeCount);
        string json = File.ReadAllText(destination);
        Assert.IsFalse(json.Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-session", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-path", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("public-but-not-needed", StringComparison.Ordinal));

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.AreEqual(
            "hud-diagnostics/1",
            document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(
            "HUD UI v0.5.0-alpha",
            document.RootElement.GetProperty("hud").GetProperty("uiVersion").GetString());
        Assert.AreEqual(
            3,
            document.RootElement.GetProperty("hud").GetProperty("sessionCount").GetInt32());
        Assert.AreEqual(
            2,
            document.RootElement.GetProperty("logs").GetProperty("events").GetArrayLength());
    }

    [TestMethod]
    public void Export_MissingLogDirectoryStillWritesRuntimeSnapshot()
    {
        string destination = Path.Combine(_directory, "diagnostics.json");
        HudDiagnosticsSnapshot snapshot = new(
            "HUD UI v0.5.0-alpha",
            "NoSessions",
            "Import a replay",
            "No frame received",
            "Game window: not found",
            "Frame age: — · refresh: —",
            "Render: waiting for frame",
            "replay",
            0,
            false);

        HudDiagnosticsExportResult result = HudDiagnosticsExporter.Export(
            destination,
            snapshot,
            Path.Combine(_directory, "missing-logs"));

        Assert.AreEqual(0, result.LogFileCount);
        Assert.AreEqual(0, result.LogRecordCount);
        Assert.IsTrue(File.Exists(destination));
        Assert.IsTrue(File.ReadAllText(destination).Contains("NoSessions", StringComparison.Ordinal));
    }
}
