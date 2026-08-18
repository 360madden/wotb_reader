using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Decodes a bounded entity-base region dump (the `entity-base` region
/// anchor of the batch read surface) into the health fields the live frame
/// needs: current health as signed int16 at <c>+0xB8</c>, the alive byte at
/// <c>+0xBA</c>, and max health as signed int16 at <c>+0x11C</c> — pinned by
/// the static <c>VerifyPlayerHpChain</c> evidence on the 11.19.0.10 build
/// and confirmed live by the OD-RECOVERY-087 L1 HP session (savanna:
/// every health drop equaled its damage sum exactly; max HP constant at
/// 1550). The healing int16 at <c>+0x11E</c> is pinned by the same chain but
/// not read by the live frame (no live consumption yet). Pure — no IO, no
/// Win32, no allocation. Fail-closed: a region too short for a field or a
/// value that cannot be a health quantity (negative, or an alive byte that
/// is neither 0 nor 1) yields null for that field, never a fabricated
/// number.
/// </summary>
public static class EntityBaseRegion
{
    /// <summary>Current-health signed int16 offset on the entity base
    /// (VerifyPlayerHpChain; OD-RECOVERY-087 live-confirmed).</summary>
    public const int HpCurrentOffset = 0xb8;

    /// <summary>Alive byte offset on the entity base (VerifyPlayerHpChain).</summary>
    public const int AliveOffset = 0xba;

    /// <summary>Max-health signed int16 offset on the entity base
    /// (VerifyPlayerHpChain; OD-RECOVERY-087 constant at 1550).</summary>
    public const int HpMaxOffset = 0x11c;

    /// <summary>Healing signed int16 offset on the entity base
    /// (VerifyPlayerHpChain; documented, not read by the live frame).</summary>
    public const int HealingOffset = 0x11e;

    /// <summary>
    /// Decodes the current-health int16 at <c>+0xB8</c> as a float. Returns
    /// null when the region is too short (needs 2 bytes from the offset) or
    /// the value is negative (a corrupt/sentinel read — health is never
    /// negative; zero is a valid dead-tank value).
    /// </summary>
    public static float? TryReadHpCurrent(ReadOnlySpan<byte> region)
    {
        if (region.Length < HpCurrentOffset + 2)
        {
            return null;
        }

        short current = BinaryPrimitives.ReadInt16LittleEndian(region[HpCurrentOffset..]);
        return current < 0 ? null : current;
    }

    /// <summary>
    /// Decodes the alive byte at <c>+0xBA</c>. Returns null when the region
    /// is too short or the byte is neither 0 nor 1 (not a bool — fail
    /// closed).
    /// </summary>
    public static bool? TryReadAlive(ReadOnlySpan<byte> region)
    {
        if (region.Length < AliveOffset + 1)
        {
            return null;
        }

        byte alive = region[AliveOffset];
        return alive switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
    }

    /// <summary>
    /// Decodes the max-health int16 at <c>+0x11C</c> as a float. Returns
    /// null when the region is too short (needs 2 bytes from the offset) or
    /// the value is negative (a corrupt/sentinel read).
    /// </summary>
    public static float? TryReadHpMax(ReadOnlySpan<byte> region)
    {
        if (region.Length < HpMaxOffset + 2)
        {
            return null;
        }

        short max = BinaryPrimitives.ReadInt16LittleEndian(region[HpMaxOffset..]);
        return max < 0 ? null : max;
    }
}
