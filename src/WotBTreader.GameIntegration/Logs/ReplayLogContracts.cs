using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Logs;

/// <summary>Strict native-log markers that are safe to expose outside the parser.</summary>
public enum ReplayLogMarkerKind
{
    ReplayRecordingStarted,
    ReplayRecordingStopped,
    OfflineReplayStarted,
    OfflineReplayStopped,
}

/// <summary>A recognized native marker with optional source wall-clock evidence.</summary>
public sealed record ParsedReplayLogMarker(
    ReplayLogMarkerKind Kind,
    DateTimeOffset? SourceTimestampUtc)
{
    /// <summary>
    /// Gets whether the marker is positive evidence that the client entered local replay playback.
    /// Recording markers never satisfy this safety gate.
    /// </summary>
    public bool IsPositiveOfflineReplayEvidence =>
        Kind == ReplayLogMarkerKind.OfflineReplayStarted;
}

/// <summary>A sequenced, privacy-safe marker emitted from a tailed native log.</summary>
public sealed record ReplayLogEvent(
    long Sequence,
    ReplayLogMarkerKind Kind,
    DateTimeOffset? SourceTimestampUtc,
    DateTimeOffset ObservedAtUtc,
    ContentHash OpaqueSourceId,
    long ByteOffset)
{
    /// <summary>Gets whether this event positively establishes local replay playback.</summary>
    public bool IsPositiveOfflineReplayEvidence =>
        Kind == ReplayLogMarkerKind.OfflineReplayStarted;
}

/// <summary>Recognizes an allowlist of replay lifecycle markers without retaining raw log text.</summary>
public interface IBlitzReplayLifecycleParser
{
    /// <summary>Attempts to reduce one bounded log line to a known lifecycle marker.</summary>
    bool TryParse(string line, out ParsedReplayLogMarker? marker);
}

/// <summary>
/// Tails native Blitz logs using watcher hints plus periodic reconciliation.
/// Implementations expose no raw lines, file names, or full paths.
/// </summary>
public interface IBlitzReplayLogMonitor
{
    /// <summary>Streams recognized replay lifecycle events until cancelled.</summary>
    IAsyncEnumerable<ReplayLogEvent> WatchAsync(CancellationToken cancellationToken);
}
