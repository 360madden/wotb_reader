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
        memory.WritePosition(memory.Object2 + 0x20 + 0x30, 12.5f, -3.25f, 44.75f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            Chain(0x10, 0x20),
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(memory.Object2 + 0x20 + 0x30, result.Address);
        Assert.IsNotNull(result.Bytes);
        Assert.AreEqual(12.5f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes));
        Assert.IsNull(result.FailureStage);
    }

    [TestMethod]
    public void Walk_RootThenRecordOffset_ReadsDirectly()
    {
        var memory = new MemoryFixture();
        memory.WritePosition(ModuleBase + RootRva + 0x08, 7f, 8f, 9f);

        OffsetChainWalkResult result = OffsetChainWalker.Walk(
            [
                new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
                new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x08, null),
            ],
            ModuleBase,
            valueLength: 4,
            memory.Read);

        Assert.AreEqual(OffsetChainWalkStatus.Resolved, result.Status);
        Assert.AreEqual(ModuleBase + RootRva + 0x08, result.Address);
        Assert.AreEqual(7f, BinaryPrimitives.ReadSingleLittleEndian(result.Bytes!));
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
        Assert.AreEqual("hop-2", result.FailureStage);
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
        Assert.AreEqual("hop-1", result.FailureStage);
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
    [DataRow(0)]
    [DataRow(9)]
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

    private static IReadOnlyList<OffsetChainHop> Chain(params int[] memberOffsets) =>
        [
            new OffsetChainHop(OffsetChainHopKind.RootRva, (int)RootRva, null),
            .. memberOffsets.Select(offset => new OffsetChainHop(OffsetChainHopKind.MemberOffset, offset, null)),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, 0x30, null),
        ];

    private sealed class MemoryFixture
    {
        private readonly Dictionary<uint, byte> _bytes = [];

        public uint Object1 { get; } = 0x20000000;
        public uint Object2 { get; } = 0x21000000;
        public uint Object3 { get; } = 0x22000000;

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

        public void WriteUInt32(uint address, uint value) =>
            Write(address, BitConverter.GetBytes(value));

        public void WritePosition(uint address, float x, float y, float z)
        {
            Span<byte> bytes = stackalloc byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(bytes, x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], z);
            Write(address, bytes.ToArray());
        }

        private void Write(uint address, byte[] bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                _bytes[address + (uint)index] = bytes[index];
            }
        }
    }
}
