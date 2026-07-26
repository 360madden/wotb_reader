using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WotBTreader.Core;

namespace WotBTreader.Replays;

internal static class ReplayBinary
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ContentHash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    public static string DecodeUtf8(ReadOnlySpan<byte> bytes, string field)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReplayFormatException(
                "replay.invalid_utf8",
                $"Replay field '{field}' is not valid UTF-8.")
            {
                Data = { ["cause"] = exception.GetType().Name },
            };
        }
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        EnsureAvailable(bytes, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    public static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        EnsureAvailable(bytes, offset, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    }

    public static float ReadSingle(ReadOnlySpan<byte> bytes, int offset)
    {
        int bits = ReadInt32(bytes, offset);
        return BitConverter.Int32BitsToSingle(bits);
    }

    public static void EnsureAvailable(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            throw new ReplayFormatException(
                "replay.truncated",
                "Replay binary data ended before the declared value.");
        }
    }
}
