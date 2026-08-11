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

/// <summary>A decoded type-10 position packet.</summary>
/// <param name="Sequence">Packet ordinal in the event stream.</param>
/// <param name="ReplayTime">Replay clock time of the sample.</param>
/// <param name="EntityId">World entity carrying the position.</param>
/// <param name="SpaceId">Coordinate space id.</param>
/// <param name="VehicleId">Vehicle id.</param>
/// <param name="X">World x coordinate (replay-raw space).</param>
/// <param name="Y">World y coordinate (replay-raw space).</param>
/// <param name="Z">World z coordinate (replay-raw space).</param>
/// <param name="Yaw">Vehicle yaw (radians).</param>
/// <param name="Pitch">Vehicle pitch (radians).</param>
/// <param name="Roll">Vehicle roll (radians).</param>
/// <param name="IsDestroyMarker">True when the packet is a destroy marker:
/// the per-entity constant (payload +24..+35) is zeroed and the status
/// flags byte (+48) is cleared. Verified 2026-08-10 on both 11.19 replays:
/// the first marker per roster entity fires at the instant the position
/// stream freezes (the death position), every destroyed tank has exactly
/// one first-marker, and no survivor has any. Normal packets carry a
/// non-zero constant and flags=1.</param>
/// <param name="Evidence">Binary evidence slice backing the packet.</param>
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
    bool IsDestroyMarker,
    BinaryEvidence Evidence);

/// <summary>
/// A decoded type-8 subtype-1 health-change packet — the replay's actual HP
/// ledger. Layout (pinned from real 11.19 replays and validated against
/// battle_results per-player damage totals): entity/victim id u32 LE at
/// +0x00, subtype 1 u32 LE at +0x04, declared length 7 u32 LE at +0x08,
/// post-hit health u16 LE at +0x0C, attacker id i32 LE at +0x0E, flag byte
/// at +0x12. A post-hit health of 0xFFFD (65533) is the destroy marker and
/// names the killer in the attacker field. The damage amount is the victim's
/// HP delta (previous health minus this post-hit health), not a stored field
/// — the type-8 subtype-8 "amount" at +0x16 is unrelated to HP loss.
/// </summary>
internal sealed record HealthChangeObservation(
    long Sequence,
    TimeSpan ReplayTime,
    long VictimEntityId,
    int PostHitHealth,
    long AttackerEntityId,
    bool IsDestroy,
    BinaryEvidence Evidence);

/// <summary>
/// A decoded type-5 spawn full-state broadcast. The payload leads with the
/// entity id (u32 LE at +0x00) and carries the tank's current health as a
/// u16 LE at +0x33. Broadcasts fire at spawn and periodically; the first
/// broadcast per entity precedes any damage (verified 2026-08-11 on both
/// 11.19 replays), so its health value is the tank's max HP.
/// </summary>
internal sealed record SpawnHealthObservation(
    long Sequence,
    TimeSpan ReplayTime,
    long EntityId,
    int Health,
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

        // Destroy marker: the per-entity constant (payload +24..+35) is
        // zeroed and the status flags byte (+48) is cleared. Verified
        // 2026-08-10 on both 11.19 replays — the first marker per roster
        // entity fires at the death instant (position stream freezes) and
        // survivors never carry one. See the record docs for details.
        bool isDestroyMarker = payload[48] == 0;
        if (isDestroyMarker)
        {
            for (int i = 24; i < 36; i++)
            {
                if (payload[i] != 0)
                {
                    isDestroyMarker = false;
                    break;
                }
            }
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
            isDestroyMarker,
            EvidenceForPacket(packet));
        return true;
    }

    /// <summary>
    /// Decodes a type-5 spawn full-state broadcast: entity id (u32 LE at
    /// +0x00) and current health (u16 LE at +0x33). The layout was pinned
    /// from live replay evidence — the author's value (700) equals
    /// battle_results hitpoints_left exactly, the value is monotonic
    /// non-increasing per tank across broadcasts (first broadcast = max HP),
    /// and the same tank_id reads the same value across replays.
    /// </summary>
    public static bool TryReadSpawnHealth(
        EventPacket packet,
        out SpawnHealthObservation? spawnHealth,
        out string? warning)
    {
        spawnHealth = null;
        warning = null;
        const int minimumLength = 0x35; // u16 health at +0x33 needs 0x35 bytes
        if (packet.Type != 5 || packet.Payload.Length < minimumLength)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        int entity = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int health = BinaryPrimitives.ReadUInt16LittleEndian(payload[0x33..]);
        if (entity <= 0 || health <= 0)
        {
            warning = "A type-5 spawn broadcast carried a non-positive entity or health.";
            return false;
        }

        spawnHealth = new SpawnHealthObservation(
            packet.Ordinal,
            TimeSpan.FromSeconds(packet.ClockSeconds),
            entity,
            health,
            EvidenceForPacket(packet));
        return true;
    }

    /// <summary>
    /// Decodes a type-8 subtype-1 health-change packet — the replay's HP
    /// ledger. Layout: victim id u32 LE at +0x00, subtype 1 at +0x04,
    /// declared length 7 at +0x08, post-hit health u16 LE at +0x0C, attacker
    /// i32 LE at +0x0E, flag byte at +0x12. Post-hit health 0xFFFD is the
    /// destroy marker and carries the killer in the attacker field. The
    /// damage amount is NOT stored in the packet: it is the victim's HP
    /// delta from its previous known health (seeded by the type-5 max-HP
    /// broadcast), which is why the old subtype-8 amount read (at +0x16)
    /// never matched battle_results. Validated 2026-08-11: per-attacker
    /// damage sums equal battle_results damage_dealt on both replays with
    /// destroy credit (remaining HP at the destroy marker) included.
    /// </summary>
    public static bool TryReadHealthChange(
        EventPacket packet,
        out HealthChangeObservation? healthChange,
        out string? warning)
    {
        healthChange = null;
        warning = null;
        if (packet.Type != 8 || packet.Payload.Length < 19)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        uint subtype = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (subtype != 1)
        {
            return false;
        }

        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (declaredLength != 7)
        {
            return false;
        }

        int victim = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int postHitHealth = BinaryPrimitives.ReadUInt16LittleEndian(payload[0x0C..]);
        int attacker = BinaryPrimitives.ReadInt32LittleEndian(payload[0x0E..]);
        if (victim <= 0)
        {
            warning = "A health-change packet carried a non-positive victim id.";
            return false;
        }

        const int destroyHealth = 0xFFFD;
        healthChange = new HealthChangeObservation(
            packet.Ordinal,
            TimeSpan.FromSeconds(packet.ClockSeconds),
            victim,
            postHitHealth,
            attacker,
            IsDestroy: postHitHealth == destroyHealth,
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
