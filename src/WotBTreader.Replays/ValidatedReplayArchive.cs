using System.IO.Compression;
using System.Security.Cryptography;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record ValidatedReplayArchive(
    IReadOnlyDictionary<string, byte[]> Entries,
    long ArchiveLength)
{
    public byte[] this[string name] => Entries[name];
}

internal static class ReplayArchiveReader
{
    public static async ValueTask<ValidatedReplayArchive> ReadAsync(
        ReplayInput input,
        DecoderLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        long maximumArchiveBytes = Math.Min(
            limits.MaximumArchiveBytes,
            ReplayFormatConstants.MaximumStrictArchiveBytes);
        if (maximumArchiveBytes <= 0 || maximumArchiveBytes > int.MaxValue)
        {
            throw new ReplayFormatException(
                "replay.invalid_limits",
                "The archive byte limit is outside the supported range.");
        }

        await using Stream source = await input.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        if (!source.CanRead)
        {
            throw new ReplayFormatException(
                "replay.unreadable",
                "The replay source is not readable.");
        }

        byte[] archiveBytes = await ReadBoundedAsync(
            source,
            checked((int)maximumArchiveBytes),
            cancellationToken).ConfigureAwait(false);

        if (archiveBytes.LongLength != input.Artifact.ByteLength)
        {
            throw new ReplayFormatException(
                "replay.artifact_length_mismatch",
                "The replay source length does not match its immutable artifact metadata.");
        }

        byte[] actualHash = SHA256.HashData(archiveBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(input.Artifact.Sha256.Value)))
        {
            throw new ReplayFormatException(
                "replay.artifact_hash_mismatch",
                "The replay source hash does not match its immutable artifact metadata.");
        }

        using MemoryStream memory = new(archiveBytes, writable: false);
        using ZipArchive archive = OpenArchive(memory);
        ValidateEntryTable(archive, limits);

        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long entryLimit = GetEntryLimit(entry.FullName, limits);
            expandedBytes = checked(expandedBytes + entry.Length);
            long maximumExpanded = Math.Min(
                limits.MaximumExpandedBytes,
                ReplayFormatConstants.MaximumStrictExpandedBytes);
            if (expandedBytes > maximumExpanded)
            {
                throw new ReplayFormatException(
                    "replay.expanded_size_limit",
                    "The replay archive exceeds the expanded-data limit.");
            }

            await using Stream entryStream = entry.Open();
            entries.Add(
                entry.FullName,
                await ReadExactEntryAsync(
                    entryStream,
                    entry.Length,
                    entryLimit,
                    cancellationToken).ConfigureAwait(false));
        }

        return new ValidatedReplayArchive(entries, archiveBytes.LongLength);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException exception)
        {
            throw new ReplayFormatException(
                "replay.invalid_zip",
                "The replay is not a valid ZIP archive.")
            {
                Data = { ["cause"] = exception.GetType().Name },
            };
        }
    }

    private static void ValidateEntryTable(ZipArchive archive, DecoderLimits limits)
    {
        int maximumEntries = Math.Min(limits.MaximumArchiveEntries, ReplayFormatConstants.RequiredEntries.Count);
        if (archive.Entries.Count > maximumEntries)
        {
            throw new ReplayFormatException(
                "replay.entry_count_limit",
                "The replay archive has too many entries.");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (string.IsNullOrEmpty(entry.Name) ||
                name.Contains('/', StringComparison.Ordinal) ||
                name.Contains('\\', StringComparison.Ordinal) ||
                name is "." or "..")
            {
                throw new ReplayFormatException(
                    "replay.unsafe_entry_name",
                    "The replay archive contains a directory or unsafe entry name.");
            }

            if (!names.Add(name))
            {
                throw new ReplayFormatException(
                    "replay.duplicate_entry",
                    "The replay archive contains duplicate entry names.");
            }

            if (!ReplayFormatConstants.RequiredEntries.Contains(name))
            {
                throw new ReplayFormatException(
                    "replay.unexpected_entry",
                    "The replay archive contains an unexpected entry.");
            }

            long entryLimit = GetEntryLimit(name, limits);
            if (entry.Length < 0 || entry.Length > entryLimit)
            {
                throw new ReplayFormatException(
                    "replay.entry_size_limit",
                    "A replay archive entry exceeds its byte limit.");
            }

            if (entry.Length > 0)
            {
                double ratio = entry.Length / (double)Math.Max(1, entry.CompressedLength);
                if (!double.IsFinite(ratio) || ratio > limits.MaximumCompressionRatio)
                {
                    throw new ReplayFormatException(
                        "replay.compression_ratio_limit",
                        "A replay archive entry exceeds the compression-ratio limit.");
                }
            }
        }

        if (!ReplayFormatConstants.RequiredEntries.SetEquals(names))
        {
            throw new ReplayFormatException(
                "replay.missing_entry",
                "The replay archive is missing a required entry.");
        }
    }

    private static long GetEntryLimit(string name, DecoderLimits limits)
    {
        long formatLimit = name switch
        {
            ReplayFormatConstants.MetadataEntry => ReplayFormatConstants.MaximumMetadataBytes,
            ReplayFormatConstants.BattleResultsEntry => ReplayFormatConstants.MaximumBattleResultsBytes,
            ReplayFormatConstants.EventStreamEntry => ReplayFormatConstants.MaximumEventStreamBytes,
            _ => 0,
        };

        return Math.Min(limits.MaximumEntryBytes, formatLimit);
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining < 0 || remaining > maximumBytes)
            {
                throw new ReplayFormatException(
                    "replay.archive_size_limit",
                    "The replay archive exceeds the byte limit.");
            }
        }

        using MemoryStream output = new(Math.Min(maximumBytes, 1024 * 1024));
        byte[] buffer = new byte[64 * 1024];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new ReplayFormatException(
                    "replay.archive_size_limit",
                    "The replay archive exceeds the byte limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async ValueTask<byte[]> ReadExactEntryAsync(
        Stream stream,
        long declaredLength,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (declaredLength < 0 || declaredLength > maximumBytes || declaredLength > int.MaxValue)
        {
            throw new ReplayFormatException(
                "replay.entry_size_limit",
                "A replay archive entry exceeds its byte limit.");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)declaredLength));
        int offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new ReplayFormatException(
                        "replay.truncated_entry",
                        "A replay archive entry ended before its declared size.");
                }

                offset += read;
            }

            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new ReplayFormatException(
                    "replay.entry_size_mismatch",
                    "A replay archive entry exceeds its declared size.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new ReplayFormatException(
                "replay.corrupt_entry",
                "A replay archive entry failed integrity validation.")
            {
                Data = { ["cause"] = exception.GetType().Name },
            };
        }

        return bytes;
    }
}
