using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Supplies one exact, bounded memory read for a chain walk. Implementations
/// must return false unless the complete destination was filled.
/// </summary>
public delegate bool OffsetChainMemoryReader(uint address, Span<byte> destination);

/// <summary>Outcome of one module-rooted pointer-chain walk.</summary>
public enum OffsetChainWalkStatus
{
    /// <summary>The chain was walked and the value bytes were read.</summary>
    Resolved,

    /// <summary>The chain shape is invalid (empty, bad hop kinds, negative value).</summary>
    InvalidChain,

    /// <summary>The module base was zero, so no root address could be formed.</summary>
    InvalidModuleBase,

    /// <summary>A required read failed (root, an intermediate pointer, or the value).</summary>
    ReadFailed,

    /// <summary>An intermediate pointer resolved to null.</summary>
    NullPointer,
}

/// <summary>
/// Sanitized result of a chain walk. The resolved address is included for
/// diagnostics but carries no process identity; callers decide how to project it.
/// </summary>
public sealed record OffsetChainWalkResult(
    OffsetChainWalkStatus Status,
    uint Address,
    byte[]? Bytes,
    string? FailureStage);

/// <summary>
/// Walks a published pointer chain from a module-rooted RVA through member
/// offsets to a final record offset. The chain shape is validated fail-closed:
/// exactly one <see cref="OffsetChainHopKind.RootRva"/> first, zero or more
/// <see cref="OffsetChainHopKind.MemberOffset"/> dereferences, and exactly one
/// trailing <see cref="OffsetChainHopKind.RecordOffset"/>.
/// </summary>
/// <remarks>
/// This is a STRUCTURAL walker: every <see cref="OffsetChainHopKind.MemberOffset"/>
/// hop dereferences a pointer. It therefore cannot yet walk the published
/// 11.19.0.10 position chains end-to-end — their final member hop
/// (<c>AvatarHelperCurrentIndexOffset 0x1C8</c>) is an integer index read
/// multiplied by the ring stride (0x38), which no current hop kind expresses.
/// The <see cref="Type10EntityPositionResolver"/> remains the authoritative
/// reader for that chain. This walker is the foundation for plain pointer
/// chains (entity-record fields) and for a future stride-aware ring hop.
/// </remarks>
public static class OffsetChainWalker
{
    private const int PointerSize = 4;

    /// <summary>
    /// Walks <paramref name="chain"/> and reads <paramref name="valueLength"/>
    /// bytes at the final record address.
    /// </summary>
    public static OffsetChainWalkResult Walk(
        IReadOnlyList<OffsetChainHop> chain,
        uint moduleBase,
        int valueLength,
        OffsetChainMemoryReader memory)
    {
        if (!TryValidate(chain, valueLength, out string? validationStage))
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.InvalidChain,
                0,
                null,
                validationStage);
        }

        if (moduleBase == 0)
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.InvalidModuleBase,
                0,
                null,
                "module-base");
        }

        uint address = moduleBase + (uint)chain[0].Value;
        for (int index = 1; index < chain.Count - 1; index++)
        {
            if (!TryReadPointer(memory, address, out uint pointer))
            {
                return new OffsetChainWalkResult(
                    OffsetChainWalkStatus.ReadFailed,
                    address,
                    null,
                    $"hop-{index}");
            }

            if (pointer == 0)
            {
                return new OffsetChainWalkResult(
                    OffsetChainWalkStatus.NullPointer,
                    address,
                    null,
                    $"hop-{index}");
            }

            address = pointer + (uint)chain[index].Value;
        }

        address += (uint)chain[^1].Value;
        Span<byte> bytes = stackalloc byte[valueLength];
        if (!memory(address, bytes))
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.ReadFailed,
                address,
                null,
                "record");
        }

        return new OffsetChainWalkResult(
            OffsetChainWalkStatus.Resolved,
            address,
            bytes.ToArray(),
            null);
    }

    private static bool TryValidate(
        IReadOnlyList<OffsetChainHop> chain,
        int valueLength,
        out string? stage)
    {
        stage = null;
        if (chain is null || chain.Count == 0)
        {
            stage = "empty";
            return false;
        }

        if (chain[0].Kind != OffsetChainHopKind.RootRva)
        {
            stage = "hop-0";
            return false;
        }

        if (chain[^1].Kind != OffsetChainHopKind.RecordOffset)
        {
            stage = $"hop-{chain.Count - 1}";
            return false;
        }

        for (int index = 1; index < chain.Count - 1; index++)
        {
            if (chain[index].Kind != OffsetChainHopKind.MemberOffset)
            {
                stage = $"hop-{index}";
                return false;
            }
        }

        for (int index = 0; index < chain.Count; index++)
        {
            if (chain[index].Value < 0)
            {
                stage = $"hop-{index}";
                return false;
            }
        }

        if (valueLength is < 1 or > 8)
        {
            stage = "value-length";
            return false;
        }

        return true;
    }

    private static bool TryReadPointer(
        OffsetChainMemoryReader memory,
        uint address,
        out uint pointer)
    {
        Span<byte> bytes = stackalloc byte[PointerSize];
        if (!memory(address, bytes))
        {
            pointer = 0;
            return false;
        }

        pointer = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }
}
