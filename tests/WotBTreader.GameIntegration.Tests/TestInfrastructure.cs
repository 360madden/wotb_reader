using System.Buffers.Binary;
using System.Globalization;
using K4os.Compression.LZ4;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Dvpl;

namespace WotBTreader.GameIntegration.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WotBTreader.GameIntegration.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] segments)
    {
        string path = segments.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPath(params string[] segments) =>
        segments.Aggregate(Path, System.IO.Path.Combine);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class DvplTestData
{
    public static void Write(
        string path,
        ReadOnlySpan<byte> payload,
        DvplCompressionMode mode = DvplCompressionMode.None,
        uint? crcOverride = null,
        uint? originalSizeOverride = null,
        uint? storedSizeOverride = null,
        uint? modeOverride = null,
        ReadOnlySpan<byte> magic = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        byte[] stored;
        if (mode == DvplCompressionMode.None)
        {
            stored = payload.ToArray();
        }
        else
        {
            byte[] buffer = new byte[LZ4Codec.MaximumOutputSize(payload.Length)];
            LZ4Level level = mode == DvplCompressionMode.Lz4HighCompression
                ? LZ4Level.L12_MAX
                : LZ4Level.L00_FAST;
            int encoded = LZ4Codec.Encode(payload, buffer, level);
            Assert.IsGreaterThan(0, encoded);
            stored = buffer[..encoded];
        }

        byte[] file = new byte[stored.Length + 20];
        stored.CopyTo(file, 0);
        Span<byte> footer = file.AsSpan(stored.Length, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[0..4],
            originalSizeOverride ?? checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[4..8],
            storedSizeOverride ?? checked((uint)stored.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[8..12],
            crcOverride ?? Crc32.Compute(stored));
        BinaryPrimitives.WriteUInt32LittleEndian(
            footer[12..16],
            modeOverride ?? (uint)mode);
        (magic.IsEmpty ? "DVPL"u8 : magic).CopyTo(footer[16..20]);
        File.WriteAllBytes(path, file);
    }

    public static ContentHash HashOf(byte value) =>
        new(
            string.Concat(
                Enumerable.Repeat(
                    value.ToString("x2", CultureInfo.InvariantCulture),
                    ContentHash.Sha256HexLength / 2)));
}
