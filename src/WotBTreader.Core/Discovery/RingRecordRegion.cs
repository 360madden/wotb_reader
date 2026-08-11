using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Decodes a bounded ring-record region dump (the `ring-record` region
/// anchor of the batch read surface) into the fields the live frame needs:
/// the world position float32 triple at <c>+0x10</c> (the published
/// position-chain read) and the rotation triple at the record tail — roll
/// <c>+0x28</c>, pitch <c>+0x2C</c>, yaw <c>+0x30</c> — live-verified by the
/// OD-RECOVERY-088 L2 facing session (Oasis Palms, 48 region dumps, all
/// three fields align to packet ground truth within 0.5 deg at the ~5 s
/// memory-apply lag). The earlier +0x2C yaw rehearsal hit was an artifact
/// of the rehearsal constructing its synthetic dumps with yaw placed at
/// +0x2C by design; the live read corrects the layout. Pure — no IO, no
/// Win32, no allocation. Fail-closed: a region too short for a field or a
/// non-finite value yields null for that field, never a fabricated number.
/// </summary>
public static class RingRecordRegion
{
    /// <summary>Position float32 triple offset on the ring record (matches
    /// <see cref="Type10EntityPositionLayout.PositionRecordOffset"/>).</summary>
    public const int PositionOffset = 0x10;

    /// <summary>Roll float32 offset on the ring record (OD-RECOVERY-088).</summary>
    public const int RollOffset = 0x28;

    /// <summary>Pitch float32 offset on the ring record (OD-RECOVERY-088).</summary>
    public const int PitchOffset = 0x2c;

    /// <summary>Hull yaw float32 offset on the ring record (OD-RECOVERY-088
    /// corrected the rehearsal's +0x2C prediction to +0x30).</summary>
    public const int YawOffset = 0x30;

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
    /// Decodes the hull yaw float32 at <c>+0x30</c>. Returns null when the
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

    /// <summary>
    /// Decodes the pitch float32 at <c>+0x2C</c>. Returns null when the
    /// region is too short (needs 4 bytes from the offset) or the value is
    /// not finite.
    /// </summary>
    public static float? TryReadPitch(ReadOnlySpan<byte> region)
    {
        if (region.Length < PitchOffset + 4)
        {
            return null;
        }

        float pitch = BinaryPrimitives.ReadSingleLittleEndian(region[PitchOffset..]);
        return float.IsFinite(pitch) ? pitch : null;
    }

    /// <summary>
    /// Decodes the roll float32 at <c>+0x28</c>. Returns null when the
    /// region is too short (needs 4 bytes from the offset) or the value is
    /// not finite.
    /// </summary>
    public static float? TryReadRoll(ReadOnlySpan<byte> region)
    {
        if (region.Length < RollOffset + 4)
        {
            return null;
        }

        float roll = BinaryPrimitives.ReadSingleLittleEndian(region[RollOffset..]);
        return float.IsFinite(roll) ? roll : null;
    }
}
