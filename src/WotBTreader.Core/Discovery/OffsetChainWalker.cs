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

    /// <summary>A required read failed (root, an intermediate pointer, the ring index, or the value).</summary>
    ReadFailed,

    /// <summary>An intermediate pointer resolved to null.</summary>
    NullPointer,

    /// <summary>A ring index read was negative.</summary>
    InvalidRingIndex,
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
/// intermediate <see cref="OffsetChainHopKind.MemberOffset"/> /
/// <see cref="OffsetChainHopKind.RingIndex"/> hops, and exactly one trailing
/// <see cref="OffsetChainHopKind.RecordOffset"/>.
/// </summary>
/// <remarks>
/// This is a STRUCTURAL walker: every <see cref="OffsetChainHopKind.MemberOffset"/>
/// hop dereferences a pointer, and <see cref="OffsetChainHopKind.RingIndex"/>
/// selects a ring entry from an index Int32 × stride. It therefore cannot yet
/// walk the published 11.19.0.10 position chains end-to-end: those chains
/// contain a cached fast path and three ALTERNATIVE entity-tree map roots
/// (branching the resolver performs in <c>FindEntity</c>, which no hop kind
/// expresses), and the ring-record step needs a stride-aware hop. The
/// <see cref="Type10EntityPositionResolver"/> remains the authoritative reader
/// for that chain. This walker is the foundation for linear pointer chains
/// (entity-record fields) and for ring/array records.
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
        uint currentObject = 0;
        bool hasObject = false;

        for (int hopIndex = 1; hopIndex < chain.Count - 1; hopIndex++)
        {
            OffsetChainHop hop = chain[hopIndex];
            switch (hop.Kind)
            {
                case OffsetChainHopKind.MemberOffset:
                    if (!TryReadPointer(memory, address, out uint pointer))
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.ReadFailed,
                            address,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (pointer == 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.NullPointer,
                            address,
                            null,
                            $"hop-{hopIndex}");
                    }

                    currentObject = pointer;
                    hasObject = true;
                    address = pointer + (uint)hop.Value;
                    break;

                case OffsetChainHopKind.RingIndex:
                    if (!hasObject
                        || hop.IndexOffset is not int indexOffset
                        || hop.Stride is not int stride
                        || indexOffset < 0
                        || stride < 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.InvalidChain,
                            address,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (!TryReadPointer(
                            memory,
                            currentObject + (uint)hop.Value,
                            out uint ring))
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.ReadFailed,
                            currentObject + (uint)hop.Value,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (ring == 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.NullPointer,
                            currentObject + (uint)hop.Value,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (!TryReadInt32(
                            memory,
                            currentObject + (uint)indexOffset,
                            out int ringIndex))
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.ReadFailed,
                            currentObject + (uint)indexOffset,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (ringIndex < 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.InvalidRingIndex,
                            currentObject + (uint)indexOffset,
                            null,
                            $"hop-{hopIndex}");
                    }

                    address = ring + ((uint)ringIndex * (uint)stride);
                    currentObject = ring;
                    break;

                default:
                    return new OffsetChainWalkResult(
                        OffsetChainWalkStatus.InvalidChain,
                        address,
                        null,
                        $"hop-{hopIndex}");
            }
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
            if (chain[index].Kind is not (
                OffsetChainHopKind.MemberOffset or OffsetChainHopKind.RingIndex))
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

    private static bool TryReadInt32(
        OffsetChainMemoryReader memory,
        uint address,
        out int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!memory(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }
}
