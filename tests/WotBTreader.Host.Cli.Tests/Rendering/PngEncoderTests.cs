using System.IO.Compression;
using System.Text;
using WotBTreader.Host.Cli.Rendering;

namespace WotBTreader.Host.Cli.Tests.Rendering;

[TestClass]
public sealed class PngEncoderTests
{
    private static readonly string[] ExpectedChunkTypes = ["IHDR", "IDAT", "IEND"];

    [TestMethod]
    public void Encode_ProducesStructurallyValidPng()
    {
        // Two red pixels in a 2x1 image.
        byte[] png = PngEncoder.Encode(2, 1, [255, 0, 0, 255, 255, 0, 0, 255]);

        // 1. Signature.
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png.Take(8).ToArray());

        // 2. Chunk sequence IHDR → IDAT → IEND, every CRC valid, no trailing
        //    bytes.
        int offset = 8;
        var chunks = new List<(string Type, byte[] Data)>();
        while (offset < png.Length)
        {
            uint length = ReadBe(png, offset);
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png.Skip(offset + 8).Take((int)length).ToArray();
            uint storedCrc = ReadBe(png, offset + 8 + (int)length);
            Assert.AreEqual(Crc32(type, data), storedCrc, $"CRC mismatch for {type} chunk.");
            chunks.Add((type, data));
            offset += 12 + (int)length;
        }

        Assert.AreEqual(png.Length, offset, "Unexpected trailing bytes after IEND.");
        CollectionAssert.AreEqual(ExpectedChunkTypes,
            chunks.Select(chunk => chunk.Type).ToArray());

        // 3. IHDR: 2x1 RGBA8, no interlace.
        byte[] ihdr = chunks[0].Data;
        Assert.HasCount(13, ihdr);
        Assert.AreEqual(2u, ReadBe(ihdr, 0));
        Assert.AreEqual(1u, ReadBe(ihdr, 4));
        Assert.AreEqual(8, ihdr[8]);
        Assert.AreEqual(6, ihdr[9]);
        Assert.AreEqual(0, ihdr[12]);

        // 4. IDAT: zlib stream (0x78 0x9C) whose deflate payload decompresses
        //    to the scanline data (filter byte 0 + 8 RGBA bytes), adler32
        //    trailer matching.
        byte[] idat = chunks[1].Data;
        Assert.AreEqual(0x78, idat[0]);
        Assert.AreEqual(0x9C, idat[1]);
        using var input = new MemoryStream(idat, 2, idat.Length - 6);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        byte[] raw = output.ToArray();
        CollectionAssert.AreEqual(new byte[] { 0, 255, 0, 0, 255, 255, 0, 0, 255 }, raw);
        Assert.AreEqual(Adler32(raw), ReadBe(idat, idat.Length - 4));
    }

    [TestMethod]
    public void Encode_IsDeterministic()
    {
        byte[] rgba = new byte[32 * 16 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251);
            rgba[i + 1] = 10;
            rgba[i + 2] = 200;
            rgba[i + 3] = 255;
        }

        byte[] first = PngEncoder.Encode(32, 16, rgba);
        byte[] second = PngEncoder.Encode(32, 16, rgba);
        CollectionAssert.AreEqual(first, second);
    }

    [TestMethod]
    public void Encode_RejectsInvalidDimensionsAndBuffer()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PngEncoder.Encode(0, 1, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PngEncoder.Encode(1, -1, []));
        Assert.ThrowsExactly<ArgumentException>(
            () => PngEncoder.Encode(2, 2, new byte[2 * 2 * 4 - 1]));
    }

    private static uint ReadBe(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24)
        | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8)
        | buffer[offset + 3];

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

    private static uint Crc32(string type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in Encoding.ASCII.GetBytes(type))
        {
            crc = Update(crc, value);
        }

        foreach (byte value in data)
        {
            crc = Update(crc, value);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        return crc;
    }
}
