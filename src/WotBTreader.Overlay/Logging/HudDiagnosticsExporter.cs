using System.IO;
using System.Text.Json;

namespace WotBTreader.Overlay.Logging;

/// <summary>Privacy-safe snapshot of the current HUD presentation state.</summary>
internal sealed record HudDiagnosticsSnapshot(
    string HudUiVersion,
    string RuntimeState,
    string RuntimeStateDetail,
    string FrameStatus,
    string GameWindowStatus,
    string FrameHealth,
    string RenderHealth,
    string Mode,
    int SessionCount,
    bool HasSelectedSession);

/// <summary>Bounded result returned after writing a diagnostics bundle.</summary>
internal sealed record HudDiagnosticsExportResult(
    int LogFileCount,
    int LogRecordCount,
    int EventTypeCount);

/// <summary>
/// Writes a portable diagnostics JSON document. Raw log lines, paths, replay
/// data, identifiers, URLs, and credentials are deliberately excluded; only
/// event names, levels, timestamps, counts, and the safe HUD snapshot remain.
/// </summary>
internal static class HudDiagnosticsExporter
{
    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private const int MaximumFiles = 14;
    private const int MaximumRecordsPerFile = 10_000;
    private const int MaximumEventTypes = 128;

    public static HudDiagnosticsExportResult Export(
        string destinationPath,
        HudDiagnosticsSnapshot snapshot,
        string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A destination is required.", nameof(destinationPath));
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("A log directory is required.", nameof(logDirectory));
        }

        LogSummary summary = SummarizeLogs(logDirectory);
        Dictionary<string, object?> document = new(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "hud-diagnostics/1",
            ["generatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["component"] = "overlay",
            ["hud"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["uiVersion"] = SafeText(snapshot.HudUiVersion),
                ["runtimeState"] = SafeText(snapshot.RuntimeState),
                ["runtimeDetail"] = SafeText(snapshot.RuntimeStateDetail),
                ["frameStatus"] = SafeText(snapshot.FrameStatus),
                ["gameWindowStatus"] = SafeText(snapshot.GameWindowStatus),
                ["frameHealth"] = SafeText(snapshot.FrameHealth),
                ["renderHealth"] = SafeText(snapshot.RenderHealth),
                ["mode"] = snapshot.Mode is "live" ? "live" : "replay",
                ["sessionCount"] = Math.Max(0, snapshot.SessionCount),
                ["hasSelectedSession"] = snapshot.HasSelectedSession,
            },
            ["logs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["filesSummarized"] = summary.FileCount,
                ["recordsSummarized"] = summary.RecordCount,
                ["events"] = summary.Events
                    .OrderBy(item => item.Event, StringComparer.Ordinal)
                    .Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["event"] = item.Event,
                        ["level"] = item.Level,
                        ["count"] = item.Count,
                        ["firstTimestampUtc"] = item.FirstTimestampUtc,
                        ["lastTimestampUtc"] = item.LastTimestampUtc,
                    })
                    .ToArray(),
            },
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(destinationPath, JsonSerializer.Serialize(document, ExportOptions));
        return new HudDiagnosticsExportResult(
            summary.FileCount,
            summary.RecordCount,
            summary.Events.Count);
    }

    private static LogSummary SummarizeLogs(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            return new LogSummary(0, 0, []);
        }

        Dictionary<string, EventSummary> byEvent = new(StringComparer.Ordinal);
        int fileCount = 0;
        int recordCount = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(logDirectory, "hud-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MaximumFiles)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LogSummary(0, 0, []);
        }

        foreach (string file in files)
        {
            fileCount++;
            int recordsInFile = 0;
            try
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (recordsInFile++ >= MaximumRecordsPerFile)
                    {
                        break;
                    }

                    if (!TryReadSafeEvent(line, out string eventName, out string level, out string timestampUtc))
                    {
                        continue;
                    }

                    recordCount++;
                    if (byEvent.Count >= MaximumEventTypes
                        && !byEvent.ContainsKey(eventName))
                    {
                        continue;
                    }

                    if (!byEvent.TryGetValue(eventName, out EventSummary? summary))
                    {
                        summary = new EventSummary(eventName, level, 0, timestampUtc, timestampUtc);
                        byEvent.Add(eventName, summary);
                    }

                    summary.Count++;
                    summary.LastTimestampUtc = timestampUtc;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A partially readable local log must not prevent exporting the
                // safe runtime snapshot or the other readable log files.
            }
        }

        return new LogSummary(fileCount, recordCount, byEvent.Values.ToArray());
    }

    private static bool TryReadSafeEvent(
        string line,
        out string eventName,
        out string level,
        out string timestampUtc)
    {
        eventName = string.Empty;
        level = string.Empty;
        timestampUtc = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("event", out JsonElement eventElement)
                || eventElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? candidate = eventElement.GetString();
            if (candidate is null
                || candidate.Length > 96
                || candidate.Any(character =>
                    !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
            {
                return false;
            }

            eventName = candidate;
            level = root.TryGetProperty("level", out JsonElement levelElement)
                && levelElement.ValueKind == JsonValueKind.String
                ? SafeText(levelElement.GetString())
                : "Unknown";
            timestampUtc = root.TryGetProperty("timestampUtc", out JsonElement timestampElement)
                && timestampElement.ValueKind == JsonValueKind.String
                ? SafeText(timestampElement.GetString())
                : string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SafeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value
                .Where(character => !char.IsControl(character))
                .Take(192)
                .ToArray());

    private sealed class EventSummary(
        string eventName,
        string level,
        int count,
        string firstTimestampUtc,
        string lastTimestampUtc)
    {
        public string Event { get; } = eventName;

        public string Level { get; } = level;

        public int Count { get; set; } = count;

        public string FirstTimestampUtc { get; } = firstTimestampUtc;

        public string LastTimestampUtc { get; set; } = lastTimestampUtc;
    }

    private sealed record LogSummary(
        int FileCount,
        int RecordCount,
        IReadOnlyList<EventSummary> Events);
}
