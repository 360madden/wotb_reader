namespace WotBTreader.GameIntegration.Dvpl;

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320U;

    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ Polynomial;
            }

            table[index] = value;
        }

        return table;
    }
}
