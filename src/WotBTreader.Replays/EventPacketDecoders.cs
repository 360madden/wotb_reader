using System.Buffers.Binary;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record ArenaParticipantObservation(
    long? AccountId,
    long EntityId,
    int? TeamNumber,
    string? PlayerName,
    int? TankCompactDescriptor,
    BinaryEvidence Evidence);

/// <summary>
/// Decoded fixed header of a type-0 (BasePlayerCreate) event-stream packet.
/// </summary>
/// <remarks>
/// Layout cross-validated against the Rust wotbreplay-parser payload reader
/// (payload.rs): 10 skipped bytes, a 1-byte-length UTF-8 author nickname, a
/// little-endian u64 arena unique id, and a little-endian u32 arena type id.
/// The trailing pickled arguments are preserved as raw evidence and never
/// executed (evidence-first).
/// </remarks>
internal sealed record BasePlayerCreateObservation(
    long Ordinal,
    TimeSpan ReplayTime,
    string AuthorNickname,
    ulong ArenaUniqueId,
    uint ArenaTypeId,
    BinaryEvidence Evidence);

internal sealed record PositionObservation(
    long Sequence,
    TimeSpan ReplayTime,
    long EntityId,
    int SpaceId,
    int VehicleId,
    double X,
    double Y,
    double Z,
    double Yaw,
    double Pitch,
    double Roll,
    BinaryEvidence Evidence);

internal sealed record DamageObservation(
    long Sequence,
    TimeSpan ReplayTime,
    long AttackerEntityId,
    long VictimEntityId,
    int Damage,
    BinaryEvidence Evidence);

internal static class EventPacketDecoders
{
    public static bool TryReadArenaParticipants(
        EventPacket packet,
        DecoderLimits limits,
        out IReadOnlyList<ArenaParticipantObservation> participants,
        out string? warning)
    {
        participants = [];
        warning = null;
        if (packet.Type != 8 || packet.Payload.Length < 8)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        uint subtype = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (subtype != 48)
        {
            return false;
        }

        try
        {
            int offset = 8;
            ReplayBinary.EnsureAvailable(payload, offset, sizeof(uint));
            uint remainingLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            offset += sizeof(uint);
            if (remainingLength > payload.Length - offset)
            {
                throw new ReplayFormatException(
                    "replay.invalid_update_arena_length",
                    "updateArena2 declares more bytes than its packet contains.");
            }

            ulong fieldNumber = ProtobufWireReader.ReadVarint(payload, ref offset);
            if (fieldNumber != 1)
            {
                return false;
            }

            int messageLength = ReadQuirkyLength(payload, ref offset);
            ReplayBinary.EnsureAvailable(payload, offset, messageLength);
            ReadOnlyMemory<byte> message = packet.Payload.Slice(offset, messageLength);
            ProtobufBudget budget = new(Math.Min(limits.MaximumUnknownFields, 4_096));
            IReadOnlyList<ProtobufField> updateArena = ProtobufWireReader.ReadMessage(
                message,
                limits,
                budget,
                depth: 1);
            ProtobufField? wrapperField = updateArena.FirstOrDefault(
                field => field.Number == 1 &&
                         field.WireType == ProtobufWireType.LengthDelimited);
            if (wrapperField is null)
            {
                throw new ReplayFormatException(
                    "replay.invalid_update_arena",
                    "updateArena2 is missing its players wrapper.");
            }

            IReadOnlyList<ProtobufField> wrapper = ProtobufWireReader.ReadMessage(
                wrapperField.Bytes,
                limits,
                budget,
                depth: 2);
            List<ArenaParticipantObservation> decoded = [];
            foreach (ProtobufField playerField in wrapper)
            {
                if (playerField.Number != 1 ||
                    playerField.WireType != ProtobufWireType.LengthDelimited)
                {
                    continue;
                }

                if (decoded.Count >= ReplayFormatConstants.MaximumRosterEntries)
                {
                    throw new ReplayFormatException(
                        "replay.update_arena_roster_limit",
                        "updateArena2 exceeds the participant limit.");
                }

                IReadOnlyList<ProtobufField> player = ProtobufWireReader.ReadMessage(
                    playerField.Bytes,
                    limits,
                    budget,
                    depth: 3);
                ulong? entity = FirstVarint(player, 1);
                ulong? account = FirstVarint(player, 7);
                if (entity is null ||
                    entity > long.MaxValue ||
                    account > long.MaxValue)
                {
                    continue;
                }

                ProtobufField? nicknameField = player.FirstOrDefault(
                    field => field.Number == 3 &&
                             field.WireType == ProtobufWireType.LengthDelimited);
                string? nickname = nicknameField is null
                    ? null
                    : ReadParticipantText(nicknameField.Bytes);
                long? accountId = account is null or 0 ? null : (long)account.Value;
                if (accountId is null && string.IsNullOrWhiteSpace(nickname))
                {
                    continue;
                }

                int? team = NormalizeTeam(FirstVarint(player, 4));
                ProtobufField? statsField = player.FirstOrDefault(
                    field => field.Number == 2 &&
                             field.WireType == ProtobufWireType.LengthDelimited);
                int? tankDescriptor = statsField is { Bytes.Length: >= 2 }
                    ? BinaryPrimitives.ReadUInt16LittleEndian(statsField.Bytes.Span)
                    : null;
                if (tankDescriptor == 0)
                {
                    tankDescriptor = null;
                }

                int playerOffsetInPacket = checked(
                    12 +
                    offset +
                    wrapperField.ValueOffset +
                    playerField.Offset);
                decoded.Add(new ArenaParticipantObservation(
                    accountId,
                    (long)entity.Value,
                    team,
                    nickname,
                    tankDescriptor,
                    new BinaryEvidence(
                        ReplayFormatConstants.EventStreamEntry,
                        checked(packet.Offset + playerOffsetInPacket),
                        playerField.EncodedLength,
                        playerField.EncodedBytes)));
            }

            participants = decoded;
            return true;
        }
        catch (ReplayFormatException exception)
        {
            warning = $"A type 8/subtype 48 packet was preserved as raw evidence: {exception.Code}.";
            return false;
        }
    }

    public static bool TryReadBasePlayerCreate(
        EventPacket packet,
        out BasePlayerCreateObservation? observation,
        out string? warning)
    {
        observation = null;
        warning = null;
        if (packet.Type != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        // 10 reserved bytes, then 1-byte-length author nickname.
        const int reserved = 10;
        if (payload.Length <= reserved)
        {
            warning = "A type 0 packet was preserved as raw evidence (header too short).";
            return false;
        }

        try
        {
            int offset = reserved;
            int nameLength = payload[offset++];
            if (nameLength > 512)
            {
                throw new ReplayFormatException(
                    "replay.base_player_create_name_limit",
                    "A BasePlayerCreate author nickname exceeds its byte limit.");
            }

            ReplayBinary.EnsureAvailable(payload, offset, nameLength);
            string authorNickname = ReplayBinary.DecodeUtf8(
                payload.Slice(offset, nameLength),
                "BasePlayerCreate author nickname");
            offset += nameLength;

            ReplayBinary.EnsureAvailable(payload, offset, sizeof(ulong) + sizeof(uint));
            ulong arenaUniqueId = BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]);
            offset += sizeof(ulong);
            uint arenaTypeId = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);

            observation = new BasePlayerCreateObservation(
                packet.Ordinal,
                TimeSpan.FromSeconds(packet.ClockSeconds),
                authorNickname,
                arenaUniqueId,
                arenaTypeId,
                EvidenceForPacket(packet));
            return true;
        }
        catch (ReplayFormatException exception)
        {
            warning = $"A type 0 packet was preserved as raw evidence: {exception.Code}.";
            return false;
        }
    }

    public static bool TryReadPosition(
        EventPacket packet,
        out PositionObservation? position,
        out string? warning)
    {
        position = null;
        warning = null;
        if (packet.Type != 10)
        {
            return false;
        }

        if (packet.Payload.Length != 49)
        {
            warning = "A type 10 packet did not have the evidence-backed 49-byte layout.";
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        int entityId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int spaceId = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        int vehicleId = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        float x = ReadFiniteSingle(payload, 12);
        float y = ReadFiniteSingle(payload, 16);
        float z = ReadFiniteSingle(payload, 20);
        // The 49-byte layout's tail carries the entity's rotation as three
        // float32 values (payload +36 yaw, +40 pitch, +44 roll — verified
        // 2026-08-10: the viewpoint yaw tracks the replay-derived heading
        // 1:1 in radians across both 11.19 replays, sign-correct; pitch/roll
        // are the small residual components). They are evidence-backed fields
        // like the coordinates: non-finite values fail the packet closed.
        float yaw = ReadFiniteSingle(payload, 36);
        float pitch = ReadFiniteSingle(payload, 40);
        float roll = ReadFiniteSingle(payload, 44);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
            || !float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(roll))
        {
            warning = "A type 10 packet contained non-finite coordinates or rotation.";
            return false;
        }

        position = new PositionObservation(
            packet.Ordinal,
            TimeSpan.FromSeconds(packet.ClockSeconds),
            entityId,
            spaceId,
            vehicleId,
            x,
            y,
            z,
            yaw,
            pitch,
            roll,
            EvidenceForPacket(packet));
        return true;
    }

    public static bool TryReadDirectDamage(
        EventPacket packet,
        out DamageObservation? damage,
        out string? warning)
    {
        damage = null;
        warning = null;
        if (packet.Type != 8 || packet.Payload.Length < 24)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        int methodEntity = BinaryPrimitives.ReadInt32LittleEndian(payload);
        uint subtype = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (subtype != 8)
        {
            return false;
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (declaredLength > payload.Length - 12 || payload.Length < 24)
        {
            warning = "A direct-damage method packet had an invalid declared length.";
            return false;
        }

        int attacker = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]);
        int victim = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]);
        byte kind = payload[20];
        byte damageSubtype = payload[21];
        int amount = BinaryPrimitives.ReadUInt16BigEndian(payload[22..]);
        if (methodEntity != victim || kind != 1 || damageSubtype != 3)
        {
            return false;
        }

        damage = new DamageObservation(
            packet.Ordinal,
            TimeSpan.FromSeconds(packet.ClockSeconds),
            attacker,
            victim,
            amount,
            EvidenceForPacket(packet));
        return true;
    }

    public static uint? ReadEntityMethodSubtype(EventPacket packet)
    {
        if (packet.Type != 8 || packet.Payload.Length < 8)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span[4..]);
    }

    public static BinaryEvidence EvidenceForPacket(EventPacket packet) =>
        new(
            ReplayFormatConstants.EventStreamEntry,
            packet.Offset,
            packet.EncodedLength,
            packet.EncodedBytes);

    private static int ReadQuirkyLength(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ReplayBinary.EnsureAvailable(bytes, offset, 1);
        byte first = bytes[offset++];
        if (first != 0xff)
        {
            return first;
        }

        ReplayBinary.EnsureAvailable(bytes, offset, 3);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += sizeof(ushort);
        if (bytes[offset++] != 0)
        {
            throw new ReplayFormatException(
                "replay.invalid_quirky_length",
                "A replay extended length is missing its zero terminator.");
        }

        return length;
    }

    private static ulong? FirstVarint(IReadOnlyList<ProtobufField> fields, int number) =>
        fields.FirstOrDefault(
            field => field.Number == number &&
                     field.WireType == ProtobufWireType.Varint)?.NumericValue;

    private static int? NormalizeTeam(ulong? value) =>
        value is 1 or 2 ? (int)value.Value : null;

    private static string ReadParticipantText(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 512)
        {
            throw new ReplayFormatException(
                "replay.participant_text_limit",
                "An updateArena2 participant name exceeds its byte limit.");
        }

        return ReplayBinary.DecodeUtf8(bytes.Span, "updateArena2 participant name");
    }

    private static float ReadFiniteSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
}
