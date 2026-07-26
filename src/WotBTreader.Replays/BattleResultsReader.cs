using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record BattleParticipantObservation(
    long AccountId,
    string? PlayerName,
    string? ClanTag,
    int? TeamNumber,
    int? TankCompactDescriptor,
    BinaryEvidence Evidence);

internal sealed record BinaryEvidence(
    string ArchiveEntry,
    int Offset,
    int Length,
    ReadOnlyMemory<byte> Bytes);

internal sealed record UnknownProtobufEvidence(
    int FieldNumber,
    ProtobufWireType WireType,
    string Path,
    BinaryEvidence Evidence);

internal sealed record BattleResultsData(
    ulong ArenaIdentity,
    DateTimeOffset? BattleTimeUtc,
    IReadOnlyDictionary<long, BattleParticipantObservation> Participants,
    IReadOnlyList<UnknownProtobufEvidence> UnknownFields,
    BinaryEvidence WholeEntryEvidence);

internal static class BattleResultsReader
{
    public static BattleResultsData Read(
        ReadOnlyMemory<byte> pickleBytes,
        DecoderLimits limits)
    {
        BattleResultsEnvelope envelope =
            RestrictedPickleReader.ReadBattleResultsEnvelope(pickleBytes, limits);
        ProtobufBudget budget = new(Math.Min(limits.MaximumUnknownFields, 16_384));
        IReadOnlyList<ProtobufField> root = ProtobufWireReader.ReadMessage(
            envelope.Protobuf,
            limits,
            budget);

        Dictionary<long, MutableParticipant> participants = [];
        List<UnknownProtobufEvidence> unknown = [];
        DateTimeOffset? battleTime = null;
        int rosterCount = 0;
        int resultsCount = 0;
        foreach (ProtobufField field in root)
        {
            switch (field.Number, field.WireType)
            {
                case (2, ProtobufWireType.Varint) when field.NumericValue is not null:
                    battleTime = TryUnixTime(field.NumericValue.Value);
                    break;
                case (201, ProtobufWireType.LengthDelimited):
                    if (++rosterCount > ReplayFormatConstants.MaximumRosterEntries)
                    {
                        throw new ReplayFormatException(
                            "replay.roster_count_limit",
                            "The battle-results roster exceeds the participant limit.");
                    }

                    if (!TryReadRosterEntry(
                        field,
                        envelope.ProtobufOffset,
                        participants,
                        unknown,
                        limits,
                        budget))
                    {
                        unknown.Add(CreateUnknown(
                            field,
                            envelope.ProtobufOffset,
                            "root.201.unmapped"));
                    }

                    break;
                case (301, ProtobufWireType.LengthDelimited):
                    if (++resultsCount > ReplayFormatConstants.MaximumRosterEntries)
                    {
                        throw new ReplayFormatException(
                            "replay.results_count_limit",
                            "The battle-results player list exceeds the participant limit.");
                    }

                    ReadResultsEntry(
                        field,
                        envelope.ProtobufOffset,
                        participants,
                        unknown,
                        limits,
                        budget);
                    break;
                default:
                    unknown.Add(CreateUnknown(
                        field,
                        envelope.ProtobufOffset,
                        "root"));
                    break;
            }
        }

        Dictionary<long, BattleParticipantObservation> immutable = [];
        foreach ((long accountId, MutableParticipant participant) in participants)
        {
            BinaryEvidence evidence = participant.Evidence ??
                throw new ReplayFormatException(
                    "replay.missing_participant_evidence",
                    "A decoded participant has no source evidence.");
            immutable.Add(
                accountId,
                new BattleParticipantObservation(
                    accountId,
                    participant.PlayerName,
                    participant.ClanTag,
                    participant.TeamNumber,
                    participant.TankCompactDescriptor,
                    evidence));
        }

        return new BattleResultsData(
            envelope.ArenaIdentity,
            battleTime,
            immutable,
            unknown,
            new BinaryEvidence(
                ReplayFormatConstants.BattleResultsEntry,
                0,
                pickleBytes.Length,
                pickleBytes));
    }

    private static bool TryReadRosterEntry(
        ProtobufField rosterField,
        int protobufBaseOffset,
        Dictionary<long, MutableParticipant> participants,
        List<UnknownProtobufEvidence> unknown,
        DecoderLimits limits,
        ProtobufBudget budget)
    {
        int rosterBase = checked(protobufBaseOffset + rosterField.ValueOffset);
        IReadOnlyList<ProtobufField> fields = ProtobufWireReader.ReadMessage(
            rosterField.Bytes,
            limits,
            budget,
            depth: 1);
        ulong? accountValue = FirstVarint(fields, 1);
        if (accountValue is null || accountValue > long.MaxValue)
        {
            // Some observed 11.18 results contain an extra #201 message that
            // is not a roster row. Its bytes stay available as unknown
            // evidence; identity is never guessed from the remaining fields.
            return false;
        }

        long accountId = (long)accountValue.Value;
        MutableParticipant participant = GetParticipant(participants, accountId);
        participant.Evidence ??= EvidenceFor(rosterField, protobufBaseOffset);
        foreach (ProtobufField field in fields)
        {
            switch (field.Number, field.WireType)
            {
                case (1, ProtobufWireType.Varint):
                    break;
                case (2, ProtobufWireType.LengthDelimited):
                    ReadRosterInfo(
                        field,
                        rosterBase,
                        participant,
                        unknown,
                        limits,
                        budget);
                    break;
                default:
                    unknown.Add(CreateUnknown(field, rosterBase, "root.201"));
                    break;
            }
        }

        return true;
    }

    private static void ReadRosterInfo(
        ProtobufField infoField,
        int rosterBase,
        MutableParticipant participant,
        List<UnknownProtobufEvidence> unknown,
        DecoderLimits limits,
        ProtobufBudget budget)
    {
        int infoBase = checked(rosterBase + infoField.ValueOffset);
        IReadOnlyList<ProtobufField> fields = ProtobufWireReader.ReadMessage(
            infoField.Bytes,
            limits,
            budget,
            depth: 2);
        foreach (ProtobufField field in fields)
        {
            switch (field.Number, field.WireType)
            {
                case (1, ProtobufWireType.LengthDelimited):
                    participant.PlayerName = ReadBoundedText(field.Bytes, "participant nickname");
                    break;
                case (3, ProtobufWireType.Varint):
                    participant.TeamNumber = NormalizeTeam(field.NumericValue);
                    break;
                case (5, ProtobufWireType.LengthDelimited):
                    participant.ClanTag = ReadBoundedText(field.Bytes, "participant clan");
                    break;
                default:
                    unknown.Add(CreateUnknown(field, infoBase, "root.201.2"));
                    break;
            }
        }
    }

    private static void ReadResultsEntry(
        ProtobufField resultsField,
        int protobufBaseOffset,
        Dictionary<long, MutableParticipant> participants,
        List<UnknownProtobufEvidence> unknown,
        DecoderLimits limits,
        ProtobufBudget budget)
    {
        int resultBase = checked(protobufBaseOffset + resultsField.ValueOffset);
        IReadOnlyList<ProtobufField> fields = ProtobufWireReader.ReadMessage(
            resultsField.Bytes,
            limits,
            budget,
            depth: 1);
        ProtobufField? infoField = fields.FirstOrDefault(
            field => field.Number == 2 && field.WireType == ProtobufWireType.LengthDelimited);
        if (infoField is null)
        {
            throw new ReplayFormatException(
                "replay.invalid_player_results",
                "A player-results entry is missing its info message.");
        }

        int infoBase = checked(resultBase + infoField.ValueOffset);
        IReadOnlyList<ProtobufField> info = ProtobufWireReader.ReadMessage(
            infoField.Bytes,
            limits,
            budget,
            depth: 2);
        ulong? accountValue = FirstVarint(info, 101);
        if (accountValue is null || accountValue > long.MaxValue)
        {
            // Older evidence can omit #101. Preserve the record but never guess
            // an identity from the unrelated outer result identifier.
            unknown.Add(CreateUnknown(resultsField, protobufBaseOffset, "root.301.unmapped"));
            return;
        }

        long accountId = (long)accountValue.Value;
        MutableParticipant participant = GetParticipant(participants, accountId);
        participant.Evidence ??= EvidenceFor(resultsField, protobufBaseOffset);
        foreach (ProtobufField field in fields)
        {
            if (field.Number is not 1 and not 2)
            {
                unknown.Add(CreateUnknown(field, resultBase, "root.301"));
            }
        }

        foreach (ProtobufField field in info)
        {
            switch (field.Number, field.WireType)
            {
                case (101, ProtobufWireType.Varint):
                    break;
                case (102, ProtobufWireType.Varint):
                    participant.TeamNumber = NormalizeTeam(field.NumericValue) ??
                        participant.TeamNumber;
                    break;
                case (103, ProtobufWireType.Varint) when field.NumericValue <= int.MaxValue:
                    participant.TankCompactDescriptor = (int)field.NumericValue.Value;
                    break;
                default:
                    unknown.Add(CreateUnknown(field, infoBase, "root.301.2"));
                    break;
            }
        }
    }

    private static ulong? FirstVarint(IReadOnlyList<ProtobufField> fields, int number) =>
        fields.FirstOrDefault(
            field => field.Number == number &&
                     field.WireType == ProtobufWireType.Varint)?.NumericValue;

    private static MutableParticipant GetParticipant(
        Dictionary<long, MutableParticipant> participants,
        long accountId)
    {
        if (!participants.TryGetValue(accountId, out MutableParticipant? participant))
        {
            participant = new MutableParticipant();
            participants.Add(accountId, participant);
        }

        return participant;
    }

    private static int? NormalizeTeam(ulong? value) =>
        value is 1 or 2 ? (int)value.Value : null;

    private static string ReadBoundedText(ReadOnlyMemory<byte> bytes, string field)
    {
        if (bytes.Length > 512)
        {
            throw new ReplayFormatException(
                "replay.participant_text_limit",
                $"The {field} exceeds the character byte limit.");
        }

        return ReplayBinary.DecodeUtf8(bytes.Span, field);
    }

    private static DateTimeOffset? TryUnixTime(ulong value)
    {
        if (value > long.MaxValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static BinaryEvidence EvidenceFor(ProtobufField field, int baseOffset) =>
        new(
            ReplayFormatConstants.BattleResultsEntry,
            checked(baseOffset + field.Offset),
            field.EncodedLength,
            field.EncodedBytes);

    private static UnknownProtobufEvidence CreateUnknown(
        ProtobufField field,
        int baseOffset,
        string path) =>
        new(
            field.Number,
            field.WireType,
            path,
            new BinaryEvidence(
                ReplayFormatConstants.BattleResultsEntry,
                checked(baseOffset + field.Offset),
                field.EncodedLength,
                field.EncodedBytes));

    private sealed class MutableParticipant
    {
        public string? PlayerName { get; set; }

        public string? ClanTag { get; set; }

        public int? TeamNumber { get; set; }

        public int? TankCompactDescriptor { get; set; }

        public BinaryEvidence? Evidence { get; set; }
    }
}
