using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Replay;

/// <summary>
/// Security and resource limits applied to every decoder invocation.
/// Prevents decompression bombs, archive bombs, runaway decoding, and
/// excessive unknown-field accumulation.
/// </summary>
/// <param name="MaximumArchiveBytes">Maximum total compressed archive size.</param>
/// <param name="MaximumArchiveEntries">Maximum number of entries in the archive.</param>
/// <param name="MaximumEntryBytes">Maximum size of a single archive entry.</param>
/// <param name="MaximumExpandedBytes">Maximum total decompressed size across all entries.</param>
/// <param name="MaximumCompressionRatio">Maximum allowed compression ratio (expanded/compressed).</param>
/// <param name="MaximumPacketCount">Maximum decoded packets before truncation.</param>
/// <param name="MaximumPacketBytes">Maximum bytes per individual packet.</param>
/// <param name="MaximumUnknownFields">Maximum unrecognised protobuf fields before abort.</param>
/// <param name="MaximumNestingDepth">Maximum protobuf/pickle nesting depth.</param>
/// <param name="MaximumResynchronizationBytes">Maximum bytes to scan for resynchronization.</param>
/// <param name="MaximumDecodeDuration">Maximum wall-clock time for a single decode.</param>
public sealed record DecoderLimits(
    long MaximumArchiveBytes,
    int MaximumArchiveEntries,
    long MaximumEntryBytes,
    long MaximumExpandedBytes,
    double MaximumCompressionRatio,
    int MaximumPacketCount,
    int MaximumPacketBytes,
    int MaximumUnknownFields,
    int MaximumNestingDepth,
    int MaximumResynchronizationBytes,
    TimeSpan MaximumDecodeDuration)
{
    /// <summary>Sensible defaults for production use on typical replay files.</summary>
    public static DecoderLimits Default { get; } = new(
        MaximumArchiveBytes: 128 * 1024 * 1024,
        MaximumArchiveEntries: 32,
        MaximumEntryBytes: 64 * 1024 * 1024,
        MaximumExpandedBytes: 256 * 1024 * 1024,
        MaximumCompressionRatio: 250,
        MaximumPacketCount: 2_000_000,
        MaximumPacketBytes: 4 * 1024 * 1024,
        MaximumUnknownFields: 1_000_000,
        MaximumNestingDepth: 64,
        MaximumResynchronizationBytes: 1024 * 1024,
        MaximumDecodeDuration: TimeSpan.FromMinutes(2));
}

public sealed record ReplayInput(
    SourceArtifact Artifact,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync);

public sealed record ReplayProbeResult(
    bool IsReplay,
    string? GameVersion,
    string? FormatVersion,
    IReadOnlyList<string> ArchiveEntries,
    ReplayCapability ObservableCapabilities,
    IReadOnlyList<string> Warnings);

public sealed record DecoderDescriptor(
    string Id,
    string Version,
    string SchemaVersion,
    IReadOnlySet<string> SupportedGameVersions);

public sealed record ReplayDecodeRequest(
    ReplayInput Input,
    DecodeRunId DecodeRunId,
    ReplayProbeResult Probe,
    DecoderLimits Limits);

/// <summary>Performs a bounded, non-mutating identification pass over a replay source.</summary>
public interface IReplayProbe
{
    ValueTask<OperationResult<ReplayProbeResult>> ProbeAsync(
        ReplayInput input,
        DecoderLimits limits,
        CancellationToken cancellationToken);
}

/// <summary>Decodes one explicitly supported replay version without executing embedded data.</summary>
public interface IReplayDecoder
{
    DecoderDescriptor Descriptor { get; }

    bool CanDecode(ReplayProbeResult probe);

    ValueTask<OperationResult<ReplayDecodeProjection>> DecodeAsync(
        ReplayDecodeRequest request,
        CancellationToken cancellationToken);
}
