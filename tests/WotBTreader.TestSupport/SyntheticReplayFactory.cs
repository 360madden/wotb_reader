using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.Replays;

namespace WotBTreader.TestSupport;

/// <summary>
/// Builds deterministic synthetic replay archives. CI never uses private game
/// files, so every decoder and ingestion test is exercised against fixtures
/// generated here from the real format constants.
/// </summary>
public static class SyntheticReplayFactory
{
    public static byte[] CreateReplay(
        string version = "11.18.0",
        bool insertMalformedGap = false,
        bool includeEndSentinel = true,
        ulong? basePlayerCreateArenaId = null,
        bool includeDestroyMarker = false,
        bool includeSpawnHealth = false,
        bool includeHealthChange = false,
        bool includeShotImpact = false)
    {
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version,
            title = string.Empty,
            dbid = "42",
            playerName = "pilot-a",
            battleStartTime = "1700000000",
            playerVehicleName = "vehicle-a",
            mapName = "synthetic-map",
            arenaUniqueId = "42",
            battleDuration = 120.0,
            vehicleCompDescriptor = 2897,
            camouflageId = 0,
            mapId = 5,
            arenaBonusType = 1,
            camouflageCustomData = string.Empty,
        });
        byte[] battleResults = CreateBattleResults();
        byte[] eventStream = CreateEventStream(
            insertMalformedGap,
            includeEndSentinel,
            basePlayerCreateArenaId,
            includeDestroyMarker,
            includeSpawnHealth,
            includeHealthChange,
            includeShotImpact);
        return CreateArchive(
            (ReplayFormatConstants.MetadataEntry, metadata),
            (ReplayFormatConstants.BattleResultsEntry, battleResults),
            (ReplayFormatConstants.EventStreamEntry, eventStream));
    }

    public static byte[] CreateArchive(params (string Name, byte[] Bytes)[] entries)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using Stream stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return output.ToArray();
    }

    public static ReplayInput CreateInput(byte[] archiveBytes)
    {
        byte[] immutableBytes = archiveBytes.ToArray();
        SourceArtifact artifact = new(
            SourceArtifactId.New(),
            new ContentHash(
                Convert.ToHexString(SHA256.HashData(immutableBytes)).ToLowerInvariant()),
            immutableBytes.LongLength,
            "application/vnd.wargaming.wotb-replay",
            ".wotbreplay",
            DateTimeOffset.UnixEpoch,
            "1");
        return new ReplayInput(
            artifact,
            _ => ValueTask.FromResult<Stream>(
                new MemoryStream(immutableBytes, writable: false)));
    }

    public static byte[] CreatePickle(ReadOnlySpan<byte> protobuf)
    {
        using MemoryStream output = new();
        output.WriteByte(0x80);
        output.WriteByte(0x02);
        output.WriteByte(0x8a);
        output.WriteByte(0x01);
        output.WriteByte(0x2a);
        output.WriteByte(0x54);
        WriteUInt32(output, checked((uint)protobuf.Length));
        output.Write(protobuf);
        output.WriteByte(0x86);
        output.WriteByte(0x71);
        output.WriteByte(0x00);
        output.WriteByte(0x2e);
        return output.ToArray();
    }

    public static byte[] CreateEventStream(
        bool insertMalformedGap,
        bool includeEndSentinel,
        ulong? basePlayerCreateArenaId = null,
        bool includeDestroyMarker = false,
        bool includeSpawnHealth = false,
        bool includeHealthChange = false,
        bool includeShotImpact = false)
    {
        using MemoryStream output = new();
        WriteUInt32(output, ReplayFormatConstants.EventStreamMagic);
        output.Write(new byte[8]);
        WriteByteString(output, "synthetic-hash");
        WriteByteString(output, "11.18.0");
        output.WriteByte(0);

        WritePacket(output, 0, 0.1f, CreateBasePlayerCreatePayload(basePlayerCreateArenaId));
        WritePacket(output, 8, 0.2f, CreateUpdateArenaPayload());
        if (includeSpawnHealth)
        {
            // Type-5 spawn full-state broadcasts: a first broadcast per roster
            // entity (max HP), then a later lower-health re-broadcast that
            // must NOT emit a second MaxHealthObserved event, plus a
            // non-roster entity (999) that must not emit one either.
            WritePacket(output, 5, 0.5f, CreateSpawnHealthPayload(100, 700));
            WritePacket(output, 5, 0.6f, CreateSpawnHealthPayload(200, 500));
            WritePacket(output, 5, 0.7f, CreateSpawnHealthPayload(100, 650));
            WritePacket(output, 5, 0.8f, CreateSpawnHealthPayload(999, 400));
        }

        WritePacket(output, 10, 1.0f, CreatePositionPayload(100, 10, 20, 30, yaw: 0.75f));
        if (insertMalformedGap)
        {
            output.Write(new byte[] { 0xde, 0xad, 0xbe });
        }

        WritePacket(output, 10, 2.0f, CreatePositionPayload(200, -10, 5, -20));
        if (includeHealthChange)
        {
            // Type-8 subtype-1 health-change ledger. Entity 100 starts at
            // max 700 (from the spawn broadcast), takes 100 -> 600, then is
            // destroyed (0xFFFD marker) by entity 200 with 600 remaining HP.
            // Entity 200 takes 50 -> 450. The destroy marker's remaining HP
            // is credited to the killer, so entity 200 deals 600 + 50.
            // Written after the position packets so stream clocks ascend.
            WritePacket(output, 8, 2.0f, CreateHealthChangePayload(100, 600, 200));
            WritePacket(output, 8, 2.1f, CreateHealthChangePayload(200, 450, 100));
            WritePacket(output, 8, 3.0f, CreateHealthChangePayload(100, 0xFFFD, 200));
        }
        if (includeDestroyMarker)
        {
            // Destroy marker for roster entity 100 at t=3.0, plus a second
            // marker at t=3.1 for the same entity (wreck re-broadcast, must
            // not emit a second Destroyed event), plus a marker for a
            // non-roster entity (999) that must be ignored.
            WritePacket(output, 10, 3.0f, CreatePositionPayload(100, 10, 20, 30, destroyMarker: true));
            WritePacket(output, 10, 3.1f, CreatePositionPayload(100, 10, 20, 30, destroyMarker: true));
            WritePacket(output, 10, 3.2f, CreatePositionPayload(999, 1, 2, 3, destroyMarker: true));
        }

        if (includeShotImpact)
        {
            // Type-32 damage-with-payload mirror: a penetrating `01 12` hit on
            // 200 (result 0x03), a non-penetrating `01 12` bounce on 100
            // (result 0x00), a penetrating `01 11` hit on 200 (result 0x03),
            // and a short-companion `01 02` that must NOT emit a ShotImpact.
            WritePacket(output, 32, 4.0f, CreateShotImpactPayload(200, 0x03, variantB: true));
            WritePacket(output, 32, 4.1f, CreateShotImpactPayload(100, 0x00, variantB: true));
            WritePacket(output, 32, 4.2f, CreateShotImpactPayload(200, 0x03, variantB: false));
            WritePacket(output, 32, 4.3f, CreateShortCompanionPayload(100));
            // Type-8 subtype-8 attributions: the attacker for the t=4.0 hit
            // (100 shoots 200) and the t=4.1 bounce (200 shoots 100). The
            // t=4.2 hit has NO attribution, so its attackerEntityId stays null.
            WritePacket(output, 8, 4.0f, CreateShotAttributionPayload(200, 100));
            WritePacket(output, 8, 4.1f, CreateShotAttributionPayload(100, 200));
        }

        WritePacket(output, 14, 120.0f, []);
        if (includeEndSentinel)
        {
            WritePacket(output, uint.MaxValue, 0, new byte[16]);
        }

        return output.ToArray();
    }

    private static byte[] CreateBattleResults()
    {
        using MemoryStream rosterInfo = new();
        WriteBytesField(rosterInfo, 1, Encoding.UTF8.GetBytes("pilot-a"));
        WriteVarintField(rosterInfo, 3, 1);
        WriteBytesField(rosterInfo, 5, Encoding.UTF8.GetBytes("TAG"));

        using MemoryStream roster = new();
        WriteVarintField(roster, 1, 42);
        WriteBytesField(roster, 2, rosterInfo.ToArray());

        using MemoryStream resultsInfo = new();
        WriteVarintField(resultsInfo, 2, 1200);   // credits earned
        WriteVarintField(resultsInfo, 3, 850);    // base XP
        WriteVarintField(resultsInfo, 4, 15);     // shots
        WriteVarintField(resultsInfo, 5, 9);      // hits dealt
        WriteVarintField(resultsInfo, 7, 5);      // penetrations dealt
        WriteVarintField(resultsInfo, 8, 2340);   // damage dealt
        WriteVarintField(resultsInfo, 9, 300);    // assisted damage 1
        WriteVarintField(resultsInfo, 10, 120);   // assisted damage 2
        WriteVarintField(resultsInfo, 12, 2);     // hits received
        WriteVarintField(resultsInfo, 13, 1);     // non-penetrating hits received
        WriteVarintField(resultsInfo, 15, 1);     // penetrations received
        WriteVarintField(resultsInfo, 17, 3);     // enemies damaged
        WriteVarintField(resultsInfo, 18, 1);     // enemies destroyed
        WriteVarintField(resultsInfo, 32, 40);    // victory points earned
        WriteVarintField(resultsInfo, 33, 20);    // victory points seized
        WriteFixed32Field(resultsInfo, 107, BitConverter.SingleToInt32Bits(2575.5f));
        WriteVarintField(resultsInfo, 117, 410);  // damage blocked
        WriteVarintField(resultsInfo, 101, 42);
        WriteVarintField(resultsInfo, 102, 1);
        WriteVarintField(resultsInfo, 103, 2897);

        using MemoryStream results = new();
        WriteVarintField(results, 1, 7);
        WriteBytesField(results, 2, resultsInfo.ToArray());

        using MemoryStream root = new();
        WriteVarintField(root, 2, 1_700_000_000);
        WriteBytesField(root, 201, roster.ToArray());
        WriteBytesField(root, 301, results.ToArray());
        WriteVarintField(root, 999, 123);
        return CreatePickle(root.ToArray());
    }

    private static byte[] CreateBasePlayerCreatePayload(ulong? arenaIdOverride = null)
    {
        using MemoryStream payload = new();
        // 10 reserved bytes, then a 1-byte-length UTF-8 author nickname, a
        // little-endian u64 arena unique id, a little-endian u32 arena type
        // id, then the (unused here) pickled arguments length marker.
        payload.Write(new byte[10]);
        WriteByteString(payload, "pilot-a");
        Span<byte> arenaId = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(arenaId, arenaIdOverride ?? 42);
        payload.Write(arenaId);
        Span<byte> arenaType = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(arenaType, 7);
        payload.Write(arenaType);
        payload.WriteByte(0); // quirky length 0: no pickled arguments.
        return payload.ToArray();
    }

    private static byte[] CreateUpdateArenaPayload()
    {
        byte[] first = CreateArenaPlayer(
            entityId: 100,
            accountId: 42,
            nickname: "pilot-a",
            team: 1,
            tankDescriptor: 2897);
        byte[] second = CreateArenaPlayer(
            entityId: 200,
            accountId: null,
            nickname: "unit-b",
            team: 2,
            tankDescriptor: 17);

        using MemoryStream wrapper = new();
        WriteBytesField(wrapper, 1, first);
        WriteBytesField(wrapper, 1, second);

        using MemoryStream message = new();
        WriteBytesField(message, 1, wrapper.ToArray());
        byte[] messageBytes = message.ToArray();
        if (messageBytes.Length >= byte.MaxValue)
        {
            throw new InvalidOperationException(
                "The synthetic updateArena2 message no longer fits its one-byte length.");
        }

        using MemoryStream payload = new();
        WriteInt32(payload, 1);
        WriteUInt32(payload, 48);
        WriteUInt32(payload, checked((uint)(messageBytes.Length + 2)));
        payload.WriteByte(1);
        payload.WriteByte(checked((byte)messageBytes.Length));
        payload.Write(messageBytes);
        return payload.ToArray();
    }

    private static byte[] CreateArenaPlayer(
        int entityId,
        long? accountId,
        string nickname,
        int team,
        ushort tankDescriptor)
    {
        using MemoryStream player = new();
        WriteVarintField(player, 1, checked((ulong)entityId));
        byte[] stats = new byte[15];
        BinaryPrimitives.WriteUInt16LittleEndian(stats, tankDescriptor);
        WriteBytesField(player, 2, stats);
        WriteBytesField(player, 3, Encoding.UTF8.GetBytes(nickname));
        WriteVarintField(player, 4, checked((ulong)team));
        WriteVarintField(player, 5, 1);
        if (accountId is not null)
        {
            WriteVarintField(player, 7, checked((ulong)accountId.Value));
        }

        return player.ToArray();
    }

    private static byte[] CreateHealthChangePayload(int victimId, int postHitHealth, int attackerId)
    {
        // Type-8 subtype-1 health-change layout: victim u32 LE at +0x00,
        // subtype 1 at +0x04, declared length 7 at +0x08, post-hit health
        // u16 LE at +0x0C, attacker i32 LE at +0x0E, flag byte at +0x12.
        byte[] payload = new byte[19];
        BinaryPrimitives.WriteInt32LittleEndian(payload, victimId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x0C), checked((ushort)postHitHealth));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0x0E), attackerId);
        return payload;
    }

    private static byte[] CreateShotImpactPayload(int victimId, byte hitResult, bool variantB)
    {
        // Type-32 damage-with-payload layout (pinned 2026-08-13): victim u32
        // at +0x00, event flag u16 at +0x04 (`01 12` 27 B or `01 11` 26 B),
        // a `00000099`/`00000098` marker word, a 2–3 byte flags region, the
        // 6-byte shell signature, then a trailing 4-byte field whose FIRST
        // byte is the hit result (0x03 = penetrating, else non-penetrating).
        byte[] payload = new byte[variantB ? 27 : 26];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)victimId));
        payload[4] = 0x01;
        payload[5] = variantB ? (byte)0x12 : (byte)0x11;
        payload[9] = variantB ? (byte)0x99 : (byte)0x98;
        // Flags region (opaque stand-in) at +0x0A (3 B for `01 12`, 2 B for `01 11`).
        payload[10] = 0x80;
        payload[12] = 0x01;
        // Shell signature (opaque stand-in) at +0x0D (`01 12`) / +0x0C (`01 11`).
        int shellOffset = variantB ? 13 : 12;
        payload[shellOffset] = 0xa6;
        payload[shellOffset + 1] = 0xa5;
        payload[shellOffset + 2] = 0xe0;
        payload[shellOffset + 3] = 0xa2;
        payload[shellOffset + 4] = 0xa8;
        payload[shellOffset + 5] = 0xb1;
        // Hit-result byte at +0x13 (`01 12`) / +0x12 (`01 11`).
        payload[variantB ? 19 : 18] = hitResult;
        return payload;
    }

    private static byte[] CreateShotAttributionPayload(int victimId, int attackerId)
    {
        // Type-8 subtype-8 shot-attribution layout (pinned 2026-08-13, 33 B):
        // victim u32 LE at +0x00, subtype 8 at +0x04, declared length 21 at
        // +0x08, attacker u32 LE at +0x0C, victim again at +0x10, then a flag
        // u16 + byte + 6-byte shell signature + trailing 4 bytes (opaque).
        byte[] payload = new byte[33];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)victimId));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 21);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x0C), checked((uint)attackerId));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10), checked((uint)victimId));
        payload[20] = 0x01;
        payload[22] = 0x01;
        return payload;
    }

    private static byte[] CreateShortCompanionPayload(int victimId)
    {
        // Type-32 `01 02` short companion (11 B): victim u32 + flag `01 02` +
        // a small opaque tail. Not a damage-with-payload variant, so it must
        // NOT emit a ShotImpact event.
        byte[] payload = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)victimId));
        payload[4] = 0x01;
        payload[5] = 0x02;
        payload[10] = 0x28;
        return payload;
    }

    private static byte[] CreateSpawnHealthPayload(int entityId, int health)
    {
        // Type-5 spawn broadcast layout (pinned from real 11.19 replays):
        // entity id u32 LE at +0x00, current health u16 LE at +0x33. The
        // payload must be at least 0x35 bytes for the health read.
        byte[] payload = new byte[0x40];
        BinaryPrimitives.WriteInt32LittleEndian(payload, entityId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0x33), checked((ushort)health));
        return payload;
    }

    private static byte[] CreatePositionPayload(
        int entityId,
        float x,
        float y,
        float z,
        float yaw = 0f,
        float pitch = 0f,
        float roll = 0f,
        bool destroyMarker = false)
    {
        byte[] payload = new byte[49];
        BinaryPrimitives.WriteInt32LittleEndian(payload, entityId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), entityId);
        WriteSingle(payload, 12, x);
        WriteSingle(payload, 16, y);
        WriteSingle(payload, 20, z);
        // Rotation tail: yaw/pitch/roll float32 at payload +36/+40/+44.
        WriteSingle(payload, 36, yaw);
        WriteSingle(payload, 40, pitch);
        WriteSingle(payload, 44, roll);
        if (destroyMarker)
        {
            // Destroy marker: per-entity constant (payload +24..+35) zeroed
            // and status flags byte (+48) cleared. The byte[] initializes to
            // zeros, so nothing more to write.
            return payload;
        }

        // Normal packets carry a non-zero per-entity constant and flags=1.
        // (The decoder only gates on the destroy-marker predicate; the exact
        // constant is a stand-in for the real per-entity value.) The constant
        // occupies exactly payload +24..+35 so the rotation tail at +36 stays
        // untouched.
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24), entityId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32), entityId);
        payload[48] = 1;
        return payload;
    }

    private static void WritePacket(
        Stream output,
        uint type,
        float clock,
        ReadOnlySpan<byte> payload)
    {
        WriteUInt32(output, checked((uint)payload.Length));
        WriteUInt32(output, type);
        WriteInt32(output, BitConverter.SingleToInt32Bits(clock));
        output.Write(payload);
    }

    private static void WriteBytesField(Stream output, int fieldNumber, byte[] bytes)
    {
        WriteVarint(output, checked((ulong)((fieldNumber << 3) | 2)));
        WriteVarint(output, checked((ulong)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteFixed32Field(Stream output, int fieldNumber, int bits)
    {
        WriteVarint(output, checked((ulong)((fieldNumber << 3) | 5)));
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, bits);
        output.Write(buffer);
    }

    private static void WriteVarintField(Stream output, int fieldNumber, ulong value)
    {
        WriteVarint(output, checked((ulong)(fieldNumber << 3)));
        WriteVarint(output, value);
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    private static void WriteByteString(Stream output, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        output.WriteByte(checked((byte)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteSingle(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset),
            BitConverter.SingleToInt32Bits(value));

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }
}
