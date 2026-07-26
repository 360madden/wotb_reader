using System.Text;
using System.Text.Json;

namespace WotBTreader.GameHarness;

/// <summary>
/// Hash-only screenshot evidence retained in the append-only action ledger.
/// </summary>
public sealed record ScreenshotAuditEvidence(
    string FileName,
    string PngSha256,
    int PixelWidth,
    int PixelHeight,
    string Backend,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Append-only audit entry for a capture or game-affecting operation. It
/// deliberately excludes full paths, raw log lines, player data, and pixels.
/// </summary>
public sealed record HarnessActionAuditRecord(
    string SchemaVersion,
    Guid RecordId,
    DateTimeOffset TimestampUtc,
    Guid CorrelationId,
    string Command,
    int? ProcessId,
    string? ExecutableSha256,
    string? ReplaySha256,
    long? WindowHandle,
    int? DpiX,
    int? DpiY,
    string? LogWatermark,
    long TimeoutMilliseconds,
    string? ExpectedState,
    string ValidationCode,
    bool Succeeded,
    string? ResultCode,
    ScreenshotAuditEvidence? Before,
    ScreenshotAuditEvidence? After);

/// <summary>
/// Receives immutable action audit entries.
/// </summary>
public interface IHarnessAuditSink
{
    ValueTask AppendAsync(
        HarnessActionAuditRecord record,
        CancellationToken cancellationToken);
}

/// <summary>
/// Appends one compact JSON object per line and flushes each action before
/// returning. A process-local gate prevents interleaved JSON records.
/// </summary>
public sealed class JsonLinesHarnessAuditSink : IHarnessAuditSink, IAsyncDisposable
{
    private readonly string _ledgerPath;
    private readonly SemaphoreSlim _appendGate = new(1, 1);

    public JsonLinesHarnessAuditSink(string ledgerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        _ledgerPath = Path.GetFullPath(ledgerPath);
    }

    public async ValueTask AppendAsync(
        HarnessActionAuditRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = Path.GetDirectoryName(_ledgerPath)
            ?? throw new InvalidOperationException("The ledger path has no parent directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(record, HarnessJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                _ledgerPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _appendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class ScreenshotAuditEvidenceExtensions
{
    public static ScreenshotAuditEvidence ToAuditEvidence(this WindowCaptureResult result) =>
        new(
            Path.GetFileName(result.FileName),
            result.PngSha256,
            result.PixelWidth,
            result.PixelHeight,
            result.Backend,
            result.CapturedAtUtc);
}
