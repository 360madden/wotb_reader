using System.Buffers.Binary;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Tests;

/// <summary>
/// End-to-end proof of the 2nd-generation walkable position-chain form. The
/// SINGLE source of truth is the canonical draft file
/// <c>docs/operations/g0-walkable-position-chains.draft.json</c> (rendered in
/// <c>docs/operations/g0-offset-table-draft.md</c> §7 and validated by
/// <c>scripts/python/offset_check.py</c>). This test loads THAT file through
/// the real parse path (<c>OffsetTableReader</c>), then walks the parsed
/// chains on full-spine synthetic memory and requires the same record address
/// and X/Y/Z floats as the resolver's own traversal over the SAME memory. Any
/// drift between the canonical file and the resolver's layout constants — or
/// between the file and the operator-facing doc block (checked by the Python
/// gate) — fails here.
/// </summary>
[TestClass]
public sealed class WalkablePositionChainTests
{
    private const string Version = "11.19.0.10";
    private static readonly string Hash = new('a', ContentHash.Sha256HexLength);

    // The canonical draft file: gameVersion + the real 11.19.0.10 executable
    // hash it declares. Loading through OffsetTableReader pins the file's
    // identity to the exact analyzed binary. The version string matches the
    // filename (g0-walkable-position-chains.draft.json) — required by both
    // OffsetTableReader's {gameVersion}.json lookup and offset_check.py's
    // filename rule.
    private const string DraftVersion = "g0-walkable-position-chains.draft";
    private const string DraftHash =
        "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    private const uint ModuleBase = 0x10000000;
    private const int EntityId = 4242;

    private static Type10EntityPositionLayout Layout => Type10EntityPositionLayout.WotBlitz1119010;

    [TestMethod]
    public void Load_ParsesWalkableChains_ToResolverConstants()
    {
        OperationResult<OffsetTable?> result = LoadDraft();

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.IsNotNull(result.Value.Chains);
        Assert.HasCount(3, result.Value.Chains);

        Type10EntityPositionLayout layout = Layout;
        AssertChainEqual(ExpectedChain((int)layout.PositionRecordOffset), result.Value.Chains["playerPositionX"]);
        AssertChainEqual(ExpectedChain((int)layout.PositionRecordOffset + 4), result.Value.Chains["playerPositionY"]);
        AssertChainEqual(ExpectedChain((int)layout.PositionRecordOffset + 8), result.Value.Chains["playerPositionZ"]);
    }

    [TestMethod]
    public void Walk_ParsedWalkableChains_CachePath_MatchesResolver()
    {
        var memory = FullSpineFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);
        RunEquivalence(memory, 12.5f, -3.25f, 44.75f);
    }

    [TestMethod]
    public void Walk_ParsedWalkableChains_TreePath_MatchesResolver()
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
    public void Walk_ParsedWalkableChains_AlternativeTertiaryRoot_MatchesResolver()
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
    public void Walk_ParsedWalkableChains_EntityNotFound_WhenAllTreesEmpty()
    {
        OperationResult<OffsetTable?> result = LoadDraft();
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value?.Chains);

        var memory = FullSpineFixture.CreateEmptyMaps();

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            result.Value.Chains["playerPositionX"],
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, resolver.Status);
        Assert.AreEqual(OffsetChainWalkStatus.EntityNotFound, walker.Status);
        Assert.IsNull(resolver.RecordAddress);
    }

    [TestMethod]
    public void Load_MalformedEntityLookupDescriptor_DropsChainFailClosed()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, MalformedChainsJson);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        // The malformed chain is dropped per-field (fail-closed) — the other
        // fields' chains must survive.
        Assert.IsNotNull(result.Value.Chains);
        Assert.IsFalse(result.Value.Chains.ContainsKey("playerPositionX"));
        Assert.HasCount(2, result.Value.Chains);
    }

    [TestMethod]
    public void Walk_PublishedTableChains_CachePath_MatchesResolver()
    {
        // The PUBLISHED table (memory-offsets/11.19.0.10.json) carries the
        // walkable chains since OD-RECOVERY-084 — the walker must read the
        // published table directly, not just the canonical draft. Same
        // full-spine memory, same resolver comparison.
        var memory = FullSpineFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);
        RunEquivalenceOnPublished("playerPositionX", memory, 12.5f, -3.25f, 44.75f);
    }

    [TestMethod]
    public void Walk_PublishedTableChains_TreePath_MatchesResolver()
    {
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: EntityId,
            tertiaryKey: null,
            secondaryKey: null,
            x: 1f,
            y: 2f,
            z: 3f);
        RunEquivalenceOnPublished("playerPositionX", memory, 1f, 2f, 3f);
    }

    [TestMethod]
    public void Walk_PublishedTableChains_AlternativeTertiaryRoot_MatchesResolver()
    {
        var memory = FullSpineFixture.CreateTree(
            EntityId,
            primaryRootKey: null,
            tertiaryKey: EntityId,
            secondaryKey: null,
            x: 4f,
            y: 5f,
            z: 6f);
        RunEquivalenceOnPublished("playerPositionX", memory, 4f, 5f, 6f);
    }

    [TestMethod]
    public void Walk_PublishedTableChains_YAndZ_ReadTheSameRecord()
    {
        // Y/Z share the walkable chain with recordOffset 0x14/0x18. Walking
        // each published chain must land on the exact field addresses.
        var memory = FullSpineFixture.CreateCached(EntityId, 12.5f, -3.25f, 44.75f);
        OperationResult<OffsetTable?> result = LoadPublished();
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value?.Chains);

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        Assert.AreEqual(Type10EntityPositionStatus.Resolved, resolver.Status);
        Assert.IsNotNull(resolver.RecordAddress);
        nint record = (nint)resolver.RecordAddress.Value;

        foreach ((string field, int recordOffset) in new[]
                 {
                     ("playerPositionX", (int)Layout.PositionRecordOffset),
                     ("playerPositionY", (int)Layout.PositionRecordOffset + 4),
                     ("playerPositionZ", (int)Layout.PositionRecordOffset + 8),
                 })
        {
            OffsetChainWalkResult walker = OffsetChainWalker.Walk(
                result.Value.Chains[field],
                ModuleBase,
                valueLength: 4,
                memory.Read,
                entityId: EntityId);
            Assert.AreEqual(OffsetChainWalkStatus.Resolved, walker.Status, field);
            Assert.AreEqual(record + recordOffset, (nint)walker.Address, field);
            Assert.IsNotNull(walker.Bytes);
        }
    }

    [TestMethod]
    public void Walk_PublishedTableChains_EntityNotFound_WhenAllTreesEmpty()
    {
        OperationResult<OffsetTable?> result = LoadPublished();
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value?.Chains);

        var memory = FullSpineFixture.CreateEmptyMaps();

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            result.Value.Chains["playerPositionX"],
            ModuleBase,
            valueLength: 12,
            memory.Read,
            entityId: EntityId);

        Assert.AreEqual(Type10EntityPositionStatus.EntityNotFound, resolver.Status);
        Assert.AreEqual(OffsetChainWalkStatus.EntityNotFound, walker.Status);
        Assert.IsNull(resolver.RecordAddress);
    }

    private static void RunEquivalence(FullSpineFixture memory, float x, float y, float z)
    {
        OperationResult<OffsetTable?> result = LoadDraft();
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value?.Chains);
        RunEquivalence(result.Value, memory, x, y, z);
    }

    private static void RunEquivalenceOnPublished(
        string field, FullSpineFixture memory, float x, float y, float z)
    {
        OperationResult<OffsetTable?> result = LoadPublished();
        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value?.Chains);
        Assert.AreEqual("playerPositionX", field);
        RunEquivalence(result.Value, memory, x, y, z);
    }

    private static void RunEquivalence(
        OffsetTable table, FullSpineFixture memory, float x, float y, float z)
    {
        Assert.IsNotNull(table.Chains);

        Type10EntityPositionAddressResult resolver =
            Type10EntityPositionResolver.ResolveRecordAddress(
                ModuleBase,
                EntityId,
                Layout,
                memory.Read);
        OffsetChainWalkResult walker = OffsetChainWalker.Walk(
            table.Chains["playerPositionX"],
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
            // final FIELD address (record + recordOffset).
            Assert.AreEqual(
                resolver.RecordAddress.Value + Layout.PositionRecordOffset,
                walker.Address);
            Assert.IsNotNull(walker.Bytes);
            Assert.AreEqual(x, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(0)));
            Assert.AreEqual(y, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(4)));
            Assert.AreEqual(z, BinaryPrimitives.ReadSingleLittleEndian(walker.Bytes.AsSpan(8)));
        }
    }

    /// <summary>
    /// The expected chain, derived from the resolver's layout constants + the
    /// hardcoded tree-node layout. The draft JSON must parse to EXACTLY this.
    /// </summary>
    private static IReadOnlyList<OffsetChainHop> ExpectedChain(int recordOffset)
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
            new OffsetChainHop(
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
                    MaxTreeNodes: layout.MaxTreeNodes)),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.EntityMovementFilterOffset, null),
            new OffsetChainHop(OffsetChainHopKind.MemberOffset, (int)layout.AvatarFilterHelperOffset, null),
            new OffsetChainHop(
                OffsetChainHopKind.RingIndex,
                (int)layout.AvatarHelperRingOffset,
                null,
                IndexOffset: (int)layout.AvatarHelperCurrentIndexOffset,
                Stride: (int)layout.AvatarHelperRingStride),
            new OffsetChainHop(OffsetChainHopKind.RecordOffset, recordOffset, null),
        ];
    }

    private static void AssertChainEqual(
        IReadOnlyList<OffsetChainHop> expected,
        IReadOnlyList<OffsetChainHop> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int index = 0; index < expected.Count; index++)
        {
            OffsetChainHop e = expected[index];
            OffsetChainHop a = actual[index];
            Assert.AreEqual(e.Kind, a.Kind, $"hop {index} kind");
            Assert.AreEqual(e.Value, a.Value, $"hop {index} value");
            Assert.AreEqual(e.IndexOffset, a.IndexOffset, $"hop {index} indexOffset");
            Assert.AreEqual(e.Stride, a.Stride, $"hop {index} stride");
            Assert.AreEqual(e.EntityLookup is null, a.EntityLookup is null, $"hop {index} descriptor presence");
            if (e.EntityLookup is not null && a.EntityLookup is not null)
            {
                Assert.AreEqual(e.EntityLookup.CachedEntityOffset, a.EntityLookup.CachedEntityOffset, "cachedEntityOffset");
                Assert.AreEqual(e.EntityLookup.EntityIdOffset, a.EntityLookup.EntityIdOffset, "entityIdOffset");
                CollectionAssert.AreEqual(
                    e.EntityLookup.TreeRootOffsets.ToArray(),
                    a.EntityLookup.TreeRootOffsets.ToArray(),
                    "treeRootOffsets");
                Assert.AreEqual(e.EntityLookup.TreeNodeSize, a.EntityLookup.TreeNodeSize, "treeNodeSize");
                Assert.AreEqual(e.EntityLookup.TreeNodeNilOffset, a.EntityLookup.TreeNodeNilOffset, "treeNodeNilOffset");
                Assert.AreEqual(e.EntityLookup.TreeNodeKeyOffset, a.EntityLookup.TreeNodeKeyOffset, "treeNodeKeyOffset");
                Assert.AreEqual(e.EntityLookup.TreeNodeValueOffset, a.EntityLookup.TreeNodeValueOffset, "treeNodeValueOffset");
                Assert.AreEqual(e.EntityLookup.TreeNodeChildLessOffset, a.EntityLookup.TreeNodeChildLessOffset, "treeNodeChildLessOffset");
                Assert.AreEqual(e.EntityLookup.TreeNodeChildGreaterOffset, a.EntityLookup.TreeNodeChildGreaterOffset, "treeNodeChildGreaterOffset");
                Assert.AreEqual(e.EntityLookup.TreeSentinelFirstNodeOffset, a.EntityLookup.TreeSentinelFirstNodeOffset, "treeSentinelFirstNodeOffset");
                Assert.AreEqual(e.EntityLookup.MaxTreeNodes, a.EntityLookup.MaxTreeNodes, "maxTreeNodes");
            }
        }
    }

    /// <summary>
    /// Loads the CANONICAL draft file through the real parse path. The file's
    /// gameVersion and hash are fixed inputs, so any edit to the file that
    /// breaks identity or shape fails here.
    /// </summary>
    private static OperationResult<OffsetTable?> LoadDraft()
    {
        string draftDirectory = Path.Combine(RepoRoot(), "docs", "operations");
        return new OffsetTableReader(draftDirectory).Load(DraftVersion, DraftHash);
    }

    /// <summary>
    /// Loads the REAL published table (<c>memory-offsets/11.19.0.10.json</c>)
    /// with its true executable hash — the exact artifact the operator table
    /// ships. Since OD-RECOVERY-084 its chains are the walkable form, so this
    /// proves the walker reads the published table directly.
    /// </summary>
    private static OperationResult<OffsetTable?> LoadPublished()
    {
        string offsetsDirectory = Path.Combine(RepoRoot(), "memory-offsets");
        return new OffsetTableReader(offsetsDirectory).Load(Version, DraftHash);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WotBTreader.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    private static void WriteTable(string directory, string chainsJson)
    {
        string json =
            """
            {
              "schemaVersion": 1,
              "gameVersion": "11.19.0.10",
              "executableSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "discoveredAtUtc": "2026-08-10T00:00:00Z",
              "offsets": {
                "replayTime": 0,
                "playerHP": 0,
                "playerPositionX": 0,
                "playerPositionY": 0,
                "playerPositionZ": 0,                "playerYaw": 0,
                "cameraPitch": 0,
                "aliveTankCount": 0
              },
            """
            + chainsJson
            + """
              ,
              "confidence": "high",
              "notes": "walkable-form fixture"
            }
            """;
        File.WriteAllText(Path.Combine(directory, $"{Version}.json"), json);
    }

    /// <summary>Same table but with the X chain's entityLookup missing its
    /// required descriptor fields — the chain must be dropped fail-closed.</summary>
    private const string MalformedChainsJson =
        """
        "chains": {
          "playerPositionX": [
            { "kind": "rootRva", "value": 67722376, "note": "GameCoreRootRva" },
            { "kind": "entityLookup", "value": 0, "note": "missing treeRootOffsets and the rest of the descriptor" },
            { "kind": "recordOffset", "value": 16, "note": "X" }
          ],
          "playerPositionY": [
            { "kind": "rootRva", "value": 67722376, "note": "GameCoreRootRva" },
            { "kind": "memberOffset", "value": 12, "note": "GameCoreAppControllerOffset" },
            { "kind": "recordOffset", "value": 20, "note": "Y" }
          ],
          "playerPositionZ": [
            { "kind": "rootRva", "value": 67722376, "note": "GameCoreRootRva" },
            { "kind": "memberOffset", "value": 12, "note": "GameCoreAppControllerOffset" },
            { "kind": "recordOffset", "value": 24, "note": "Z" }
          ]
        }
        """;

    /// <summary>
    /// Full-spine synthetic memory mirroring the resolver's own fixture: every
    /// object the resolver validates (vtables) and reads (spine, entity maps,
    /// ring) at the layout's exact offsets. Cache/tree contents are
    /// caller-controlled so both readers exercise the same path.
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
        public uint SentinelA { get; } = 0x26000000;
        public uint SentinelB { get; } = 0x26200000;
        public uint SentinelC { get; } = 0x26400000;

        public static FullSpineFixture CreateCached(int entityId, float x, float y, float z)
        {
            var memory = new FullSpineFixture();
            memory.BuildSpine();
            memory.WriteUInt32(memory.Entities + 0x48, memory.Entity);
            memory.WriteInt32(memory.Entity + 0x1c, entityId);
            memory.WritePosition(x, y, z);
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
            AddTree(memory, memory.Entities + 0x1c, primaryRootKey, memory.SentinelA);
            AddTree(memory, memory.Entities + 0x40, tertiaryKey, memory.SentinelB);
            AddTree(memory, memory.Entities + 0x34, secondaryKey, memory.SentinelC);
            memory.WritePosition(x, y, z);
            return memory;
        }

        public static FullSpineFixture CreateEmptyMaps()
        {
            var memory = new FullSpineFixture();
            memory.BuildSpine();
            memory.WriteUInt32(memory.Entities + 0x48, 0);
            AddTree(memory, memory.Entities + 0x1c, null, memory.SentinelA);
            AddTree(memory, memory.Entities + 0x40, null, memory.SentinelB);
            AddTree(memory, memory.Entities + 0x34, null, memory.SentinelC);
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
            WriteUInt32(Filter, ModuleBase + layout.MovementFilterVtableRvas[1]);
            WriteUInt32(Helper, ModuleBase + layout.AvatarHelperVtableRvas[1]);
            WriteInt32(Helper + 0x1c8, 3);
        }

        private static void AddTree(FullSpineFixture memory, uint treeObject, int? rootKey, uint sentinel)
        {
            memory.WriteUInt32(treeObject, sentinel);
            if (rootKey.HasValue)
            {
                uint root = sentinel + 0x100000;
                memory.WriteUInt32(sentinel + 0x04, root);
                memory.WriteTreeNode(root, left: sentinel, right: sentinel, key: rootKey.Value);
            }
            else
            {
                // Empty tree: node == sentinel terminates the walk immediately.
                memory.WriteUInt32(sentinel + 0x04, sentinel);
            }
        }

        public void WriteTreeNode(uint address, uint left, uint right, int key)
        {
            Span<byte> empty = stackalloc byte[0x18];
            Write(address, empty);
            WriteUInt32(address, left);
            WriteUInt32(address + 0x08, right);
            WriteByte(address + 0x0d, 0);
            WriteInt32(address + 0x10, key);
            WriteUInt32(address + 0x14, Entity);
        }

        public void WritePosition(float x, float y, float z)
        {
            uint record = Helper + 0x08 + (3 * 0x38);
            Span<byte> bytes = stackalloc byte[0x38];
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x10..], BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x14..], BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[0x18..], BitConverter.SingleToInt32Bits(z));
            Write(record, bytes);
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WotBTreader.Application.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
