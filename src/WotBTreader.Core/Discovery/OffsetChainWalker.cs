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

    /// <summary>An entity-map lookup failed to find the target entity id.</summary>
    EntityNotFound,

    /// <summary>An entity-tree traversal exceeded the stated node budget.</summary>
    TraversalLimitExceeded,
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
/// <see cref="OffsetChainHopKind.InlineOffset"/> /
/// <see cref="OffsetChainHopKind.RingIndex"/> /
/// <see cref="OffsetChainHopKind.EntityLookup"/> hops, and exactly one
/// trailing <see cref="OffsetChainHopKind.RecordOffset"/>.
/// </summary>
/// <remarks>
/// The walk tracks a single current OBJECT address. <c>RootRva</c> dereferences
/// the root slot (<c>moduleBase + RVA</c>); <c>memberOffset</c> dereferences a
/// pointer at (object + value); <c>inlineOffset</c> adds value without
/// dereferencing; <c>ringIndex</c> selects an INLINE ring entry at
/// (object + value + index·stride) using the Int32 index field at
/// (object + indexOffset); <c>entityLookup</c> resolves an entity-map lookup
/// (cached fast path + alternative tree roots) mirroring the resolver's
/// <c>FindEntity</c> — given a target entity id supplied per walk, never
/// carried by the chain. The final <c>recordOffset</c> adds value without
/// dereferencing and reads the value bytes.
///
/// The published 11.19.0.10 position chains are still NOT walkable as
/// published: they spell the inline entities step, the inline ring base, and
/// the ring-index read as plain <c>memberOffset</c> hops (which would deref
/// them), and they encode the cache/tree branching as sequential member
/// offsets with no rebase semantics. A chain re-expressed with
/// <c>inlineOffset</c> + <c>entityLookup</c> + <c>ringIndex</c> IS walkable and
/// is proven equivalent to the resolver (see the walker tests). The
/// <see cref="Type10EntityPositionResolver"/> remains the authoritative reader
/// for the published table.
/// </remarks>
public static class OffsetChainWalker
{
    private const int PointerSize = 4;
    private const int MinimumPointerValue = 0x00010000;
    private const int MaximumNodeSize = 256;

    /// <summary>
    /// Walks <paramref name="chain"/> and reads <paramref name="valueLength"/>
    /// bytes at the final record address. <paramref name="entityId"/> is the
    /// runtime target for any <see cref="OffsetChainHopKind.EntityLookup"/> hop
    /// (required if the chain contains one; the chain never carries it).
    /// </summary>
    public static OffsetChainWalkResult Walk(
        IReadOnlyList<OffsetChainHop> chain,
        uint moduleBase,
        int valueLength,
        OffsetChainMemoryReader memory,
        int? entityId = null)
    {
        if (!TryValidate(chain, valueLength, entityId, out string? validationStage))
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

        // Root slot dereference: moduleBase + RVA holds the root pointer.
        if (!TryReadPointer(memory, moduleBase + (uint)chain[0].Value, out uint currentObject))
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.ReadFailed,
                moduleBase + (uint)chain[0].Value,
                null,
                "hop-0");
        }

        if (currentObject == 0)
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.NullPointer,
                moduleBase + (uint)chain[0].Value,
                null,
                "hop-0");
        }

        for (int hopIndex = 1; hopIndex < chain.Count - 1; hopIndex++)
        {
            OffsetChainHop hop = chain[hopIndex];
            switch (hop.Kind)
            {
                case OffsetChainHopKind.MemberOffset:
                    if (!TryReadPointer(
                            memory,
                            currentObject + (uint)hop.Value,
                            out uint pointer))
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.ReadFailed,
                            currentObject + (uint)hop.Value,
                            null,
                            $"hop-{hopIndex}");
                    }

                    if (pointer == 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.NullPointer,
                            currentObject + (uint)hop.Value,
                            null,
                            $"hop-{hopIndex}");
                    }

                    currentObject = pointer;
                    break;

                case OffsetChainHopKind.InlineOffset:
                    currentObject = currentObject + (uint)hop.Value;
                    break;

                case OffsetChainHopKind.RingIndex:
                    if (hop.IndexOffset is not int indexOffset
                        || hop.Stride is not int stride
                        || indexOffset < 0
                        || stride < 0)
                    {
                        return new OffsetChainWalkResult(
                            OffsetChainWalkStatus.InvalidChain,
                            currentObject,
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

                    // INLINE ring array: no pointer dereference.
                    currentObject = currentObject
                        + (uint)hop.Value
                        + ((uint)ringIndex * (uint)stride);
                    break;

                case OffsetChainHopKind.EntityLookup:
                    {
                        if (hop.EntityLookup is not OffsetEntityLookupDescriptor lookup
                            || entityId is not int targetId)
                        {
                            return new OffsetChainWalkResult(
                                OffsetChainWalkStatus.InvalidChain,
                                currentObject,
                                null,
                                $"hop-{hopIndex}");
                        }

                        // Cached-entity fast path (mirrors the resolver's
                        // FindEntity): only used when the cached record's id
                        // matches the target.
                        if (!TryReadPointer(
                                memory,
                                currentObject + (uint)lookup.CachedEntityOffset,
                                out uint cachedEntity))
                        {
                            return new OffsetChainWalkResult(
                                OffsetChainWalkStatus.ReadFailed,
                                currentObject + (uint)lookup.CachedEntityOffset,
                                null,
                                $"hop-{hopIndex}:cached-entity");
                        }

                        if (cachedEntity != 0)
                        {
                            if (!TryReadInt32(
                                    memory,
                                    cachedEntity + (uint)lookup.EntityIdOffset,
                                    out int cachedEntityId))
                            {
                                return new OffsetChainWalkResult(
                                    OffsetChainWalkStatus.ReadFailed,
                                    cachedEntity + (uint)lookup.EntityIdOffset,
                                    null,
                                    $"hop-{hopIndex}:cached-entity-id");
                            }

                            if (cachedEntityId == targetId)
                            {
                                currentObject = cachedEntity;
                                break;
                            }
                        }

                        // ALTERNATIVE tree-map roots, tried in order (mirrors the
                        // resolver's FindEntity: a tree miss falls through to the
                        // next root; a non-EntityNotFound failure stops the walk).
                        bool found = false;
                        Span<byte> nodeBytes = stackalloc byte[lookup.TreeNodeSize];
                        for (int rootIndex = 0; rootIndex < lookup.TreeRootOffsets.Count; rootIndex++)
                        {
                            // The tree root slot holds the SENTINEL pointer directly
                            // (mirrors the resolver: FindEntity keeps the slot
                            // address and FindEntityInTree reads the sentinel at it).
                            if (!TryReadPointer(
                                    memory,
                                    currentObject + (uint)lookup.TreeRootOffsets[rootIndex],
                                    out uint sentinel))
                            {
                                return new OffsetChainWalkResult(
                                    OffsetChainWalkStatus.ReadFailed,
                                    currentObject + (uint)lookup.TreeRootOffsets[rootIndex],
                                    null,
                                    $"hop-{hopIndex}:tree-root-{rootIndex}");
                            }

                            if (!TryReadPointer(
                                    memory,
                                    sentinel + (uint)lookup.TreeSentinelFirstNodeOffset,
                                    out uint node))
                            {
                                return new OffsetChainWalkResult(
                                    OffsetChainWalkStatus.ReadFailed,
                                    sentinel + (uint)lookup.TreeSentinelFirstNodeOffset,
                                    null,
                                    $"hop-{hopIndex}:tree-node-{rootIndex}");
                            }

                            HashSet<uint> visited = [];
                            int nodesVisited = 0;
                            while (node != sentinel)
                            {
                                if (node < MinimumPointerValue)
                                {
                                    return new OffsetChainWalkResult(
                                        OffsetChainWalkStatus.ReadFailed,
                                        node,
                                        null,
                                        $"hop-{hopIndex}:tree-node-pointer-{rootIndex}");
                                }

                                if (!visited.Add(node) || nodesVisited >= lookup.MaxTreeNodes)
                                {
                                    return new OffsetChainWalkResult(
                                        OffsetChainWalkStatus.TraversalLimitExceeded,
                                        node,
                                        null,
                                        $"hop-{hopIndex}:tree-traversal-{rootIndex}");
                                }

                                nodeBytes.Clear();
                                if (!memory(node, nodeBytes))
                                {
                                    return new OffsetChainWalkResult(
                                        OffsetChainWalkStatus.ReadFailed,
                                        node,
                                        null,
                                        $"hop-{hopIndex}:tree-node-{rootIndex}");
                                }

                                nodesVisited++;
                                byte isNil = nodeBytes[lookup.TreeNodeNilOffset];
                                if (isNil == 1)
                                {
                                    break;
                                }

                                if (isNil != 0)
                                {
                                    return new OffsetChainWalkResult(
                                        OffsetChainWalkStatus.ReadFailed,
                                        node,
                                        null,
                                        $"hop-{hopIndex}:tree-nil-flag-{rootIndex}");
                                }

                                int key = BinaryPrimitives.ReadInt32LittleEndian(
                                    nodeBytes[lookup.TreeNodeKeyOffset..]);
                                if (key == targetId)
                                {
                                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(
                                        nodeBytes[lookup.TreeNodeValueOffset..]);
                                    if (value < MinimumPointerValue)
                                    {
                                        return new OffsetChainWalkResult(
                                            OffsetChainWalkStatus.ReadFailed,
                                            node,
                                            null,
                                            $"hop-{hopIndex}:tree-value-{rootIndex}");
                                    }

                                    currentObject = value;
                                    found = true;
                                    break;
                                }

                                int childOffset = targetId < key
                                    ? lookup.TreeNodeChildLessOffset
                                    : lookup.TreeNodeChildGreaterOffset;
                                node = BinaryPrimitives.ReadUInt32LittleEndian(
                                    nodeBytes[childOffset..]);
                            }

                            if (found)
                            {
                                break;
                            }
                        }

                        if (!found)
                        {
                            return new OffsetChainWalkResult(
                                OffsetChainWalkStatus.EntityNotFound,
                                currentObject,
                                null,
                                $"hop-{hopIndex}:entity-lookup");
                        }

                        break;
                    }

                default:
                    return new OffsetChainWalkResult(
                        OffsetChainWalkStatus.InvalidChain,
                        currentObject,
                        null,
                        $"hop-{hopIndex}");
            }
        }

        uint recordAddress = currentObject + (uint)chain[^1].Value;
        Span<byte> bytes = stackalloc byte[valueLength];
        if (!memory(recordAddress, bytes))
        {
            return new OffsetChainWalkResult(
                OffsetChainWalkStatus.ReadFailed,
                recordAddress,
                null,
                "record");
        }

        return new OffsetChainWalkResult(
            OffsetChainWalkStatus.Resolved,
            recordAddress,
            bytes.ToArray(),
            null);
    }

    private static bool TryValidate(
        IReadOnlyList<OffsetChainHop> chain,
        int valueLength,
        int? entityId,
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
                OffsetChainHopKind.MemberOffset
                or OffsetChainHopKind.InlineOffset
                or OffsetChainHopKind.RingIndex
                or OffsetChainHopKind.EntityLookup))
            {
                stage = $"hop-{index}";
                return false;
            }

            if (chain[index].Kind == OffsetChainHopKind.EntityLookup
                && (chain[index].Value != 0
                    || entityId is null
                    || !TryValidateEntityLookup(chain[index].EntityLookup)))
            {
                stage = entityId is null ? "entity-id-required" : $"hop-{index}";
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

        if (valueLength is < 1 or > 64)
        {
            stage = "value-length";
            return false;
        }

        return true;
    }

    private static bool TryValidateEntityLookup(OffsetEntityLookupDescriptor? lookup)
    {
        if (lookup is null
            || lookup.CachedEntityOffset < 0
            || lookup.EntityIdOffset < 0
            || lookup.TreeNodeSize is < 1 or > MaximumNodeSize
            || lookup.TreeNodeNilOffset < 0
            || lookup.TreeNodeKeyOffset < 0
            || lookup.TreeNodeValueOffset < 0
            || lookup.TreeNodeChildLessOffset < 0
            || lookup.TreeNodeChildGreaterOffset < 0
            || lookup.TreeSentinelFirstNodeOffset < 0
            || lookup.MaxTreeNodes < 1
            || lookup.TreeRootOffsets is not { Count: > 0 }
            || lookup.TreeRootOffsets.Any(static offset => offset < 0))
        {
            return false;
        }

        // Reads must stay inside the node record (fail-closed, never throw).
        return lookup.TreeNodeNilOffset < lookup.TreeNodeSize
            && lookup.TreeNodeKeyOffset + 4 <= lookup.TreeNodeSize
            && lookup.TreeNodeValueOffset + 4 <= lookup.TreeNodeSize
            && lookup.TreeNodeChildLessOffset + 4 <= lookup.TreeNodeSize
            && lookup.TreeNodeChildGreaterOffset + 4 <= lookup.TreeNodeSize;
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
