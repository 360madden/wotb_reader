using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WotBTreader.Overlay.Logging;

/// <summary>Severity written by the standalone WPF HUD logger.</summary>
public enum HudLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>
/// Small logging seam owned by the overlay. The implementation deliberately
/// accepts structured values rather than exception messages or formatted text.
/// </summary>
public interface IHudLogger
{
    void Debug(string eventName, params (string Key, object? Value)[] properties);

    void Information(string eventName, params (string Key, object? Value)[] properties);

    void Warning(string eventName, params (string Key, object? Value)[] properties);

    void Failure(
        string eventName,
        Exception? exception = null,
        params (string Key, object? Value)[] properties);
}

/// <summary>Configuration for the HUD's local JSON-lines log sink.</summary>
internal sealed class HudLoggerOptions
{
    public required string DirectoryPath { get; init; }

    public string FilePrefix { get; init; } = "hud-";

    public long FileSizeLimitBytes { get; init; } = 20 * 1024 * 1024;

    public int RetainedFileCount { get; init; } = 14;

    public HudLogLevel MinimumLevel { get; init; } = HudLogLevel.Information;

    public TimeSpan HighVolumeMinimumInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Creates the production HUD logger without adding a project reference to Bootstrap.</summary>
public static class HudLoggerFactory
{
    public static IHudLogger CreateDefault()
    {
        return new JsonLineHudLogger(new HudLoggerOptions
        {
            DirectoryPath = GetDefaultLogDirectory(),
        });
    }

    internal static string GetDefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WotBTreader",
        "logs");
}

/// <summary>No-op logger used by unit tests and headless view-model callers.</summary>
internal sealed class NullHudLogger : IHudLogger
{
    public static NullHudLogger Instance { get; } = new();

    private NullHudLogger()
    {
    }

    public void Debug(string eventName, params (string Key, object? Value)[] properties)
    {
    }

    public void Information(string eventName, params (string Key, object? Value)[] properties)
    {
    }

    public void Warning(string eventName, params (string Key, object? Value)[] properties)
    {
    }

    public void Failure(
        string eventName,
        Exception? exception = null,
        params (string Key, object? Value)[] properties)
    {
    }
}

/// <summary>
/// Synchronous, bounded JSON-lines sink for the standalone overlay process.
/// Logging failures are fail-open: a broken log directory must never stop the
/// HUD. Exception messages, stack traces, paths, tokens, IDs, and arbitrary
/// object graphs are never written.
/// </summary>
internal sealed class JsonLineHudLogger : IHudLogger, IDisposable
{
    private const string Redacted = "[REDACTED]";
    private const int MaximumEventNameLength = 96;
    private const int MaximumPropertyKeyLength = 64;
    private const int MaximumStringValueLength = 192;

    private readonly object _gate = new();
    private readonly HudLoggerOptions _options;
    private readonly Dictionary<string, RateState> _rateStates = new(StringComparer.Ordinal);
    private StreamWriter? _writer;
    private string? _activePath;
    private DateOnly _activeDate;
    private long _bytesWritten;
    private bool _disabled;
    private bool _disposed;

    public JsonLineHudLogger(HudLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            throw new ArgumentException("A log directory is required.", nameof(options));
        }

        if (options.FileSizeLimitBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The HUD log file-size limit must be at least 1024 bytes.");
        }

        if (options.RetainedFileCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The HUD log must retain at least one file.");
        }

        if (options.HighVolumeMinimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The high-volume interval cannot be negative.");
        }

        try
        {
            Directory.CreateDirectory(options.DirectoryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DisableLogging("initialization", exception);
        }
    }

    public void Debug(string eventName, params (string Key, object? Value)[] properties) =>
        Write(HudLogLevel.Debug, eventName, properties, exception: null);

    public void Information(string eventName, params (string Key, object? Value)[] properties) =>
        Write(HudLogLevel.Information, eventName, properties, exception: null);

    public void Warning(string eventName, params (string Key, object? Value)[] properties) =>
        Write(HudLogLevel.Warning, eventName, properties, exception: null);

    public void Failure(
        string eventName,
        Exception? exception = null,
        params (string Key, object? Value)[] properties) =>
        Write(HudLogLevel.Error, eventName, properties, exception);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWriter();
        }

        GC.SuppressFinalize(this);
    }

    private void Write(
        HudLogLevel level,
        string eventName,
        (string Key, object? Value)[] properties,
        Exception? exception)
    {
        if (level < _options.MinimumLevel || string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        string safeEventName = SanitizeEventName(eventName);
        lock (_gate)
        {
            if (_disposed || _disabled)
            {
                return;
            }

            DateTimeOffset timestamp = _options.TimeProvider.GetUtcNow();
            if (!TryAdmit(safeEventName, timestamp, out int suppressedCount))
            {
                return;
            }

            Dictionary<string, object?> safeProperties = SanitizeProperties(properties);
            if (suppressedCount > 0)
            {
                safeProperties["suppressedCount"] = suppressedCount;
            }

            Dictionary<string, object?> record = new(StringComparer.Ordinal)
            {
                ["timestampUtc"] = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["level"] = level.ToString(),
                ["event"] = safeEventName,
                ["component"] = "overlay",
                ["properties"] = safeProperties,
            };
            if (exception is not null)
            {
                record["exceptionType"] = exception.GetType().Name;
            }

            try
            {
                EnsureWriter(timestamp);
                string line = JsonSerializer.Serialize(record);
                _writer!.WriteLine(line);
                _writer.Flush();
                _bytesWritten += Encoding.UTF8.GetByteCount(line)
                    + Encoding.UTF8.GetByteCount(Environment.NewLine);
                EnforceRetention();
            }
            catch (Exception writeException) when (
                writeException is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                DisposeWriter();
                DisableLogging("write", writeException);
            }
        }
    }

    private bool TryAdmit(string eventName, DateTimeOffset timestamp, out int suppressedCount)
    {
        suppressedCount = 0;
        if (!IsHighVolumeEvent(eventName) || _options.HighVolumeMinimumInterval == TimeSpan.Zero)
        {
            return true;
        }

        if (!_rateStates.TryGetValue(eventName, out RateState? state))
        {
            _rateStates[eventName] = new RateState(timestamp);
            return true;
        }

        if (timestamp - state.LastWritten < _options.HighVolumeMinimumInterval)
        {
            state.SuppressedCount++;
            return false;
        }

        suppressedCount = state.SuppressedCount;
        state.LastWritten = timestamp;
        state.SuppressedCount = 0;
        return true;
    }

    private void EnsureWriter(DateTimeOffset timestamp)
    {
        DateOnly date = DateOnly.FromDateTime(timestamp.UtcDateTime);
        if (_writer is not null
            && _activeDate == date
            && _bytesWritten < _options.FileSizeLimitBytes)
        {
            return;
        }

        DisposeWriter();
        int sequence = 0;
        string path;
        do
        {
            string suffix = sequence == 0 ? string.Empty : $"-{sequence:D3}";
            path = Path.Combine(
                _options.DirectoryPath,
                $"{_options.FilePrefix}{date:yyyyMMdd}{suffix}.jsonl");
            sequence++;
        }
        while (File.Exists(path) && new FileInfo(path).Length >= _options.FileSizeLimitBytes);

        _activePath = path;
        _activeDate = date;
        _bytesWritten = File.Exists(path) ? new FileInfo(path).Length : 0;
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void EnforceRetention()
    {
        string[] files = Directory.GetFiles(
            _options.DirectoryPath,
            $"{_options.FilePrefix}*.jsonl");
        if (files.Length <= _options.RetainedFileCount)
        {
            return;
        }

        int filesToDelete = files.Length - _options.RetainedFileCount;
        foreach (string file in files.OrderBy(File.GetLastWriteTimeUtc))
        {
            if (filesToDelete == 0)
            {
                break;
            }

            if (string.Equals(file, _activePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                filesToDelete--;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retention is best effort and must not take down the HUD.
                System.Diagnostics.Debug.WriteLine($"[HUD] log retention failed: {exception.GetType().Name}");
            }
        }
    }

    private void DisableLogging(string operation, Exception exception)
    {
        _disabled = true;
        System.Diagnostics.Debug.WriteLine($"[HUD] log {operation} failed: {exception.GetType().Name}");
    }

    private void DisposeWriter()
    {
        _writer?.Dispose();
        _writer = null;
        _activePath = null;
        _bytesWritten = 0;
    }

    private static bool IsHighVolumeEvent(string eventName) =>
        eventName.StartsWith("hud.frame.", StringComparison.Ordinal)
        || eventName.StartsWith("hud.memory.", StringComparison.Ordinal)
        || eventName.StartsWith("hud.sessions.", StringComparison.Ordinal)
        || eventName.StartsWith("hud.stream.", StringComparison.Ordinal)
        || eventName.StartsWith("hud.game_window.", StringComparison.Ordinal);

    private static Dictionary<string, object?> SanitizeProperties(
        (string Key, object? Value)[] properties)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        foreach ((string key, object? value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string safeKey = SanitizeKey(key);
            result[safeKey] = NormalizeValue(safeKey, value);
        }

        return result;
    }

    private static object? NormalizeValue(string key, object? value)
    {
        if (IsSensitiveKey(key))
        {
            return Redacted;
        }

        return value switch
        {
            null => null,
            string text => SanitizeString(text),
            bool boolean => boolean,
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number when float.IsFinite(number) => number,
            double number when double.IsFinite(number) => number,
            decimal number => number,
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.TotalMilliseconds,
            Enum enumValue => SanitizeString(enumValue.ToString()),
            _ => $"type:{value.GetType().Name}",
        };
    }

    private static string SanitizeEventName(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaximumEventNameLength));
        foreach (char character in value)
        {
            if (builder.Length >= MaximumEventNameLength)
            {
                break;
            }

            builder.Append(
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_');
        }

        return builder.ToString();
    }

    private static string SanitizeKey(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaximumPropertyKeyLength));
        foreach (char character in value)
        {
            if (builder.Length >= MaximumPropertyKeyLength)
            {
                break;
            }

            builder.Append(
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_');
        }

        return builder.ToString();
    }

    private static string SanitizeString(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, MaximumStringValueLength));
        foreach (char character in value)
        {
            if (builder.Length >= MaximumStringValueLength)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsSensitiveKey(string key)
    {
        string normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("capability", StringComparison.Ordinal)
            || normalized.Contains("account", StringComparison.Ordinal)
            || normalized.Contains("artifact", StringComparison.Ordinal)
            || normalized.EndsWith("path", StringComparison.Ordinal)
            || normalized.EndsWith("uri", StringComparison.Ordinal)
            || normalized.EndsWith("url", StringComparison.Ordinal)
            || normalized.EndsWith("query", StringComparison.Ordinal)
            || normalized.EndsWith("sessionid", StringComparison.Ordinal);
    }

    private sealed class RateState(DateTimeOffset lastWritten)
    {
        public DateTimeOffset LastWritten { get; set; } = lastWritten;

        public int SuppressedCount { get; set; }
    }
}
