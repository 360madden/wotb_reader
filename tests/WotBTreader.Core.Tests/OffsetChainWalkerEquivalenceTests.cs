using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

/// <summary>
/// Equivalence proofs: the walker, given a position chain re-expressed with
/// <c>inlineOffset</c> + <c>entityLookup</c> + <c>ringIndex</c>, reproduces the
/// exact ring-record address the resolver's own traversal produces, over the
/// SAME synthetic memory. This pins the walker's semantics to the resolver's
/// (cache fast path, alternative tree roots, signed key traversal, node
/// budget, inline ring) so future hop-kind drift is caught immediately.
/// </summary>
[TestClass]
public sealed class OffsetChainWalkerEquivalenceTests
{
    private const uint ModuleBase = 0x10000000;
    private const int EntityId = 4242;

    [TestMethod]
    public void Walker_MatchesResolver_CachedPath_AddressAndValues()
    {
        var memory = FullSpineFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);
        RunEquivalence(memory, 12.5f, -3.25f, 44.75f);
    }

    [TestMethod]
    public void Walker_MatchesResolver_PrimaryTreePath()
    {
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: EntityId,
            tertiaryKey: null,
            secondaryKey: null,
            x: 1f,
            y: 2f,
            z: 3f);
        RunEquivalence(memory, 1f, 2f, 3f);
    }

    [TestMethod]
    public void Walker_MatchesResolver_AlternativeTertiaryRootPath()
    {
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: null,
            tertiaryKey: EntityId,
            secondaryKey: null,
            x: 4f,
            y: 5f,
            z: 6f);
        RunEquivalence(memory, 4f, 5f, 6f);
    }

    [TestMethod]
    public void Walker_MatchesResolver_ThirdAlternativeRootPath()
    {
        // The resolver tries all three ALTERNATIVE roots IN ORDER
        // [0x1c (primary), 0x40 (tertiary), 0x34 (secondary)]; the entity
        // living only in the LAST root exercises the deepest fallthrough
        // (two misses then a hit) and the walker must reproduce it.
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: null,
            tertiaryKey: null,
            secondaryKey: EntityId,
            x: 13f,
            y: 14f,
            z: 15f);
        RunEquivalence(memory, 13f, 14f, 15f);
    }

    [TestMethod]
    public void Walker_MatchesResolver_EntityNotFound_WhenAllTreesEmpty()
    {
        var memory = FullSpineFixture.CreateEmptyMaps();

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            PositionChain(),
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, resolver.Status);
        Assert.AreEqual(OffsetChainWalkStatus.EntityNotFound, walker.Status);
        Assert.IsNull(resolver.RecordAddress);
        Assert.IsNull(walker.ResolvedEntityAddress);
    }

    [TestMethod]
    public void Walker_MatchesResolver_TraversalBudget_WhenTreeExhausted()
    {
        // The resolver trips FindEntityInTree when nodesVisited >=
        // MaxTreeNodes (1024); the walker's entityLookup must trip at the
        // SAME node on the same memory. A 1025-node degenerate greater-chain
        // with the target absent forces both readers to exhaust the budget at
        // node 1025 (keys 1..1025 all < 4242, so the walk descends greater
        // every step).
        var memory = FullSpineFixture.CreateEmptyMaps();
        const uint chainStart = 0x27000000;
        const uint sentinel = 0x26000000;
        memory.WriteUInt32(memory.Entities + 0x1c, sentinel);
        memory.WriteUInt32(sentinel + 0x04, chainStart);
        const int nodeCount = 1025;
        for (int index = 0; index < nodeCount; index++)
        {
            uint node = chainStart + (uint)(index * 0x18);
            uint greater = index == nodeCount - 1
                ? sentinel
                : chainStart + (uint)((index + 1) * 0x18);
            memory.WriteTreeNode(node, sentinel, greater, key: index + 1, value: memory.Entity);
        }

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            PositionChain(),
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(Type10EntityPositionStatus.TraversalLimitExceeded, resolver.Status);
        Assert.AreEqual(OffsetChainWalkStatus.TraversalLimitExceeded, walker.Status);
        Assert.IsNull(resolver.RecordAddress);
    }

    [TestMethod]
    public void Walker_MatchesResolver_SignedKeyTraversal()
    {
        // A negative key on the greater side must still traverse correctly
        // (the resolver uses signed comparison; the walker must too).
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: EntityId,
            tertiaryKey: null,
            secondaryKey: null,
            x: 7f,
            y: 8f,
            z: 9f);
        memory.WriteInt32(memory.RootNodeKeySlot, -100);
        memory.WriteUInt32(memory.RootNodeGreaterSlot, memory.SecondNode);
        memory.WriteTreeNode(
            memory.SecondNode,
            left: memory.SentinelA,
            right: memory.SentinelA,
            key: EntityId,
            value: memory.Entity);
        RunEquivalence(memory, 7f, 8f, 9f);
    }

    private static void RunEquivalence(FullSpineFixture memory, float x, float y, float z)
    {
        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            PositionChain(),
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(
            resolver.Status == Type10EntityPositionStatus.Resolved,
            walker.Status == OffsetChainWalkStatus.Resolved,
            $"resolver={resolver.Status} walker={walker.Status} ({resolver.FailureStage})");

        if (resolver.Status == Type10EntityPositionStatus.Resolved)
        {
            Assert.IsNotNull(resolver.RecordAddress);
            // The resolver returns the ring-RECORD base; the walker returns the
            // final FIELD address (record + PositionRecordOffset).
            Assert.AreEqual(
                resolver.RecordAddress.Value + Layout.PositionRecordOffset,
                walker.Address);
            Assert.IsNotNull(walker.Bytes);
            Assert.AreEqual(x, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(0)));
            Assert.AreEqual(y, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(4)));
            Assert.AreEqual(z, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(8)));
            // The entityLookup hop must expose the FOUND entity base — the
            // region a record-diffing reader dumps around.
            Assert.AreEqual(memory.Entity, walker.ResolvedEntityAddress);
        }
    }

    [TestMethod]
    public void Walker_ResolvesEntityRecordPositionTriple_MatchingRingRecord()
    {
        // Mechanism proof for the entity-record form the record-diffing
        // harness will dump: entityLookup -> [entity+0x3C] transform ->
        // position triple at +0x1C. The +0x3C/+0x1C offsets are the
        // Ghidra-verified CANDIDATE layout (FUN_00bc3940, ledger
        // OD-RECOVERY-052/053) — not live-verified — so they live only here,
        // never in the layout record or any published table. Both the ring
        // record (the published position chain) and the entity-record triple
        // hold the same transform; the walker must reach both and agree.
        var memory = FullSpineFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);
        const uint transform = 0x27000000;
        memory.WriteUInt32(memory.Entity + 0x3c, transform);
        memory.WriteFloats(transform + 0x1c, 12.5f, -3.25f, 44.75f);

        OffsetChainWalkResult ring = OffsetChainWalker.Walk(
            PositionChain(),
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);
        OffsetChainWalkResult entityRecord = OffsetChainWalker.Walk(
            EntityRecordPositionChain(),
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, ring.Status);
        Assert.AreEqual(OffsetChainWalkStatus.Resolved, entityRecord.Status);
        Assert.AreEqual(memory.Entity, entityRecord.ResolvedEntityAddress);
        Assert.AreEqual(transform + 0x1c, entityRecord.Address);
        // The two position copies agree (cross-check in synthetic memory).
        CollectionAssert.AreEqual(ring.Bytes, entityRecord.Bytes);
        Assert.IsNotNull(entityRecord.Bytes);
        Assert.AreEqual(
            12.5f,
            BinaryPrimitives.ReadSingleLittleEndian(entityRecord.Bytes.AsSpan(0)));
        Assert.AreEqual(
            -3.25f,
            BinaryPrimitives.ReadSingleLittleEndian(entityRecord.Bytes.AsSpan(4)));
        Assert.AreEqual(
            44.75f,
            BinaryPrimitives.ReadSingleLittleEndian(entityRecord.Bytes.AsSpan(8)));
    }

    private static Type10EntityPositionLayout Layout => Type10EntityPositionLayout.WotBlitz1119010;

    private static IReadOnlyList<OffsetChainHop> PositionChain()
    {
        Type10EntityPositionLayout layout = Layout;
        return
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)layout.GameCoreRootRva, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.GameCoreAppControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AppControllerSessionControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.SessionControllerAccountControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AccountControllerActiveControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.PlaybackControllerConnectionOffset, null),
            new OffsetChainHop(OffsetChainHopKind.InlineOffset, (int)layout.ConnectionEntitiesOffset, null),
            EntityLookupHop(),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.EntityMovementFilterOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AvatarFilterHelperOffset, null),
            new OffsetChainHop(
                OffsetChainHopKind.RingIndex,
                (int)layout.AvatarHelperRingOffset,
                null,
                IndexOffset: (int)layout.AvatarHelperCurrentIndexOffset,
                Stride: (int)layout.AvatarHelperRingStride),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, (int)layout.PositionRecordOffset, null),
        ];
    }

    /// <summary>
    /// The entity-record chain form: after entityLookup resolves the entity
    /// base, [entity+0x3C] is the transform object and the position triple is
    /// at transform+0x1C. The +0x3C/+0x1C hops use the Ghidra-verified
    /// CANDIDATE offsets (FUN_00bc3940) — deliberately test-local constants,
    /// never published, until live verification.
    /// </summary>
    private static IReadOnlyList<OffsetChainHop> EntityRecordPositionChain()
    {
        Type10EntityPositionLayout layout = Layout;
        return
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)layout.GameCoreRootRva, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.GameCoreAppControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AppControllerSessionControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.SessionControllerAccountControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AccountControllerActiveControllerOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.PlaybackControllerConnectionOffset, null),
            new OffsetChainHop(OffsetChainHopKind.InlineOffset, (int)layout.ConnectionEntitiesOffset, null),
            EntityLookupHop(),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, 0x3c, null),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x1c, null),
        ];
    }

    private static OffsetChainHop EntityLookupHop()
    {
        Type10EntityPositionLayout layout = Layout;
        return new OffsetChainHop(
            OffsetChainHopKind.EntityLookup,
            0,
            null,
            EntityLookup: new OffsetEntityLookupDescriptor(
                CachedEntityOffset: (int)layout.CachedEntityOffset,
                EntityIdOffset: (int)layout.EntityIdOffset,
                TreeRootOffsets: layout.EntityTreeObjectOffsets
                    .Select(static offset => (int)offset).ToArray(),
                TreeNodeSize: 0x18,
                TreeNodeNilOffset: 0x0d,
                TreeNodeKeyOffset: 0x10,
                TreeNodeValueOffset: 0x14,
                TreeNodeChildLessOffset: 0x00,
                TreeNodeChildGreaterOffset: 0x08,
                TreeSentinelFirstNodeOffset: 0x04,
                MaxTreeNodes: layout.MaxTreeNodes));
    }

    /// <summary>
    /// Full-spine synthetic memory mirroring the resolver's own fixture: every
    /// object the resolver validates (vtables) and reads (spine, entity maps,
    /// ring) at the layout's exact offsets. The cache and tree-map contents are
    /// caller-controlled so both the cache path and each alternative tree root
    /// can be exercised identically by both readers.
    /// </summary>
    private sealed class FullSpineFixture
    {
        private readonly Dictionary<uint, byte> _bytes = [];

        public uint GameCore { get; } = 0x20000000;
        public uint AppController { get; } = 0x20800000;
        public uint SessionController { get; } = 0x21000000;
        public uint AccountController { get; } = 0x21800000;
        public uint PlaybackController { get; } = 0x21c00000;
        public uint Connection { get; } = 0x22000000;
        public uint Entities => Connection + 0x04;
        public uint Entity { get; } = 0x23000000;
        public uint Filter { get; } = 0x24000000;
        public uint Helper { get; } = 0x25000000;
        public uint Record => Helper + 0x08 + (3 * 0x38);
        public uint SentinelA { get; } = 0x26000000;
        public uint RootA { get; } = 0x26100000;
        public uint SentinelB { get; } = 0x26200000;
        public uint RootB { get; } = 0x26300000;
        public uint SentinelC { get; } = 0x26400000;
        public uint RootC { get; } = 0x26500000;
        public uint SecondNode { get; } = 0x26600000;
        public uint RootNodeKeySlot => RootA + 0x10;
        public uint RootNodeGreaterSlot => RootA + 0x08;

        public static FullSpineFixture CreateCached(int entityId, float x, float y, float z)
        {
            var memory = new FullSpineFixture();
            memory.BuildSpine();
            memory.WriteUInt32(memory.Entities + 0x48, memory.Entity);
            memory.WriteInt32(memory.Entity + 0x1c, entityId);
            memory.WritePosition(memory.Record, x, y, z);
            return memory;
        }

        public static FullSpineFixture CreateTree(
            int entityId,
            int? primaryRootKey,
            int? tertiaryKey,
            int? secondaryKey,
            float x,
            float y,
            float z)
        {
            var memory = new FullSpineFixture();
            memory.BuildSpine();
            memory.WriteUInt32(memory.Entities + 0x48, 0); // explicit cache miss
            // The resolver REVALIDATES the found entity's id after the lookup.
            memory.WriteInt32(memory.Entity + 0x1c, entityId);
            AddTree(memory, memory.Entities + 0x1c, primaryRootKey, memory.SentinelA, memory.RootA);
            AddTree(memory, memory.Entities + 0x40, tertiaryKey, memory.SentinelB, memory.RootB);
            AddTree(memory, memory.Entities + 0x34, secondaryKey, memory.SentinelC, memory.RootC);
            memory.WritePosition(memory.Record, x, y, z);
            return memory;
        }

        public static FullSpineFixture CreateEmptyMaps()
        {
            var memory = new FullSpineFixture();
            memory.BuildSpine();
            memory.WriteUInt32(memory.Entities + 0x48, 0);
            AddTree(memory, memory.Entities + 0x1c, null, memory.SentinelA, memory.RootA);
            AddTree(memory, memory.Entities + 0x40, null, memory.SentinelB, memory.RootB);
            AddTree(memory, memory.Entities + 0x34, null, memory.SentinelC, memory.RootC);
            return memory;
        }

        private void BuildSpine()
        {
            Type10EntityPositionLayout layout = Layout;
            WriteUInt32(ModuleBase + layout.GameCoreRootRva, GameCore);
            WriteUInt32(GameCore + layout.GameCoreAppControllerOffset, AppController);
            WriteUInt32(AppController, ModuleBase + layout.AppControllerVtableRva);
            WriteUInt32(AppController + layout.AppControllerSessionControllerOffset, SessionController);
            WriteUInt32(SessionController, ModuleBase + layout.SessionControllerVtableRva);
            WriteUInt32(SessionController + layout.SessionControllerAccountControllerOffset, AccountController);
            WriteUInt32(AccountController, ModuleBase + layout.AccountControllerVtableRva);
            WriteUInt32(AccountController + layout.AccountControllerActiveControllerOffset, PlaybackController);
            WriteUInt32(PlaybackController, ModuleBase + layout.PlaybackControllerVtableRva);
            WriteUInt32(PlaybackController + layout.PlaybackControllerConnectionOffset, Connection);
            WriteUInt32(Entity + 0x38, Filter);
            WriteUInt32(Filter + 0x08, Helper);
            UseSubtypePair(1);
            WriteInt32(Helper + 0x1c8, 3);
        }

        private void UseSubtypePair(int subtypeIndex)
        {
            Type10EntityPositionLayout layout = Layout;
            WriteUInt32(Filter, ModuleBase + layout.MovementFilterVtableRvas[subtypeIndex]);
            WriteUInt32(Helper, ModuleBase + layout.AvatarHelperVtableRvas[subtypeIndex]);
        }

        private static void AddTree(
            FullSpineFixture memory,
            uint treeObject,
            int? rootKey,
            uint sentinel,
            uint root)
        {
            memory.WriteUInt32(treeObject, sentinel);
            if (rootKey.HasValue)
            {
                memory.WriteUInt32(sentinel + 0x04, root);
                memory.WriteTreeNode(
                    root,
                    left: sentinel,
                    right: sentinel,
                    key: rootKey.Value,
                    value: memory.Entity);
            }
            else
            {
                // Empty tree: node == sentinel terminates the walk immediately.
                memory.WriteUInt32(sentinel + 0x04, sentinel);
            }
        }

        public void WriteTreeNode(uint address, uint left, uint right, int key, uint value)
        {
            Span<byte> empty = stackalloc byte[0x18];
            Write(address, empty);
            WriteUInt32(address, left);
            WriteUInt32(address + 0x08, right);
            WriteByte(address + 0x0d, 0);
            WriteInt32(address + 0x10, key);
            WriteUInt32(address + 0x14, value);
        }

        public void WritePosition(uint record, float x, float y, float z)
        {
            Span<byte> bytes = stackalloc byte[0x38];
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x10..], BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x14..], BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x18..], BitConverter.SingleToInt32Bits(z));
            Write(record, bytes);
        }

        public void WriteFloats(uint address, float x, float y, float z)
        {
            Span<byte> bytes = stackalloc byte[0x0c];
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x00..], BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x04..], BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x08..], BitConverter.SingleToInt32Bits(z));
            Write(address, bytes);
        }

        public bool Read(uint address, Span<byte> destination)
        {
            for (int index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(address + (uint)index, out byte value))
                {
                    return false;
                }

                destination[index] = value;
            }

            return true;
        }

        public void WriteUInt32(uint address, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(address, bytes);
        }

        public void WriteInt32(uint address, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(address, bytes);
        }

        private void WriteByte(uint address, byte value) => _bytes[address] = value;

        private void Write(uint address, ReadOnlySpan<byte> bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                _bytes[address + (uint)index] = bytes[index];
            }
        }
    }
}
