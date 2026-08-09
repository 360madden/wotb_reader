using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class Type10EntityPositionResolverTests
{
    private const uint ModuleBase = 0x10000000;
    private const int EntityId = 4242;

    [TestMethod]
    public void Resolve_CachedEntity_ReturnsDoubleCollectedPosition()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual(EntityId, result.EntityId);
        Assert.AreEqual(12.5f, result.X);
        Assert.AreEqual(-3.25f, result.Y);
        Assert.AreEqual(44.75f, result.Z);
        Assert.AreEqual("cache", result.EntitySource);
        Assert.AreEqual(1, result.Attempts);
        Assert.AreEqual(0, result.NodesVisited);
        Assert.IsTrue(result.ModuleRooted);
        Assert.IsTrue(result.EntityIdentityRevalidated);
        Assert.IsTrue(result.ConsistentDoubleRead);
        Assert.IsFalse(result.HardwareAtomicReadProven);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Resolve_EachProvenFilterHelperPair_ReturnsPosition(int subtypeIndex)
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 7f, 8f, 9f);
        memory.UseSubtypePair(subtypeIndex);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual(7f, result.X);
        Assert.AreEqual(8f, result.Y);
        Assert.AreEqual(9f, result.Z);
    }

    [TestMethod]
    public void Resolve_ReadsPositionInsteadOfAdjacentVelocity()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 7f, 8f, 9f);
        memory.WriteVelocity(70f, 80f, 90f);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual(7f, result.X);
        Assert.AreEqual(8f, result.Y);
        Assert.AreEqual(9f, result.Z);
    }

    [TestMethod]
    public void Resolve_MismatchedProvenFilterAndHelperSubtypes_StopsBeforeRingRead()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 7f, 8f, 9f);
        Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
        memory.WriteUInt32(
            memory.Helper,
            ModuleBase + layout.AvatarHelperVtableRvas[2]);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedMovementFilter, result.Status);
        Assert.AreEqual("avatar-helper-vtable", result.FailureStage);
        Assert.AreEqual(1, result.Attempts);
    }

    [TestMethod]
    public void Resolve_PrimaryTree_UsesSignedKeyTraversal()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.Entities + 0x48, 0);
        memory.AddTree(
            treeObject: memory.Entities + 0x1c,
            rootKey: EntityId + 100,
            childKey: EntityId,
            childIsLeft: true);
        memory.AddEmptyTree(memory.Entities + 0x40);
        memory.AddEmptyTree(memory.Entities + 0x34);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual("primary", result.EntitySource);
        Assert.AreEqual(2, result.NodesVisited);
    }

    [TestMethod]
    public void Resolve_FallsBackInGameOrder()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 4f, 5f, 6f);
        memory.WriteUInt32(memory.Entities + 0x48, 0);
        memory.AddEmptyTree(memory.Entities + 0x1c);
        memory.AddTree(
            treeObject: memory.Entities + 0x40,
            rootKey: EntityId,
            childKey: null,
            childIsLeft: false);
        memory.AddEmptyTree(memory.Entities + 0x34);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual("tertiary", result.EntitySource);
        Assert.AreEqual(1, result.NodesVisited);
    }

    [TestMethod]
    public void Resolve_AllMapsEmpty_ReturnsHonestNotFound()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.Entities + 0x48, 0);
        memory.AddEmptyTree(memory.Entities + 0x1c);
        memory.AddEmptyTree(memory.Entities + 0x40);
        memory.AddEmptyTree(memory.Entities + 0x34);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, result.Status);
        Assert.AreEqual("entity-maps", result.FailureStage);
        Assert.IsTrue(result.ModuleRooted);
        Assert.IsNull(result.X);
    }

    [TestMethod]
    public void Resolve_UnexpectedFilterVtable_FailsClosed()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.Filter, ModuleBase + 0x1234);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedMovementFilter, result.Status);
        Assert.AreEqual("movement-filter-vtable", result.FailureStage);
        Assert.AreEqual(1, result.Attempts);
    }

    [TestMethod]
    public void Resolve_NonPlaybackActiveController_FailsClosedBeforeEntityRead()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.PlaybackController, ModuleBase + 0x1234);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedReplayController, result.Status);
        Assert.AreEqual("playback-controller-vtable", result.FailureStage);
        Assert.AreEqual(1, result.Attempts);
        Assert.IsTrue(result.ModuleRooted);
    }

    [TestMethod]
    public void Resolve_UnexpectedAppControllerVtable_FailsClosedBeforeSessionRead()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.AppController, ModuleBase + 0x1234);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedAppController, result.Status);
        Assert.AreEqual("app-controller-vtable", result.FailureStage);
        Assert.AreEqual(1, result.Attempts);
        Assert.IsTrue(result.ModuleRooted);
    }

    [TestMethod]
    public void Resolve_UnexpectedSessionControllerVtable_FailsClosedBeforeAccountRead()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.SessionController, ModuleBase + 0x1234);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.UnsupportedSessionController, result.Status);
        Assert.AreEqual("session-controller-vtable", result.FailureStage);
        Assert.AreEqual(1, result.Attempts);
        Assert.IsTrue(result.ModuleRooted);
    }

    [TestMethod]
    public void Resolve_RecordChangesDuringFirstCollect_RetriesAndSucceeds()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        int recordReads = 0;
        memory.BeforeRead = (address, length) =>
        {
            if (address == memory.Record && length == 0x38 && ++recordReads == 2)
            {
                memory.WritePosition(memory.Record, 10f, 20f, 30f);
            }
        };

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.Resolved, result.Status);
        Assert.AreEqual(2, result.Attempts);
        Assert.AreEqual(10f, result.X);
        Assert.AreEqual(20f, result.Y);
        Assert.AreEqual(30f, result.Z);
    }

    [TestMethod]
    public void Resolve_NonFinitePosition_RemainsFailureAfterBoundedRetries()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, float.NaN, 2f, 3f);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.NonFinitePosition, result.Status);
        Assert.AreEqual(3, result.Attempts);
        Assert.AreEqual("position-values", result.FailureStage);
    }

    [TestMethod]
    public void Resolve_TreeCycle_StopsAtTraversalGuard()
    {
        MemoryFixture memory = MemoryFixture.CreateCached(EntityId, 1f, 2f, 3f);
        memory.WriteUInt32(memory.Entities + 0x48, 0);
        uint sentinel = 0x28000000;
        uint node = 0x28000100;
        memory.WriteUInt32(memory.Entities + 0x1c, sentinel);
        memory.WriteUInt32(sentinel + 0x04, node);
        memory.WriteTreeNode(node, left: node, right: sentinel, key: EntityId + 1, value: memory.Entity);
        memory.AddEmptyTree(memory.Entities + 0x40);
        memory.AddEmptyTree(memory.Entities + 0x34);

        Type10EntityPositionResult result = Resolve(memory);

        Assert.AreEqual(Type10EntityPositionStatus.TraversalLimitExceeded, result.Status);
        Assert.AreEqual("tree-traversal", result.FailureStage);
    }

    [TestMethod]
    public void Resolve_ModuleArithmeticOverflow_IsRejectedBeforeRead()
    {
        int reads = 0;
        EntityPositionMemoryReader reader = (_, _) =>
        {
            reads++;
            return false;
        };

        Type10EntityPositionResult result = Type10EntityPositionResolver.Resolve(
            0xff000000,
            EntityId,
            Type10EntityPositionLayout.WotBlitz1119010,
            reader);

        Assert.AreEqual(Type10EntityPositionStatus.InvalidModuleBase, result.Status);
        Assert.AreEqual(0, reads);
    }

    [TestMethod]
    public void Resolve_MalformedLayout_IsRejectedBeforeRead()
    {
        Type10EntityPositionLayout invalid = Type10EntityPositionLayout.WotBlitz1119010 with
        {
            EntityTreeObjectOffsets = [0x1c, 0x40],
        };
        int reads = 0;

        Type10EntityPositionResult result = Type10EntityPositionResolver.Resolve(
            ModuleBase,
            EntityId,
            invalid,
            (_, _) =>
            {
                reads++;
                return false;
            });

        Assert.AreEqual(Type10EntityPositionStatus.InvalidLayout, result.Status);
        Assert.AreEqual(0, reads);
    }

    private static Type10EntityPositionResult Resolve(MemoryFixture memory) =>
        Type10EntityPositionResolver.Resolve(
            ModuleBase,
            EntityId,
            Type10EntityPositionLayout.WotBlitz1119010,
            memory.Read);

    private sealed class MemoryFixture
    {
        private readonly Dictionary<uint, byte> _bytes = [];
        private uint _nextTreeAddress = 0x26000000;

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
        public Action<uint, int>? BeforeRead { get; set; }

        public static MemoryFixture CreateCached(int entityId, float x, float y, float z)
        {
            var memory = new MemoryFixture();
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            uint root = ModuleBase + layout.GameCoreRootRva;
            memory.WriteUInt32(root, memory.GameCore);
            memory.WriteUInt32(memory.GameCore + layout.GameCoreAppControllerOffset, memory.AppController);
            memory.WriteUInt32(memory.AppController, ModuleBase + layout.AppControllerVtableRva);
            memory.WriteUInt32(
                memory.AppController + layout.AppControllerSessionControllerOffset,
                memory.SessionController);
            memory.WriteUInt32(memory.SessionController, ModuleBase + layout.SessionControllerVtableRva);
            memory.WriteUInt32(
                memory.SessionController + layout.SessionControllerAccountControllerOffset,
                memory.AccountController);
            memory.WriteUInt32(
                memory.AccountController,
                ModuleBase + layout.AccountControllerVtableRva);
            memory.WriteUInt32(
                memory.AccountController + layout.AccountControllerActiveControllerOffset,
                memory.PlaybackController);
            memory.WriteUInt32(
                memory.PlaybackController,
                ModuleBase + layout.PlaybackControllerVtableRva);
            memory.WriteUInt32(
                memory.PlaybackController + layout.PlaybackControllerConnectionOffset,
                memory.Connection);
            memory.WriteUInt32(memory.Entities + 0x48, memory.Entity);
            memory.WriteInt32(memory.Entity + 0x1c, entityId);
            memory.WriteUInt32(memory.Entity + 0x38, memory.Filter);
            memory.WriteUInt32(memory.Filter + 0x08, memory.Helper);
            memory.UseSubtypePair(1);
            memory.WriteInt32(memory.Helper + 0x1c8, 3);
            memory.WritePosition(memory.Record, x, y, z);
            return memory;
        }

        public void UseSubtypePair(int subtypeIndex)
        {
            Type10EntityPositionLayout layout = Type10EntityPositionLayout.WotBlitz1119010;
            WriteUInt32(
                Filter,
                ModuleBase + layout.MovementFilterVtableRvas[subtypeIndex]);
            WriteUInt32(
                Helper,
                ModuleBase + layout.AvatarHelperVtableRvas[subtypeIndex]);
        }

        public bool Read(uint address, Span<byte> destination)
        {
            BeforeRead?.Invoke(address, destination.Length);
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

        public void AddEmptyTree(uint treeObject)
        {
            uint sentinel = Allocate(0x100);
            WriteUInt32(treeObject, sentinel);
            WriteUInt32(sentinel + 0x04, sentinel);
        }

        public void AddTree(uint treeObject, int rootKey, int? childKey, bool childIsLeft)
        {
            uint sentinel = Allocate(0x100);
            uint root = Allocate(0x100);
            uint child = childKey.HasValue ? Allocate(0x100) : sentinel;
            WriteUInt32(treeObject, sentinel);
            WriteUInt32(sentinel + 0x04, root);
            WriteTreeNode(
                root,
                left: childIsLeft ? child : sentinel,
                right: childIsLeft ? sentinel : child,
                key: rootKey,
                value: Entity);
            if (childKey.HasValue)
            {
                WriteTreeNode(
                    child,
                    left: sentinel,
                    right: sentinel,
                    key: childKey.Value,
                    value: Entity);
            }
        }

        public void WriteTreeNode(uint address, uint left, uint right, int key, uint value)
        {
            Span<byte> empty = stackalloc byte[0x18];
            Write(address, empty);
            WriteUInt32(address, left);
            WriteUInt32(address + 0x04, 0);
            WriteUInt32(address + 0x08, right);
            WriteByte(address + 0x0c, 0);
            WriteByte(address + 0x0d, 0);
            WriteInt32(address + 0x10, key);
            WriteUInt32(address + 0x14, value);
        }

        public void WritePosition(uint record, float x, float y, float z)
        {
            Span<byte> bytes = stackalloc byte[0x38];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(100.0));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x10..], BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x14..], BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x18..], BitConverter.SingleToInt32Bits(z));
            Write(record, bytes);
        }

        public void WriteVelocity(float x, float y, float z)
        {
            WriteSingle(Record + 0x28, x);
            WriteSingle(Record + 0x2c, y);
            WriteSingle(Record + 0x30, z);
        }

        public void WriteUInt32(uint address, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(address, bytes);
        }

        private void WriteInt32(uint address, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(address, bytes);
        }

        private void WriteSingle(uint address, float value) =>
            WriteInt32(address, BitConverter.SingleToInt32Bits(value));

        private void WriteByte(uint address, byte value) => _bytes[address] = value;

        private void Write(uint address, ReadOnlySpan<byte> bytes)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                _bytes[address + (uint)index] = bytes[index];
            }
        }

        private uint Allocate(uint size)
        {
            uint address = _nextTreeAddress;
            _nextTreeAddress += size;
            return address;
        }
    }
}
