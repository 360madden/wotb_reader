using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Capture;

public sealed record TelemetryReadOptions(
    int MaximumLineBytes,
    long MaximumEventCount,
    TimeSpan MaximumDuration)
{
    public static TelemetryReadOptions Default { get; } = new(
        MaximumLineBytes: 1024 * 1024,
        MaximumEventCount: 5_000_000,
        MaximumDuration: TimeSpan.FromMinutes(2));
}

public sealed record ComparisonOptions(
    TimeSpan TimestampWindow,
    IReadOnlyDictionary<string, double> FieldTolerances)
{
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
