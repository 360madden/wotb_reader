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
        bool includeSpawnHealth = false)
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
            includeSpawnHealth);
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
        bool includeSpawnHealth = false)
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
