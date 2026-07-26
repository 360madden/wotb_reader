using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Dvpl;

/// <summary>Compression modes observed in the 20-byte DAVA DVPL footer.</summary>
public enum DvplCompressionMode : uint
{
    None = 0,
    Lz4 = 1,
    Lz4HighCompression = 2,
}

/// <summary>Validated footer metadata for one DVPL resource.</summary>
public sealed record DvplFooter(
    int OriginalSize,
    int StoredSize,
    uint StoredPayloadCrc32,
    DvplCompressionMode CompressionMode);

/// <summary>A bounded, checksum-validated DVPL payload and its immutable source hashes.</summary>
public sealed record DvplPayload(
    ReadOnlyMemory<byte> Data,
    DvplFooter Footer,
    ContentHash SourceHash,
    ContentHash PayloadHash);

/// <summary>Reads DVPL resources as data without extracting them to the filesystem.</summary>
public interface IDvplReader
{
    /// <summary>Reads and validates one DVPL resource under configured allocation limits.</summary>
    ValueTask<OperationResult<DvplPayload>> ReadAsync(
        string path,
        CancellationToken cancellationToken);
}
