using System.Buffers.Binary;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// Supplies one exact, bounded memory read. Implementations must return false
/// unless the complete destination was filled.
/// </summary>
public delegate bool EntityPositionMemoryReader(uint address, Span<byte> destination);

/// <summary>
/// Hash-bound x86 layout for resolving a replay-owned entity by ID and reading
/// the newest position record retained by its verified movement-filter helper.
/// </summary>
public sealed record Type10EntityPositionLayout(
    string GameVersion,
    string ExecutableSha256,
    uint GameCoreRootRva,
    uint GameCoreAppControllerOffset,
    uint AppControllerVtableRva,
    uint AppControllerSessionControllerOffset,
    uint SessionControllerVtableRva,
    uint SessionControllerAccountControllerOffset,
    uint AccountControllerVtableRva,
    uint AccountControllerActiveControllerOffset,
    uint PlaybackControllerVtableRva,
    uint PlaybackControllerConnectionOffset,
    uint ConnectionEntitiesOffset,
    uint CachedEntityOffset,
    IReadOnlyList<uint> EntityTreeObjectOffsets,
    uint EntityIdOffset,
    uint EntityMovementFilterOffset,
    IReadOnlyList<uint> MovementFilterVtableRvas,
    uint AvatarFilterHelperOffset,
    IReadOnlyList<uint> AvatarHelperVtableRvas,
    uint AvatarHelperCurrentIndexOffset,
    uint AvatarHelperRingOffset,
    uint AvatarHelperRingStride,
    uint PositionRecordOffset,
    int RingEntryCount,
    int MaxTreeNodes,
    int MaxAttempts)
{
    /// <summary>Static layout verified for the exact 11.19.0.10 executable.</summary>
    public static Type10EntityPositionLayout WotBlitz1119010 { get; } = new(
        GameVersion: "11.19.0.10",
        ExecutableSha256: "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d",
        GameCoreRootRva: 0x04095c88,
        GameCoreAppControllerOffset: 0x0c,
        AppControllerVtableRva: 0x0323d61c,
        AppControllerSessionControllerOffset: 0x124,
        SessionControllerVtableRva: 0x0323d9bc,
        SessionControllerAccountControllerOffset: 0x118,
        AccountControllerVtableRva: 0x0323eae4,
        AccountControllerActiveControllerOffset: 0x128,
        PlaybackControllerVtableRva: 0x03253aa4,
        PlaybackControllerConnectionOffset: 0x120,
        ConnectionEntitiesOffset: 0x04,
        CachedEntityOffset: 0x48,
        EntityTreeObjectOffsets: [0x1c, 0x40, 0x34],
        EntityIdOffset: 0x1c,
        EntityMovementFilterOffset: 0x38,
        MovementFilterVtableRvas: [0x0325654c, 0x032565ac, 0x03442520],
        AvatarFilterHelperOffset: 0x08,
        AvatarHelperVtableRvas: [0x0325656c, 0x0325658c, 0x034424a4],
        AvatarHelperCurrentIndexOffset: 0x1c8,
        AvatarHelperRingOffset: 0x08,
        AvatarHelperRingStride: 0x38,
        PositionRecordOffset: 0x10,
        RingEntryCount: 8,
        MaxTreeNodes: 1024,
        MaxAttempts: 3);
}

/// <summary>Outcome of one bounded module-rooted entity-position resolution.</summary>
public enum Type10EntityPositionStatus
{
    Resolved,
    UnsupportedBuild,
    InvalidLayout,
    InvalidModuleBase,
    ReadFailed,
    UnsupportedAppController,
    ReplaySessionInactive,
    UnsupportedSessionController,
    UnsupportedAccountController,
    ReplayControllerInactive,
    UnsupportedReplayController,
    EntityNotFound,
    EntityIdentityMismatch,
    UnsupportedMovementFilter,
    InvalidRingIndex,
    NonFinitePosition,
    UnstableSnapshot,
    TraversalLimitExceeded,
}

/// <summary>
/// Sanitized result of a resolver attempt. Runtime addresses are deliberately
/// omitted so this record can be projected without leaking process locations.
/// </summary>
public sealed record Type10EntityPositionResult(
    Type10EntityPositionStatus Status,
    int EntityId,
    float? X,
    float? Y,
    float? Z,
    string? EntitySource,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted,
    bool EntityIdentityRevalidated,
    bool ConsistentDoubleRead,
    bool HardwareAtomicReadProven);

/// <summary>
/// Diagnostic projection of a resolver traversal: the ring-record address and
/// its page for the requested entity, when resolved. Deliberately NOT part of
/// <see cref="Type10EntityPositionResult"/> so poll results never leak process
/// locations; only the gate-verified position-page endpoint uses this record,
/// to arm the guard-page interceptor on the exact page a poll reads.
/// </summary>
public sealed record Type10EntityPositionAddressResult(
    Type10EntityPositionStatus Status,
    uint? RecordAddress,
    uint? PageAddress,
    string? FailureStage,
    int Attempts,
    int NodesVisited,
    bool ModuleRooted);

/// <summary>
/// Pure x86 resolver for the hash-bound type-10 entity family. It follows a
/// module root, searches the same three BWEntities maps as the game, validates
/// the entity/filter/helper identities, and double-collects the newest ring
/// record. It performs no IO or Win32 calls and never claims hardware atomicity.
/// </summary>
public static class Type10EntityPositionResolver
{
    private const int TreeNodeSize = 0x18;
    private const int TreeNodeNilOffset = 0x0d;
    private const int TreeNodeKeyOffset = 0x10;
    private const int TreeNodeValueOffset = 0x14;
    private const int RingRecordSize = 0x38;

    /// <summary>
    /// Resolves <paramref name="entityId"/> against the supplied process view.
    /// A successful result is module-rooted and identity-revalidated, but the
    /// 12-byte position is still an optimistic double-read, not an atomic read.
    /// </summary>
    public static Type10EntityPositionResult Resolve(
        uint moduleBase,
        int entityId,
        Type10EntityPositionLayout layout,
        EntityPositionMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reader);

        AttemptLoopResult loop = RunAttemptLoop(moduleBase, entityId, layout, reader);
        if (loop.Final.Status == Type10EntityPositionStatus.Resolved)
        {
            return new Type10EntityPositionResult(
                loop.Final.Status,
                entityId,
                loop.Final.X,
                loop.Final.Y,
                loop.Final.Z,
                loop.Final.EntitySource,
                null,
                loop.Attempts,
                loop.TotalNodesVisited,
                ModuleRooted: true,
                EntityIdentityRevalidated: true,
                ConsistentDoubleRead: true,
                HardwareAtomicReadProven: false);
        }

        return Failure(
            loop.Final.Status,
            entityId,
            loop.Final.FailureStage,
            loop.Attempts,
            loop.TotalNodesVisited,
            loop.Final.EntitySource,
            loop.Final.ModuleRooted);
    }

    /// <summary>
    /// Diagnostic-only entry: runs the same traversal as <see cref="Resolve"/>
    /// (same module root, same entity/filter/helper identities, same ring
    /// selection) and returns the ring-record address and its page instead of
    /// the position bytes. Used by the gate-verified position-page endpoint so
    /// the guard-page interceptor can arm the exact page a poll reads. The
    /// address is deliberately NOT part of <see cref="Type10EntityPositionResult"/>
    /// and never lands in poll results or persisted aggregates.
    /// </summary>
    public static Type10EntityPositionAddressResult ResolveRecordAddress(
        uint moduleBase,
        int entityId,
        Type10EntityPositionLayout layout,
        EntityPositionMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(reader);

        AttemptLoopResult loop = RunAttemptLoop(moduleBase, entityId, layout, reader);
        if (loop.Final.Status == Type10EntityPositionStatus.Resolved &&
            loop.Final.RecordAddress is uint record)
        {
            return new Type10EntityPositionAddressResult(
                Type10EntityPositionStatus.Resolved,
                record,
                record & ~0xFFFu,
                null,
                loop.Attempts,
                loop.TotalNodesVisited,
                ModuleRooted: true);
        }

        return new Type10EntityPositionAddressResult(
            loop.Final.Status,
            null,
            null,
            loop.Final.FailureStage,
            loop.Attempts,
            loop.TotalNodesVisited,
            loop.Final.ModuleRooted);
    }

    private static AttemptLoopResult RunAttemptLoop(
        uint moduleBase,
        int entityId,
        Type10EntityPositionLayout layout,
        EntityPositionMemoryReader reader)
    {
        if (!IsValid(layout))
        {
            return AttemptLoopResult.Failed(
                Type10EntityPositionStatus.InvalidLayout,
                "layout",
                attempts: 0,
                nodesVisited: 0);
        }

        if (!IsPointer(moduleBase) ||
            !TryAdd(moduleBase, layout.GameCoreRootRva, out uint rootAddress) ||
            !TryAdd(moduleBase, layout.AppControllerVtableRva, out uint expectedAppVtable) ||
            !TryAdd(moduleBase, layout.SessionControllerVtableRva, out uint expectedSessionVtable) ||
            !TryAdd(moduleBase, layout.AccountControllerVtableRva, out uint expectedAccountVtable) ||
            !TryAdd(moduleBase, layout.PlaybackControllerVtableRva, out uint expectedPlaybackVtable) ||
            !TryResolveModuleAddresses(
                moduleBase,
                layout.MovementFilterVtableRvas,
                out uint[] expectedFilterVtables) ||
            !TryResolveModuleAddresses(
                moduleBase,
                layout.AvatarHelperVtableRvas,
                out uint[] expectedHelperVtables))
        {
            return AttemptLoopResult.Failed(
                Type10EntityPositionStatus.InvalidModuleBase,
                "module-base",
                attempts: 0,
                nodesVisited: 0);
        }

        AttemptResult? last = null;
        int lastAttempt = 0;
        int totalNodesVisited = 0;
        for (int attempt = 1; attempt <= layout.MaxAttempts; attempt++)
        {
            AttemptResult current = TryResolveOnce(
                rootAddress,
                expectedAppVtable,
                expectedSessionVtable,
                expectedAccountVtable,
                expectedPlaybackVtable,
                expectedFilterVtables,
                expectedHelperVtables,
                entityId,
                layout,
                reader);
            totalNodesVisited += current.NodesVisited;
            last = current;
            lastAttempt = attempt;

            if (current.Status == Type10EntityPositionStatus.Resolved ||
                !current.Retryable)
            {
                break;
            }
        }

        if (last is null)
        {
            // Defensive: a valid layout always allows at least one attempt.
            return AttemptLoopResult.Failed(
                Type10EntityPositionStatus.UnstableSnapshot,
                "attempts",
                layout.MaxAttempts,
                0);
        }

        return new AttemptLoopResult(last, lastAttempt, totalNodesVisited);
    }

    private sealed record AttemptLoopResult(
        AttemptResult Final,
        int Attempts,
        int TotalNodesVisited)
    {
        public static AttemptLoopResult Failed(
            Type10EntityPositionStatus status,
            string stage,
            int attempts,
            int nodesVisited) => new(
                AttemptResult.Stop(status, stage, nodesVisited, moduleRooted: false),
                attempts,
                nodesVisited);
    }

    private static AttemptResult TryResolveOnce(
        uint rootAddress,
        uint expectedAppVtable,
        uint expectedSessionVtable,
        uint expectedAccountVtable,
        uint expectedPlaybackVtable,
        uint[] expectedFilterVtables,
        uint[] expectedHelperVtables,
        int entityId,
        Type10EntityPositionLayout layout,
        EntityPositionMemoryReader reader)
    {
        if (!TryReadPointer(reader, rootAddress, out uint gameCore) ||
            !TryReadPointerAt(
                reader,
                gameCore,
                layout.GameCoreAppControllerOffset,
                out uint appController) ||
            !TryReadUInt32(reader, appController, out uint appVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "root-chain",
                moduleRooted: false);
        }

        if (appVtable != expectedAppVtable)
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedAppController,
                "app-controller-vtable");
        }

        if (!TryReadPointerAt(
                reader,
                appController,
                layout.AppControllerSessionControllerOffset,
                out uint sessionController))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReplaySessionInactive,
                "session-controller");
        }

        if (!TryReadUInt32(reader, sessionController, out uint sessionVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "session-controller-vtable");
        }

        if (sessionVtable != expectedSessionVtable)
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedSessionController,
                "session-controller-vtable");
        }

        if (!TryReadPointerAt(
                reader,
                sessionController,
                layout.SessionControllerAccountControllerOffset,
                out uint accountController) ||
            !TryReadUInt32(reader, accountController, out uint accountVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "account-controller");
        }

        if (accountVtable != expectedAccountVtable)
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedAccountController,
                "account-controller-vtable");
        }

        if (!TryReadPointerAt(
                reader,
                accountController,
                layout.AccountControllerActiveControllerOffset,
                out uint playbackController))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "active-controller");
        }

        if (playbackController == 0)
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReplayControllerInactive,
                "active-controller");
        }

        if (!TryReadUInt32(reader, playbackController, out uint playbackVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "playback-controller-vtable");
        }

        if (playbackVtable != expectedPlaybackVtable)
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedReplayController,
                "playback-controller-vtable");
        }

        if (!TryReadPointerAt(
                reader,
                playbackController,
                layout.PlaybackControllerConnectionOffset,
                out uint connection) ||
            !TryAdd(connection, layout.ConnectionEntitiesOffset, out uint entities) ||
            !IsPointer(entities))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "replay-connection");
        }

        EntityLookup lookup = FindEntity(reader, entities, entityId, layout);
        if (lookup.Status != Type10EntityPositionStatus.Resolved)
        {
            return lookup.Retryable
                ? AttemptResult.Retry(
                    lookup.Status,
                    lookup.FailureStage,
                    lookup.NodesVisited,
                    lookup.Source)
                : AttemptResult.Stop(
                    lookup.Status,
                    lookup.FailureStage,
                    lookup.NodesVisited,
                    lookup.Source);
        }

        uint entity = lookup.EntityAddress;
        if (!TryReadInt32At(reader, entity, layout.EntityIdOffset, out int observedEntityId))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "entity-id",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (observedEntityId != entityId)
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.EntityIdentityMismatch,
                "entity-id",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryReadPointerAt(
                reader,
                entity,
                layout.EntityMovementFilterOffset,
                out uint movementFilter) ||
            !TryReadUInt32(reader, movementFilter, out uint filterVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "movement-filter",
                lookup.NodesVisited,
                lookup.Source);
        }

        int filterSubtypeIndex = -1;
        for (int index = 0; index < expectedFilterVtables.Length; index++)
        {
            if (expectedFilterVtables[index] == filterVtable)
            {
                filterSubtypeIndex = index;
                break;
            }
        }

        if (filterSubtypeIndex < 0)
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedMovementFilter,
                "movement-filter-vtable",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryReadPointerAt(
                reader,
                movementFilter,
                layout.AvatarFilterHelperOffset,
                out uint helper) ||
            !TryReadUInt32(reader, helper, out uint helperVtable))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "avatar-helper",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (helperVtable != expectedHelperVtables[filterSubtypeIndex])
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.UnsupportedMovementFilter,
                "avatar-helper-vtable",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryReadInt32At(
                reader,
                helper,
                layout.AvatarHelperCurrentIndexOffset,
                out int indexBefore))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "ring-index",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (indexBefore < 0 || indexBefore >= layout.RingEntryCount)
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.InvalidRingIndex,
                "ring-index",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryMultiply((uint)indexBefore, layout.AvatarHelperRingStride, out uint recordDelta) ||
            !TryAdd(layout.AvatarHelperRingOffset, recordDelta, out uint recordOffset) ||
            !TryAdd(helper, recordOffset, out uint recordAddress))
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.InvalidLayout,
                "ring-address",
                lookup.NodesVisited,
                lookup.Source);
        }

        Span<byte> firstRecord = stackalloc byte[RingRecordSize];
        Span<byte> secondRecord = stackalloc byte[RingRecordSize];
        if (!reader(recordAddress, firstRecord) ||
            !TryReadInt32At(
                reader,
                helper,
                layout.AvatarHelperCurrentIndexOffset,
                out int indexMiddle) ||
            !reader(recordAddress, secondRecord) ||
            !TryReadInt32At(
                reader,
                helper,
                layout.AvatarHelperCurrentIndexOffset,
                out int indexAfter))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "ring-record",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (indexBefore != indexMiddle ||
            indexBefore != indexAfter ||
            !firstRecord.SequenceEqual(secondRecord))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.UnstableSnapshot,
                "ring-record",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryExtractPosition(firstRecord, layout.PositionRecordOffset, out float x, out float y, out float z))
        {
            return AttemptResult.Stop(
                Type10EntityPositionStatus.InvalidLayout,
                "position-offset",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.NonFinitePosition,
                "position-values",
                lookup.NodesVisited,
                lookup.Source);
        }

        if (!TryReadPointer(reader, rootAddress, out uint gameCoreAfter) ||
            gameCoreAfter != gameCore ||
            !TryReadPointerAt(
                reader,
                gameCore,
                layout.GameCoreAppControllerOffset,
                out uint appControllerAfter) ||
            appControllerAfter != appController ||
            !TryReadUInt32(reader, appController, out uint appVtableAfter) ||
            appVtableAfter != expectedAppVtable ||
            !TryReadPointerAt(
                reader,
                appController,
                layout.AppControllerSessionControllerOffset,
                out uint sessionControllerAfter) ||
            sessionControllerAfter != sessionController ||
            !TryReadUInt32(reader, sessionController, out uint sessionVtableAfter) ||
            sessionVtableAfter != expectedSessionVtable ||
            !TryReadPointerAt(
                reader,
                sessionController,
                layout.SessionControllerAccountControllerOffset,
                out uint accountControllerAfter) ||
            accountControllerAfter != accountController ||
            !TryReadUInt32(reader, accountController, out uint accountVtableAfter) ||
            accountVtableAfter != expectedAccountVtable ||
            !TryReadPointerAt(
                reader,
                accountController,
                layout.AccountControllerActiveControllerOffset,
                out uint playbackControllerAfter) ||
            playbackControllerAfter != playbackController ||
            !TryReadUInt32(reader, playbackController, out uint playbackVtableAfter) ||
            playbackVtableAfter != expectedPlaybackVtable ||
            !TryReadPointerAt(
                reader,
                playbackController,
                layout.PlaybackControllerConnectionOffset,
                out uint connectionAfter) ||
            connectionAfter != connection ||
            !TryReadInt32At(reader, entity, layout.EntityIdOffset, out int entityIdAfter) ||
            entityIdAfter != entityId ||
            !TryReadPointerAt(reader, entity, layout.EntityMovementFilterOffset, out uint filterAfter) ||
            filterAfter != movementFilter ||
            !TryReadPointerAt(reader, movementFilter, layout.AvatarFilterHelperOffset, out uint helperAfter) ||
            helperAfter != helper)
        {
            return AttemptResult.Retry(
                Type10EntityPositionStatus.UnstableSnapshot,
                "identity-revalidation",
                lookup.NodesVisited,
                lookup.Source);
        }

        return AttemptResult.Success(
            x,
            y,
            z,
            lookup.NodesVisited,
            lookup.Source,
            recordAddress);
    }

    private static EntityLookup FindEntity(
        EntityPositionMemoryReader reader,
        uint entities,
        int entityId,
        Type10EntityPositionLayout layout)
    {
        if (!TryReadPointerAt(reader, entities, layout.CachedEntityOffset, out uint cachedEntity))
        {
            return EntityLookup.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "cached-entity");
        }

        if (cachedEntity != 0)
        {
            if (!TryReadInt32At(reader, cachedEntity, layout.EntityIdOffset, out int cachedEntityId))
            {
                return EntityLookup.Retry(
                    Type10EntityPositionStatus.ReadFailed,
                    "cached-entity-id");
            }

            if (cachedEntityId == entityId)
            {
                return EntityLookup.Found(cachedEntity, "cache", nodesVisited: 0);
            }
        }

        string[] sources = ["primary", "tertiary", "secondary"];
        int totalNodesVisited = 0;
        for (int index = 0; index < layout.EntityTreeObjectOffsets.Count; index++)
        {
            if (!TryAdd(entities, layout.EntityTreeObjectOffsets[index], out uint treeObject))
            {
                return EntityLookup.Stop(
                    Type10EntityPositionStatus.InvalidLayout,
                    "tree-address",
                    totalNodesVisited,
                    sources[index]);
            }

            EntityLookup lookup = FindEntityInTree(
                reader,
                treeObject,
                entityId,
                layout.MaxTreeNodes,
                sources[index]);
            totalNodesVisited += lookup.NodesVisited;
            if (lookup.Status == Type10EntityPositionStatus.Resolved)
            {
                return lookup with { NodesVisited = totalNodesVisited };
            }

            if (lookup.Status != Type10EntityPositionStatus.EntityNotFound)
            {
                return lookup with { NodesVisited = totalNodesVisited };
            }
        }

        return EntityLookup.Stop(
            Type10EntityPositionStatus.EntityNotFound,
            "entity-maps",
            totalNodesVisited);
    }

    private static EntityLookup FindEntityInTree(
        EntityPositionMemoryReader reader,
        uint treeObject,
        int entityId,
        int maxNodes,
        string source)
    {
        if (!TryReadPointer(reader, treeObject, out uint sentinel) ||
            !TryReadPointerAt(reader, sentinel, 0x04, out uint node))
        {
            return EntityLookup.Retry(
                Type10EntityPositionStatus.ReadFailed,
                "tree-root",
                source: source);
        }

        var visited = new HashSet<uint>();
        int nodesVisited = 0;
        Span<byte> bytes = stackalloc byte[TreeNodeSize];
        while (node != sentinel)
        {
            if (!IsPointer(node))
            {
                return EntityLookup.Retry(
                    Type10EntityPositionStatus.ReadFailed,
                    "tree-node-pointer",
                    nodesVisited,
                    source);
            }

            if (!visited.Add(node) || nodesVisited >= maxNodes)
            {
                return EntityLookup.Stop(
                    Type10EntityPositionStatus.TraversalLimitExceeded,
                    "tree-traversal",
                    nodesVisited,
                    source);
            }

            bytes.Clear();
            if (!reader(node, bytes))
            {
                return EntityLookup.Retry(
                    Type10EntityPositionStatus.ReadFailed,
                    "tree-node",
                    nodesVisited,
                    source);
            }

            nodesVisited++;
            byte isNil = bytes[TreeNodeNilOffset];
            if (isNil == 1)
            {
                return EntityLookup.Stop(
                    Type10EntityPositionStatus.EntityNotFound,
                    "tree-nil",
                    nodesVisited,
                    source);
            }

            if (isNil != 0)
            {
                return EntityLookup.Retry(
                    Type10EntityPositionStatus.ReadFailed,
                    "tree-nil-flag",
                    nodesVisited,
                    source);
            }

            int key = BinaryPrimitives.ReadInt32LittleEndian(bytes[TreeNodeKeyOffset..]);
            if (entityId == key)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[TreeNodeValueOffset..]);
                if (!IsPointer(value))
                {
                    return EntityLookup.Retry(
                        Type10EntityPositionStatus.ReadFailed,
                        "tree-value",
                        nodesVisited,
                        source);
                }

                return EntityLookup.Found(value, source, nodesVisited);
            }

            int childOffset = entityId < key ? 0x00 : 0x08;
            node = BinaryPrimitives.ReadUInt32LittleEndian(bytes[childOffset..]);
        }

        return EntityLookup.Stop(
            Type10EntityPositionStatus.EntityNotFound,
            "tree-end",
            nodesVisited,
            source);
    }

    private static bool TryExtractPosition(
        ReadOnlySpan<byte> record,
        uint positionOffset,
        out float x,
        out float y,
        out float z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (positionOffset > (uint)(record.Length - 12))
        {
            return false;
        }

        int offset = checked((int)positionOffset);
        x = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(record[offset..]));
        y = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 4)..]));
        z = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 8)..]));
        return true;
    }

    private static bool TryReadPointerAt(
        EntityPositionMemoryReader reader,
        uint address,
        uint offset,
        out uint value)
    {
        value = 0;
        return TryAdd(address, offset, out uint target) &&
            TryReadPointer(reader, target, out value);
    }

    private static bool TryReadInt32At(
        EntityPositionMemoryReader reader,
        uint address,
        uint offset,
        out int value)
    {
        value = 0;
        return TryAdd(address, offset, out uint target) &&
            TryReadInt32(reader, target, out value);
    }

    private static bool TryReadPointer(
        EntityPositionMemoryReader reader,
        uint address,
        out uint value)
    {
        if (!TryReadUInt32(reader, address, out value))
        {
            return false;
        }

        return value == 0 || IsPointer(value);
    }

    private static bool TryReadUInt32(
        EntityPositionMemoryReader reader,
        uint address,
        out uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!reader(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryReadInt32(
        EntityPositionMemoryReader reader,
        uint address,
        out int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!reader(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool IsValid(Type10EntityPositionLayout layout)
    {
        return !string.IsNullOrWhiteSpace(layout.GameVersion) &&
            layout.ExecutableSha256.Length == 64 &&
            layout.GameCoreRootRva != 0 &&
            layout.GameCoreAppControllerOffset != 0 &&
            layout.AppControllerVtableRva != 0 &&
            layout.AppControllerSessionControllerOffset != 0 &&
            layout.SessionControllerVtableRva != 0 &&
            layout.SessionControllerAccountControllerOffset != 0 &&
            layout.AccountControllerVtableRva != 0 &&
            layout.AccountControllerActiveControllerOffset != 0 &&
            layout.PlaybackControllerVtableRva != 0 &&
            layout.PlaybackControllerConnectionOffset != 0 &&
            layout.EntityTreeObjectOffsets.Count == 3 &&
            layout.EntityTreeObjectOffsets.All(offset => offset != 0) &&
            layout.MovementFilterVtableRvas.Count == 3 &&
            layout.MovementFilterVtableRvas.Distinct().Count() == 3 &&
            layout.MovementFilterVtableRvas.All(rva => rva != 0) &&
            layout.AvatarHelperVtableRvas.Count == 3 &&
            layout.AvatarHelperVtableRvas.Distinct().Count() == 3 &&
            layout.AvatarHelperVtableRvas.All(rva => rva != 0) &&
            layout.RingEntryCount == 8 &&
            layout.AvatarHelperRingStride == RingRecordSize &&
            layout.AvatarHelperRingOffset != 0 &&
            (ulong)layout.AvatarHelperRingOffset +
                ((ulong)layout.RingEntryCount * layout.AvatarHelperRingStride) ==
                layout.AvatarHelperCurrentIndexOffset &&
            layout.PositionRecordOffset <= (uint)(RingRecordSize - 12) &&
            layout.MaxTreeNodes is > 0 and <= 4096 &&
            layout.MaxAttempts is > 0 and <= 5;
    }

    private static bool IsPointer(uint value) => value >= 0x00010000;

    private static bool TryResolveModuleAddresses(
        uint moduleBase,
        IReadOnlyList<uint> rvas,
        out uint[] addresses)
    {
        addresses = new uint[rvas.Count];
        for (int index = 0; index < rvas.Count; index++)
        {
            if (!TryAdd(moduleBase, rvas[index], out addresses[index]))
            {
                addresses = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryAdd(uint left, uint right, out uint value)
    {
        ulong sum = (ulong)left + right;
        value = (uint)sum;
        return sum <= uint.MaxValue;
    }

    private static bool TryMultiply(uint left, uint right, out uint value)
    {
        ulong product = (ulong)left * right;
        value = (uint)product;
        return product <= uint.MaxValue;
    }

    private static Type10EntityPositionResult Failure(
        Type10EntityPositionStatus status,
        int entityId,
        string? stage,
        int attempts,
        int nodesVisited,
        string? entitySource = null,
        bool moduleRooted = false) => new(
            status,
            entityId,
            null,
            null,
            null,
            entitySource,
            stage,
            attempts,
            nodesVisited,
            ModuleRooted: moduleRooted,
            EntityIdentityRevalidated: false,
            ConsistentDoubleRead: false,
            HardwareAtomicReadProven: false);

    private sealed record AttemptResult(
        Type10EntityPositionStatus Status,
        string? FailureStage,
        bool Retryable,
        int NodesVisited,
        string? EntitySource,
        float? X,
        float? Y,
        float? Z,
        bool ModuleRooted,
        uint? RecordAddress)
    {
        public static AttemptResult Success(
            float x,
            float y,
            float z,
            int nodesVisited,
            string? source,
            uint recordAddress) => new(
                Type10EntityPositionStatus.Resolved,
                null,
                Retryable: false,
                nodesVisited,
                source,
                x,
                y,
                z,
                ModuleRooted: true,
                recordAddress);

        public static AttemptResult Retry(
            Type10EntityPositionStatus status,
            string? stage,
            int nodesVisited = 0,
            string? source = null,
            bool moduleRooted = true) => new(
                status,
                stage,
                Retryable: true,
                nodesVisited,
                source,
                null,
                null,
                null,
                moduleRooted,
                RecordAddress: null);

        public static AttemptResult Stop(
            Type10EntityPositionStatus status,
            string? stage,
            int nodesVisited = 0,
            string? source = null,
            bool moduleRooted = true) => new(
                status,
                stage,
                Retryable: false,
                nodesVisited,
                source,
                null,
                null,
                null,
                moduleRooted,
                RecordAddress: null);
    }

    private sealed record EntityLookup(
        Type10EntityPositionStatus Status,
        uint EntityAddress,
        string? Source,
        string? FailureStage,
        int NodesVisited,
        bool Retryable)
    {
        public static EntityLookup Found(uint address, string source, int nodesVisited) => new(
            Type10EntityPositionStatus.Resolved,
            address,
            source,
            null,
            nodesVisited,
            Retryable: false);

        public static EntityLookup Retry(
            Type10EntityPositionStatus status,
            string stage,
            int nodesVisited = 0,
            string? source = null) => new(
                status,
                0,
                source,
                stage,
                nodesVisited,
                Retryable: true);

        public static EntityLookup Stop(
            Type10EntityPositionStatus status,
            string stage,
            int nodesVisited = 0,
            string? source = null) => new(
                status,
                0,
                source,
                stage,
                nodesVisited,
                Retryable: false);
    }
}
