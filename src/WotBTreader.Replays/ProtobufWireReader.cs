using System.Buffers.Binary;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal enum ProtobufWireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

internal sealed record ProtobufField(
    int Number,
    ProtobufWireType WireType,
    int Offset,
    int EncodedLength,
    int ValueOffset,
    int ValueLength,
    ulong? NumericValue,
    ReadOnlyMemory<byte> Bytes,
    ReadOnlyMemory<byte> EncodedBytes);

internal sealed class ProtobufBudget
{
    private readonly int _maximumFields;

    public ProtobufBudget(int maximumFields)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFields);
        _maximumFields = maximumFields;
    }

    public int FieldCount { get; private set; }

    public void ConsumeField()
    {
        if (++FieldCount > _maximumFields)
        {
            throw new ReplayFormatException(
                "replay.protobuf_field_limit",
                "A protobuf message exceeds the field-count limit.");
        }
    }
}

/// <summary>
/// Parses the generic protobuf wire grammar without applying a generated
/// schema. Each field retains its encoded offset and bytes so unrecognized
/// values remain evidence rather than being discarded.
/// </summary>
internal static class ProtobufWireReader
{
    private const int MaximumFieldNumber = (1 << 29) - 1;

    public static IReadOnlyList<ProtobufField> ReadMessage(
        ReadOnlyMemory<byte> message,
        DecoderLimits limits,
        ProtobufBudget budget,
        int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(budget);
        if (depth < 0 || depth > limits.MaximumNestingDepth)
        {
            throw new ReplayFormatException(
                "replay.protobuf_depth_limit",
                "A protobuf message exceeds the nesting-depth limit.");
        }

        if (message.Length > limits.MaximumEntryBytes)
        {
            throw new ReplayFormatException(
                "replay.protobuf_message_limit",
                "A protobuf message exceeds the byte limit.");
        }

        List<ProtobufField> fields = [];
        int offset = 0;
        while (offset < message.Length)
        {
            int fieldStart = offset;
            ulong tag = ReadVarint(message.Span, ref offset);
            ulong rawFieldNumber = tag >> 3;
            if (rawFieldNumber is 0 or > MaximumFieldNumber)
            {
                throw new ReplayFormatException(
                    "replay.invalid_protobuf_tag",
                    "A protobuf field number is outside the valid range.");
            }

            int fieldNumber = (int)rawFieldNumber;
            ProtobufWireType wireType = (ProtobufWireType)(tag & 0x07);
            if (wireType is > ProtobufWireType.Fixed32)
            {
                throw new ReplayFormatException(
                    "replay.invalid_protobuf_wire_type",
                    "A protobuf field uses an invalid wire type.");
            }

            budget.ConsumeField();
            int valueOffset = offset;
            int valueLength;
            ulong? numeric = null;
            ReadOnlyMemory<byte> valueBytes = ReadOnlyMemory<byte>.Empty;
            switch (wireType)
            {
                case ProtobufWireType.Varint:
                    int numericStart = offset;
                    numeric = ReadVarint(message.Span, ref offset);
                    valueOffset = numericStart;
                    valueLength = offset - numericStart;
                    valueBytes = message.Slice(valueOffset, valueLength);
                    break;
                case ProtobufWireType.Fixed64:
                    EnsureAvailable(message.Span, offset, sizeof(ulong));
                    numeric = BinaryPrimitives.ReadUInt64LittleEndian(message.Span[offset..]);
                    valueLength = sizeof(ulong);
                    valueBytes = message.Slice(offset, valueLength);
                    offset += valueLength;
                    break;
                case ProtobufWireType.LengthDelimited:
                    ulong declaredLength = ReadVarint(message.Span, ref offset);
                    if (declaredLength > int.MaxValue ||
                        declaredLength > (ulong)limits.MaximumEntryBytes)
                    {
                        throw new ReplayFormatException(
                            "replay.protobuf_length_limit",
                            "A protobuf length-delimited field exceeds the byte limit.");
                    }

                    valueOffset = offset;
                    valueLength = checked((int)declaredLength);
                    EnsureAvailable(message.Span, offset, valueLength);
                    valueBytes = message.Slice(offset, valueLength);
                    offset += valueLength;
                    break;
                case ProtobufWireType.StartGroup:
                    if (depth >= limits.MaximumNestingDepth)
                    {
                        throw new ReplayFormatException(
                            "replay.protobuf_depth_limit",
                            "A protobuf group exceeds the nesting-depth limit.");
                    }

                    valueOffset = offset;
                    int endTagStart = SkipGroup(
                        message.Span,
                        ref offset,
                        fieldNumber,
                        limits,
                        budget,
                        depth + 1);
                    valueLength = endTagStart - valueOffset;
                    valueBytes = message.Slice(valueOffset, valueLength);
                    break;
                case ProtobufWireType.EndGroup:
                    throw new ReplayFormatException(
                        "replay.unexpected_protobuf_end_group",
                        "A protobuf message contains an unmatched end-group tag.");
                case ProtobufWireType.Fixed32:
                    EnsureAvailable(message.Span, offset, sizeof(uint));
                    numeric = BinaryPrimitives.ReadUInt32LittleEndian(message.Span[offset..]);
                    valueLength = sizeof(uint);
                    valueBytes = message.Slice(offset, valueLength);
                    offset += valueLength;
                    break;
                default:
                    throw new ReplayFormatException(
                        "replay.invalid_protobuf_wire_type",
                        "A protobuf field uses an invalid wire type.");
            }

            fields.Add(new ProtobufField(
                fieldNumber,
                wireType,
                fieldStart,
                offset - fieldStart,
                valueOffset,
                valueLength,
                numeric,
                valueBytes,
                message.Slice(fieldStart, offset - fieldStart)));
        }

        return fields;
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = 0;
        for (int index = 0; index < 10; index++)
        {
            EnsureAvailable(bytes, offset, 1);
            byte current = bytes[offset++];
            if (index == 9 && current > 1)
            {
                throw new ReplayFormatException(
                    "replay.protobuf_varint_overflow",
                    "A protobuf varint exceeds 64 bits.");
            }

            value |= (ulong)(current & 0x7f) << (index * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new ReplayFormatException(
            "replay.protobuf_varint_overflow",
            "A protobuf varint exceeds ten bytes.");
    }

    private static int SkipGroup(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int expectedFieldNumber,
        DecoderLimits limits,
        ProtobufBudget budget,
        int depth)
    {
        while (offset < bytes.Length)
        {
            int tagStart = offset;
            ulong tag = ReadVarint(bytes, ref offset);
            ulong rawFieldNumber = tag >> 3;
            ProtobufWireType wireType = (ProtobufWireType)(tag & 0x07);
            if (rawFieldNumber is 0 or > MaximumFieldNumber ||
                wireType is > ProtobufWireType.Fixed32)
            {
                throw new ReplayFormatException(
                    "replay.invalid_protobuf_tag",
                    "A protobuf group contains an invalid field tag.");
            }

            int fieldNumber = (int)rawFieldNumber;
            budget.ConsumeField();
            switch (wireType)
            {
                case ProtobufWireType.Varint:
                    ReadVarint(bytes, ref offset);
                    break;
                case ProtobufWireType.Fixed64:
                    EnsureAvailable(bytes, offset, sizeof(ulong));
                    offset += sizeof(ulong);
                    break;
                case ProtobufWireType.LengthDelimited:
                    ulong length = ReadVarint(bytes, ref offset);
                    if (length > int.MaxValue || length > (ulong)limits.MaximumEntryBytes)
                    {
                        throw new ReplayFormatException(
                            "replay.protobuf_length_limit",
                            "A protobuf group field exceeds the byte limit.");
                    }

                    EnsureAvailable(bytes, offset, (int)length);
                    offset += (int)length;
                    break;
                case ProtobufWireType.StartGroup:
                    if (depth >= limits.MaximumNestingDepth)
                    {
                        throw new ReplayFormatException(
                            "replay.protobuf_depth_limit",
                            "A protobuf group exceeds the nesting-depth limit.");
                    }

                    SkipGroup(bytes, ref offset, fieldNumber, limits, budget, depth + 1);
                    break;
                case ProtobufWireType.EndGroup:
                    if (fieldNumber != expectedFieldNumber)
                    {
                        throw new ReplayFormatException(
                            "replay.protobuf_group_mismatch",
                            "A protobuf end-group tag does not match its start tag.");
                    }

                    return tagStart;
                case ProtobufWireType.Fixed32:
                    EnsureAvailable(bytes, offset, sizeof(uint));
                    offset += sizeof(uint);
                    break;
                default:
                    throw new ReplayFormatException(
                        "replay.invalid_protobuf_wire_type",
                        "A protobuf group uses an invalid wire type.");
            }
        }

        throw new ReplayFormatException(
            "replay.truncated_protobuf_group",
            "A protobuf group is missing its end tag.");
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
        {
            throw new ReplayFormatException(
                "replay.truncated_protobuf",
                "A protobuf field ended before its declared value.");
        }
    }
}
