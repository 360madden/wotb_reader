using System.IO.Compression;
using System.Text;

namespace WotBTreader.Host.Cli.Rendering;

/// <summary>
/// Minimal PNG (RGBA8, non-interlaced) encoder: signature, IHDR, a single
/// IDAT (zlib-wrapped deflate), and IEND, each with a correct CRC32 chunk
/// checksum. Pure BCL — no external image library — so the offline CLI can
/// render preview frames on any platform the solution builds for.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes one RGBA image (4 bytes per pixel, row-major, origin
    /// top-left) into PNG bytes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Non-positive dimensions.</exception>
    /// <exception cref="ArgumentException">Buffer length does not match.</exception>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                "RGBA buffer length does not match the image dimensions.", nameof(rgba));
        }

        // One filter byte (0 = None) per scanline, then the row's RGBA bytes.
        byte[] raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int rawStart = y * (1 + width * 4);
            raw[rawStart] = 0;
            rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(rawStart + 1));
        }

        using var stream = new MemoryStream();
        stream.Write(Signature, 0, Signature.Length);
        WriteChunk(stream, "IHDR", BuildIhdr(width, height));
        WriteChunk(stream, "IDAT", ZlibCompress(raw));
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        byte[] data = new byte[13];
        WriteBe(data, 0, (uint)width);
        WriteBe(data, 4, (uint)height);
        data[8] = 8;   // bit depth
        data[9] = 6;   // color type: RGBA
        data[10] = 0;  // compression: deflate
        data[11] = 0;  // filter: adaptive
        data[12] = 0;  // interlace: none
        return data;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var deflated = new MemoryStream();
        using (var deflate = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        byte[] deflateBytes = deflated.ToArray();
        byte[] zlib = new byte[2 + deflateBytes.Length + 4];
        zlib[0] = 0x78; // CMF: deflate, 32K window
        zlib[1] = 0x9C; // FLG: FCHECK for 0x789C (divisible by 31), default level
        deflateBytes.CopyTo(zlib, 2);

        uint adler = Adler32(data);
        zlib[^4] = (byte)(adler >> 24);
        zlib[^3] = (byte)(adler >> 16);
        zlib[^2] = (byte)(adler >> 8);
        zlib[^1] = (byte)adler;
        return zlib;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBe(length, 0, (uint)data.Length);
        stream.Write(length, 0, 4);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);

        byte[] crc = new byte[4];
        WriteBe(crc, 0, Crc32(typeBytes, data));
        stream.Write(crc, 0, 4);
    }

    private static void WriteBe(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        crc = Update(crc, type);
        crc = Update(crc, data);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Update(uint crc, byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }
}
