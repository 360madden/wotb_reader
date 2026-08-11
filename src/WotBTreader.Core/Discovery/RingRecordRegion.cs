using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Decodes a bounded ring-record region dump (the `ring-record` region
/// anchor of the batch read surface) into the fields the live frame needs:
/// the world position float32 triple at <c>+0x10</c> (the published
/// position-chain read) and the hull yaw float32 at <c>+0x2C</c> (the
/// runtime chain field rehearsed 27/27 + 35/35 against packet yaw, pending
/// the live L2 facing session). Pure — no IO, no Win32, no allocation.
/// Fail-closed: a region too short for a field or a non-finite value yields
/// null for that field, never a fabricated number.
/// </summary>
public static class RingRecordRegion
{
    /// <summary>Position float32 triple offset on the ring record (matches
    /// <see cref="Type10EntityPositionLayout.PositionRecordOffset"/>).</summary>
    public const int PositionOffset = 0x10;

    /// <summary>Hull yaw float32 offset on the ring record (L2 chain field).</summary>
    public const int YawOffset = 0x2c;

    /// <summary>
    /// Decodes the position triple at <c>+0x10</c>. Returns null when the
    /// region is too short (needs at least 12 bytes from the offset) or any
    /// component is not finite.
    /// </summary>
    public static (float X, float Y, float Z)? TryReadPosition(ReadOnlySpan<byte> region)
    {
        if (region.Length < PositionOffset + 12)
        {
            return null;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(region[PositionOffset..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(region[(PositionOffset + 4)..]);
        float z = BinaryPrimitives.ReadSingleLittleEndian(region[(PositionOffset + 8)..]);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            ? (x, y, z)
            : null;
    }

    /// <summary>
    /// Decodes the hull yaw float32 at <c>+0x2C</c>. Returns null when the
    /// region is too short (needs 4 bytes from the offset) or the value is
    /// not finite.
    /// </summary>
    public static float? TryReadYaw(ReadOnlySpan<byte> region)
    {
        if (region.Length < YawOffset + 4)
        {
            return null;
        }

        float yaw = BinaryPrimitives.ReadSingleLittleEndian(region[YawOffset..]);
        return float.IsFinite(yaw) ? yaw : null;
    }
}
