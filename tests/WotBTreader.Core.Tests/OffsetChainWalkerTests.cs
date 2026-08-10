using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class OffsetChainWalkerTests
{
    private const uint ModuleBase = 0x10000000;
    private const uint RootRva = 0x04000000;

    [TestMethod]
    public void Walk_ValidChain_ResolvesValueAndAddress()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x10, memory.Object2);
        memory.WriteUInt32(memory.Object2 + 0x20, memory.Object3);
        memory.WritePosition(memory.Object3 + 0x30, 12.5f, -3.25f, 44.75f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x20),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.Object3 + 0x30, result.Address);
        Assert.IsNotNull(result.Bytes);
        Assert.AreEqual(12.5f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes));
        Assert.IsNull(result.FailureStage);
    }

    [TestMethod]
    public void Walk_RootThenRecordOffset_ReadsDirectly()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WritePosition(memory.Object1 + 0x08, 7f, 8f, 9f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x08, null),
            ],
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.Object1 + 0x08, result.Address);
        Assert.AreEqual(7f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_InlineOffset_AddsWithoutDereferencing()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        // Object1 + 0x04 IS the value object (an inline member: no pointer
        // stored at Object1 + 0x04, the bytes there are the object itself).
        memory.WritePosition(memory.Object1 + 0x04 + 0x10, 1f, 2f, 3f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
                new OffsetChainHop(OffsetChainHopKind.InlineOffset, 0x04, null),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x10, null),
            ],
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.Object1 + 0x04 + 0x10, result.Address);
        Assert.AreEqual(1f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_NullPointerMidChain_ReturnsNullPointer()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        // Explicit null pointer at Object1 + 0x10.
        memory.WriteUInt32(memory.Object1 + 0x10, 0);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x20),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.NullPointer, result.Status);
        Assert.AreEqual("hop-1", result.FailureStage);
        Assert.IsNull(result.Bytes);
    }

    [TestMethod]
    public void Walk_UnreadableRoot_ReturnsReadFailed()
    {
        var memory = new MemoryFixture(); // nothing placed anywhere

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x20),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.ReadFailed, result.Status);
        Assert.AreEqual("hop-0", result.FailureStage);
    }

    [TestMethod]
    public void Walk_UnreadableRecord_ReturnsReadFailed()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x10, memory.Object2);
        memory.WriteUInt32(memory.Object2 + 0x20, memory.Object3);
        // Object3 + 0x30 never written -> record read fails.

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x20),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.ReadFailed, result.Status);
        Assert.AreEqual("record", result.FailureStage);
    }

    [TestMethod]
    public void Walk_ZeroModuleBase_ReturnsInvalidModuleBase()
    {
        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x30),
            moduleBase: 0,
            valueLength: 4,
            new MemoryFixture().Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidModuleBase, result.Status);
        Assert.AreEqual("module-base", result.FailureStage);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Walk_ChainShapesWithBadFirstOrLastHop_AreInvalid(int caseIndex)
    {
        List<OffsetChainHop> chain = caseIndex switch
        {
            // First hop not RootRva.
            0 => [new OffsetChainHop(OffsetChainHopKind.MemberOffset, 4, null)],
            // Last hop not RecordOffset.
            1 =>
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, 4, null),
                new OffsetChainHop(OffsetChainHopKind.MemberOffset, 8, null),
            ],
            // Unknown hop kind in the middle.
            2 =>
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, 4, null),
                new OffsetChainHop((OffsetChainHopKind)99, 8, null),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 12, null),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(caseIndex)),
        };

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            chain,
            ModuleBase,
            valueLength: 4,
            new MemoryFixture().Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.IsNull(result.Bytes);
    }

    [TestMethod]
    public void Walk_EmptyChain_IsInvalid()
    {
        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [],
            ModuleBase,
            valueLength: 4,
            new MemoryFixture().Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("empty", result.FailureStage);
    }

    [TestMethod]
    public void Walk_NegativeHopValue_IsInvalid()
    {
        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, -1, null),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 8, null),
            ],
            ModuleBase,
            valueLength: 4,
            new MemoryFixture().Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("hop-0", result.FailureStage);
    }

    [TestMethod]
    public void Walk_RingIndex_SelectsInlineRingEntry()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x08, memory.Object2);
        memory.WriteInt32(memory.Object2 + 0x1C8, 2);
        // INLINE ring array: entries live at Object2 + 0x08 + index * 0x38.
        memory.WritePosition(memory.Object2 + 0x08 + (2 * 0x38) + 0x10, 42.5f, 0f, 0f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            RingChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.Object2 + 0x08 + (2 * 0x38) + 0x10, result.Address);
        Assert.AreEqual(42.5f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_RingIndex_MissingStride_IsInvalidChain()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x08, memory.Object2);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
                new OffsetChainHop(OffsetChainHopKind.MemberOffset, 0x08, null),
                new OffsetChainHop(OffsetChainHopKind.RingIndex, 0x08, null, IndexOffset: 0x1C8),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x10, null),
            ],
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("hop-2", result.FailureStage);
    }

    [TestMethod]
    public void Walk_RingIndex_NegativeIndex_ReturnsInvalidRingIndex()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x08, memory.Object2);
        memory.WriteInt32(memory.Object2 + 0x1C8, -1);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            RingChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidRingIndex, result.Status);
        Assert.AreEqual("hop-2", result.FailureStage);
    }

    [TestMethod]
    public void Walk_RingIndex_IndexReadFailure_ReturnsReadFailed()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x08, memory.Object2);
        // Object2 + 0x1C8 never written -> index read fails.

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            RingChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.ReadFailed, result.Status);
        Assert.AreEqual("hop-2", result.FailureStage);
    }

    [TestMethod]
    public void Walk_RingIndex_UnreadableEntry_ReturnsReadFailed()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Object1);
        memory.WriteUInt32(memory.Object1 + 0x08, memory.Object2);
        memory.WriteInt32(memory.Object2 + 0x1C8, 2);
        // No entries written at Object2 + 0x08 + 2*0x38.

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            RingChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.ReadFailed, result.Status);
        Assert.AreEqual("record", result.FailureStage);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(65)]
    public void Walk_OutOfRangeValueLength_IsInvalid(int valueLength)
    {
        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x30),
            ModuleBase,
            valueLength,
            new MemoryFixture().Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("value-length", result.FailureStage);
    }

    [TestMethod]
    public void Walk_EntityLookup_CacheHit_RebasesToCachedEntity()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        memory.WriteUInt32(memory.Map + 0x48, memory.CachedEntity);
        memory.WriteInt32(memory.CachedEntity + 0x1c, 4242);
        memory.WriteUInt32(memory.CachedEntity + 0x20, memory.ValueObject);
        memory.WritePosition(memory.ValueObject + 0x10, 5f, 6f, 7f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.ValueObject + 0x10, result.Address);
        Assert.AreEqual(5f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_EntityLookup_CacheIdMismatch_FallsThroughToTree()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        // Cache holds a different entity id -> miss.
        memory.WriteUInt32(memory.Map + 0x48, memory.CachedEntity);
        memory.WriteInt32(memory.CachedEntity + 0x1c, 9999);
        // Primary tree root holds the target.
        AddTree(memory, memory.Map + 0x1c, rootKey: 4242);
        AddEmptyTree(memory, memory.Map + 0x40);
        AddEmptyTree(memory, memory.Map + 0x34);
        memory.WriteUInt32(memory.Entity + 0x20, memory.ValueObject);
        memory.WritePosition(memory.ValueObject + 0x10, 11f, 12f, 13f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.ValueObject + 0x10, result.Address);
        Assert.AreEqual(11f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_EntityLookup_AlternativeRoots_TriedInOrder()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        memory.WriteUInt32(memory.Map + 0x48, 0); // explicit cache miss
        AddEmptyTree(memory, memory.Map + 0x1c);
        AddEmptyTree(memory, memory.Map + 0x40);
        AddTree(memory, memory.Map + 0x34, rootKey: 4242); // third root finds it
        memory.WriteUInt32(memory.Entity + 0x20, memory.ValueObject);
        memory.WritePosition(memory.ValueObject + 0x10, 21f, 22f, 23f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.ValueObject + 0x10, result.Address);
        Assert.AreEqual(21f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
    }

    [TestMethod]
    public void Walk_EntityLookup_EntityNotFound_WhenAllRootsMiss()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        memory.WriteUInt32(memory.Map + 0x48, 0);
        AddEmptyTree(memory, memory.Map + 0x1c);
        AddEmptyTree(memory, memory.Map + 0x40);
        AddEmptyTree(memory, memory.Map + 0x34);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.EntityNotFound, result.Status);
        Assert.AreEqual("hop-1:entity-lookup", result.FailureStage);
    }

    [TestMethod]
    public void Walk_EntityLookup_TraversalLimitExceeded_WhenBudgetExhausted()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        memory.WriteUInt32(memory.Map + 0x48, 0);
        // The target is absent: root key 100, right child key 200, so the walk
        // descends two levels and the budget of 1 trips at the child.
        AddTree(memory, memory.Map + 0x1c, rootKey: 100, childKey: 200, childIsLeft: false);
        AddEmptyTree(memory, memory.Map + 0x40);
        AddEmptyTree(memory, memory.Map + 0x34);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(maxNodes: 1),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.TraversalLimitExceeded, result.Status);
        Assert.AreEqual("hop-1:tree-traversal-0", result.FailureStage);
    }

    [TestMethod]
    public void Walk_EntityLookup_TreeValueNotPointer_ReturnsReadFailed()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);
        memory.WriteUInt32(memory.Map + 0x48, 0);
        AddTree(memory, memory.Map + 0x1c, rootKey: 4242, value: 0x1000); // not a pointer

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.ReadFailed, result.Status);
        Assert.AreEqual("hop-1:tree-value-0", result.FailureStage);
    }

    [TestMethod]
    public void Walk_EntityLookup_WithoutEntityId_IsInvalidChain()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            EntityLookupChain(),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("entity-id-required", result.FailureStage);
    }

    [TestMethod]
    public void Walk_EntityLookup_EmptyTreeRoots_IsInvalidChain()
    {
        var memory = new MemoryFixture();
        memory.WriteUInt32(ModuleBase + RootRva, memory.Map);

        OffsetEntityLookupDescriptor bad = Lookup() with { TreeRootOffsets = [] };
        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
                new OffsetChainHop(OffsetChainHopKind.EntityLookup, 0, null, EntityLookup: bad),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x10, null),
            ],
            ModuleBase,
            valueLength: 4,
            memory.Read,
            entityId: 4242);

        Assert.AreEqual(OffsetChainWalkStatus.InvalidChain, result.Status);
        Assert.AreEqual("hop-1", result.FailureStage);
    }

    private static IReadOnlyList<OffsetChainHop> Chain(params int[] memberOffsets) =>
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
            .. memberOffsets.Select(offset => new OffsetChainHop(OffsetChainHopKind.MemberOffset, offset, null)),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x30, null),
        ];

    private static IReadOnlyList<OffsetChainHop> RingChain() =>
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, 0x08, null),
            new OffsetChainHop(
                OffsetChainHopKind.RingIndex,
                0x08,
                null,
                IndexOffset: 0x1C8,
                Stride: 0x38),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x10, null),
        ];

    private static IReadOnlyList<OffsetChainHop> EntityLookupChain(int maxNodes = 1024) =>
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
            new OffsetChainHop(
                OffsetChainHopKind.EntityLookup,
                0,
                null,
                EntityLookup: Lookup(maxNodes)),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, 0x20, null),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x10, null),
        ];

    private static OffsetEntityLookupDescriptor Lookup(int maxNodes = 1024) => new(
        CachedEntityOffset: 0x48,
        EntityIdOffset: 0x1c,
        TreeRootOffsets: [0x1c, 0x40, 0x34],
        TreeNodeSize: 0x18,
        TreeNodeNilOffset: 0x0d,
        TreeNodeKeyOffset: 0x10,
        TreeNodeValueOffset: 0x14,
        TreeNodeChildLessOffset: 0x00,
        TreeNodeChildGreaterOffset: 0x08,
        TreeSentinelFirstNodeOffset: 0x04,
        MaxTreeNodes: maxNodes);

    private static void AddEmptyTree(MemoryFixture memory, uint treeObject)
    {
        uint sentinel = memory.Allocate(0x100);
        memory.WriteUInt32(treeObject, sentinel);
        memory.WriteUInt32(sentinel + 0x04, sentinel);
    }

    private static void AddTree(
        MemoryFixture memory,
        uint treeObject,
        int rootKey,
        int? childKey = null,
        bool childIsLeft = false,
        uint value = 0)
    {
        uint sentinel = memory.Allocate(0x100);
        uint root = memory.Allocate(0x100);
        uint child = childKey.HasValue ? memory.Allocate(0x100) : sentinel;
        memory.WriteUInt32(treeObject, sentinel);
        memory.WriteUInt32(sentinel + 0x04, root);
        WriteTreeNode(
            memory,
            root,
            left: childIsLeft ? child : sentinel,
            right: childIsLeft ? sentinel : child,
            key: rootKey,
            value: value != 0 ? value : memory.Entity);
        if (childKey.HasValue)
        {
            WriteTreeNode(
                memory,
                child,
                left: sentinel,
                right: sentinel,
                key: childKey.Value,
                value: value != 0 ? value : memory.Entity);
        }
    }

    private static void WriteTreeNode(
        MemoryFixture memory,
        uint address,
        uint left,
        uint right,
        int key,
        uint value)
    {
        Span<byte> empty = stackalloc byte[0x18];
        memory.Write(address, empty);
        memory.WriteUInt32(address, left);
        memory.WriteUInt32(address + 0x08, right);
        memory.WriteByte(address + 0x0d, 0);
        memory.WriteInt32(address + 0x10, key);
        memory.WriteUInt32(address + 0x14, value);
    }

    private sealed class MemoryFixture
    {
        private readonly Dictionary<uint, byte> _bytes = [];

        public uint Map { get; } = 0x20000000;
        public uint CachedEntity { get; } = 0x21000000;
        public uint Entity { get; } = 0x22000000;
        public uint ValueObject { get; } = 0x23000000;
        public uint Object1 { get; } = 0x24000000;
        public uint Object2 { get; } = 0x25000000;
        public uint Object3 { get; } = 0x26000000;

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

        public uint Allocate(uint size)
        {
            uint address = 0x30000000 + (uint)_bytes.Count;
            for (uint offset = 0; offset < size; offset++)
            {
                _bytes[address + offset] = 0;
            }

            return address;
        }

        // The walker reads the VALUE at the final record address, so X/Y/Z are
        // written at offsets 0/4/8 here (the +0x10 position-record layout is a
        // resolver concept exercised by the equivalence fixture, not here).
        public void WritePosition(uint record, float x, float y, float z)
        {
            Span<byte> bytes = stackalloc byte[0x38];
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x00..], BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x04..], BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x08..], BitConverter.SingleToInt32Bits(z));
            Write(record, bytes);
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

        public void WriteByte(uint address, byte value) => _bytes[address] = value;

        public void Write(uint address, ReadOnlySpan<byte> bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                _bytes[address + (uint)index] = bytes[index];
            }
        }
    }
}
