using System.Buffers.Binary;
using System.Security.Cryptography;
using K4os.Compression.LZ4;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Dvpl;

/// <summary>
/// Reads the DAVA DVPL envelope. CRC is checked over the stored bytes before any
/// decompression so corrupt compressed input never reaches the LZ4 decoder.
/// </summary>
public sealed class DvplReader : IDvplReader
{
    private const int FooterLength = 20;
    private static ReadOnlySpan<byte> FooterMagic => "DVPL"u8;

    private readonly GameIntegrationOptions _options;

    /// <summary>Creates a bounded DVPL reader.</summary>
    public DvplReader(GameIntegrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<DvplPayload>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("game.dvpl.invalid_path", "The DVPL path is required.");
        }

        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long initialLength = stream.Length;
            if (initialLength < FooterLength)
            {
                return Failure("game.dvpl.truncated", "The DVPL footer is truncated.");
            }

            if (initialLength > (long)_options.MaxDvplStoredBytes + FooterLength)
            {
                return Failure("game.dvpl.stored_limit", "The DVPL stored-size limit was exceeded.");
            }

            int actualStoredSize = checked((int)(initialLength - FooterLength));
            byte[] storedBytes = GC.AllocateUninitializedArray<byte>(actualStoredSize);
            await stream.ReadExactlyAsync(storedBytes, cancellationToken).ConfigureAwait(false);

            byte[] footerBytes = new byte[FooterLength];
            await stream.ReadExactlyAsync(footerBytes, cancellationToken).ConfigureAwait(false);

            if (stream.Length != initialLength)
            {
                return Failure(
                    "game.dvpl.changed_during_read",
                    "The DVPL resource changed while it was being read.",
                    retryable: true);
            }

            OperationResult<DvplFooter> footerResult = ParseFooter(footerBytes, actualStoredSize);
            if (!footerResult.IsSuccess)
            {
                return OperationResult.Failure<DvplPayload>(footerResult.Error!);
            }

            DvplFooter footer = footerResult.Value!;
            uint actualCrc = Crc32.Compute(storedBytes);
            if (actualCrc != footer.StoredPayloadCrc32)
            {
                return Failure("game.dvpl.crc_mismatch", "The DVPL stored-payload checksum is invalid.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            OperationResult<byte[]> decodeResult = Decode(storedBytes, footer);
            if (!decodeResult.IsSuccess)
            {
                return OperationResult.Failure<DvplPayload>(decodeResult.Error!);
            }

            byte[] output = decodeResult.Value!;
            using IncrementalHash sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sourceHasher.AppendData(storedBytes);
            sourceHasher.AppendData(footerBytes);
            ContentHash sourceHash = new(Convert.ToHexString(sourceHasher.GetHashAndReset()));
            ContentHash payloadHash = new(Convert.ToHexString(SHA256.HashData(output)));

            return OperationResult.Success(
                new DvplPayload(output, footer, sourceHash, payloadHash));
        }
        catch (FileNotFoundException)
        {
            return Failure("game.dvpl.not_found", "The DVPL resource was not found.");
        }
        catch (DirectoryNotFoundException)
        {
            return Failure("game.dvpl.not_found", "The DVPL resource directory was not found.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("game.dvpl.access_denied", "The DVPL resource could not be read.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure("game.dvpl.invalid_path", "The DVPL path is invalid.");
        }
        catch (IOException)
        {
            return Failure(
                "game.dvpl.io_failure",
                "The DVPL resource could not be read consistently.",
                retryable: true);
        }
        catch (OverflowException)
        {
            return Failure("game.dvpl.size_overflow", "The DVPL size fields are outside supported limits.");
        }
    }

    private OperationResult<DvplFooter> ParseFooter(
        ReadOnlySpan<byte> footerBytes,
        int actualStoredSize)
    {
        if (!footerBytes[16..20].SequenceEqual(FooterMagic))
        {
            return OperationResult.Failure<DvplFooter>(
                new ApplicationError("game.dvpl.invalid_magic", "The DVPL footer magic is invalid."));
        }

        uint originalSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(footerBytes[0..4]);
        uint storedSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(footerBytes[4..8]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(footerBytes[8..12]);
        uint modeRaw = BinaryPrimitives.ReadUInt32LittleEndian(footerBytes[12..16]);

        if (originalSizeRaw > _options.MaxDvplOutputBytes)
        {
            return OperationResult.Failure<DvplFooter>(
                new ApplicationError(
                    "game.dvpl.output_limit",
                    "The DVPL decompressed-size limit was exceeded."));
        }

        if (storedSizeRaw > _options.MaxDvplStoredBytes ||
            storedSizeRaw != (uint)actualStoredSize)
        {
            return OperationResult.Failure<DvplFooter>(
                new ApplicationError(
                    "game.dvpl.size_mismatch",
                    "The DVPL stored-size field does not match the resource length."));
        }

        if (!Enum.IsDefined(typeof(DvplCompressionMode), modeRaw))
        {
            return OperationResult.Failure<DvplFooter>(
                new ApplicationError(
                    "game.dvpl.unsupported_compression",
                    "The DVPL compression mode is unsupported."));
        }

        DvplCompressionMode mode = (DvplCompressionMode)modeRaw;
        if (mode == DvplCompressionMode.None && originalSizeRaw != storedSizeRaw)
        {
            return OperationResult.Failure<DvplFooter>(
                new ApplicationError(
                    "game.dvpl.size_mismatch",
                    "An uncompressed DVPL must have matching stored and original sizes."));
        }

        return OperationResult.Success(
            new DvplFooter(checked((int)originalSizeRaw), actualStoredSize, crc, mode));
    }

    private static OperationResult<byte[]> Decode(byte[] storedBytes, DvplFooter footer)
    {
        if (footer.CompressionMode == DvplCompressionMode.None)
        {
            return OperationResult.Success(storedBytes);
        }

        try
        {
            byte[] output = GC.AllocateUninitializedArray<byte>(footer.OriginalSize);
            int decoded = LZ4Codec.Decode(
                storedBytes,
                0,
                storedBytes.Length,
                output,
                0,
                output.Length);

            if (decoded != footer.OriginalSize)
            {
                return OperationResult.Failure<byte[]>(
                    new ApplicationError(
                        "game.dvpl.lz4_invalid",
                        "The DVPL LZ4 payload did not decode to the declared size."));
            }

            return OperationResult.Success(output);
        }
        catch (ArgumentException)
        {
            return OperationResult.Failure<byte[]>(
                new ApplicationError("game.dvpl.lz4_invalid", "The DVPL LZ4 payload is invalid."));
        }
    }

    private static OperationResult<DvplPayload> Failure(
        string code,
        string message,
        bool retryable = false) =>
        OperationResult.Failure<DvplPayload>(new ApplicationError(code, message, retryable));
}
