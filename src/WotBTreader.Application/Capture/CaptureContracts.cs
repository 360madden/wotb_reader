using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Capture;

/// <summary>
/// Bounds and limits for reading telemetry from a capture source.
/// Prevents runaway memory or CPU from maliciously large or infinite streams.
/// </summary>
/// <param name="MaximumLineBytes">Maximum bytes per NDJSON line before truncation.</param>
/// <param name="MaximumEventCount">Maximum number of events to read before stopping.</param>
/// <param name="MaximumDuration">Maximum wall-clock time to spend reading.</param>
public sealed record TelemetryReadOptions(
    int MaximumLineBytes,
    long MaximumEventCount,
    TimeSpan MaximumDuration)
{
    /// <summary>Default limits: 1 MB per line, 5M events, 2-minute timeout.</summary>
    public static TelemetryReadOptions Default { get; } = new(
        MaximumLineBytes: 1024 * 1024,
        MaximumEventCount: 5_000_000,
        MaximumDuration: TimeSpan.FromMinutes(2));
}

/// <summary>
/// Options for comparing two telemetry event sequences.
/// <see cref="TimestampWindow"/> controls how close two events must be in time
/// to be considered the same occurrence.
/// </summary>
/// <param name="TimestampWindow">Maximum time delta for pairing corresponding events.</param>
/// <param name="FieldTolerances">Per-field tolerance values for numeric comparisons.</param>
public sealed record ComparisonOptions(
    TimeSpan TimestampWindow,
    IReadOnlyDictionary<string, double> FieldTolerances)
{
    /// <summary>Default comparison: 250ms timestamp window, no field tolerances.</summary>
    public static ComparisonOptions Default { get; } = new(
        TimeSpan.FromMilliseconds(250),
        new Dictionary<string, double>());
}

/// <summary>Reads versioned telemetry events from a bounded source stream.</summary>
public interface ITelemetrySource
{
    string SourceId { get; }

    IAsyncEnumerable<OperationResult<TelemetryEvent>> ReadAsync(
        Stream source,
        TelemetryReadOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Writes the versioned telemetry-capture format independently from application logs.</summary>
public interface ITelemetryCaptureWriter
{
    ValueTask WriteAsync(
        Stream destination,
        IAsyncEnumerable<TelemetryEvent> events,
        CancellationToken cancellationToken);
}

/// <summary>Normalizes explicitly understood fields while retaining raw provenance.</summary>
public interface ITelemetryNormalizer
{
    OperationResult<TelemetryEvent> Normalize(TelemetryEvent telemetryEvent);
}

/// <summary>Compares two deterministic telemetry sequences without inventing identity matches.</summary>
public interface ITelemetryComparator
{
    ValueTask<OperationResult<TelemetryComparison>> CompareAsync(
        SourceArtifactId leftSourceArtifactId,
        IReadOnlyList<TelemetryEvent> left,
        SourceArtifactId rightSourceArtifactId,
        IReadOnlyList<TelemetryEvent> right,
        ComparisonOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Provides the current estimated replay clock and immutable resynchronization segments.</summary>
public interface IReplayClockSource
{
    ValueTask<OperationResult<ReplayClockSnapshot>> GetSnapshotAsync(
        BattleSessionId battleSessionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<ReplayClockSegment>> AddSegmentAsync(
        ReplayClockSegment segment,
        CancellationToken cancellationToken);

    ValueTask<OperationResult<ReplayClockSnapshot>> MarkStaleAsync(
        BattleSessionId battleSessionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}
