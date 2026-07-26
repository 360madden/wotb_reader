using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record BattleResultsEnvelope(
    ulong ArenaIdentity,
    ReadOnlyMemory<byte> Protobuf,
    int ProtobufOffset);

/// <summary>
/// Reads only passive scalar/tuple opcodes. Code-loading and callable pickle
/// opcodes are deliberately absent, so an embedded GLOBAL/REDUCE sequence can
/// never execute or resolve a runtime type.
/// </summary>
internal static class RestrictedPickleReader
{
    private static readonly object Mark = new();

    public static BattleResultsEnvelope ReadBattleResultsEnvelope(
        ReadOnlyMemory<byte> pickle,
        DecoderLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ReadOnlySpan<byte> bytes = pickle.Span;
        if (bytes.Length < 3 || bytes[0] != 0x80 || bytes[1] != 0x02)
        {
            throw new ReplayFormatException(
                "replay.unsupported_pickle_protocol",
                "battle_results.dat must use Python pickle protocol 2.");
        }

        List<object> stack = [];
        Dictionary<int, object> memo = [];
        int offset = 2;
        int opcodeCount = 1;
        bool stopped = false;
        object? result = null;
        while (offset < bytes.Length)
        {
            if (++opcodeCount > ReplayFormatConstants.MaximumPickleOpcodes)
            {
                throw new ReplayFormatException(
                    "replay.pickle_opcode_limit",
                    "battle_results.dat exceeds the pickle opcode limit.");
            }

            byte opcode = bytes[offset++];
            switch (opcode)
            {
                case 0x2e: // STOP
                    if (stack.Count != 1)
                    {
                        throw InvalidPickle("Pickle STOP did not leave exactly one value.");
                    }

                    result = stack[0];
                    stopped = true;
                    break;
                case 0x28: // MARK
                    Push(stack, Mark);
                    break;
                case 0x29: // EMPTY_TUPLE
                    Push(stack, Array.Empty<object>());
                    break;
                case 0x4a: // BININT
                    Ensure(bytes, offset, sizeof(int));
                    Push(stack, new IntegerValue(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..])));
                    offset += sizeof(int);
                    break;
                case 0x4b: // BININT1
                    Ensure(bytes, offset, 1);
                    Push(stack, new IntegerValue(bytes[offset++]));
                    break;
                case 0x4d: // BININT2
                    Ensure(bytes, offset, sizeof(ushort));
                    Push(stack, new IntegerValue(BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..])));
                    offset += sizeof(ushort);
                    break;
                case 0x49: // INT
                    Push(stack, ReadAsciiInteger(bytes, ref offset, allowLongSuffix: false));
                    break;
                case 0x4c: // LONG
                    Push(stack, ReadAsciiInteger(bytes, ref offset, allowLongSuffix: true));
                    break;
                case 0x8a: // LONG1
                    Ensure(bytes, offset, 1);
                    int shortLongLength = bytes[offset++];
                    Push(stack, ReadBinaryInteger(bytes, ref offset, shortLongLength));
                    break;
                case 0x8b: // LONG4
                    Ensure(bytes, offset, sizeof(int));
                    int longLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
                    offset += sizeof(int);
                    Push(stack, ReadBinaryInteger(bytes, ref offset, longLength));
                    break;
                case 0x54: // BINSTRING (Python 2 bytes)
                case 0x42: // BINBYTES
                    Ensure(bytes, offset, sizeof(uint));
                    uint binaryLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                    offset += sizeof(uint);
                    Push(stack, ReadBinary(bytes, ref offset, binaryLength, limits));
                    break;
                case 0x55: // SHORT_BINSTRING (Python 2 bytes)
                case 0x43: // SHORT_BINBYTES
                    Ensure(bytes, offset, 1);
                    Push(stack, ReadBinary(bytes, ref offset, bytes[offset++], limits));
                    break;
                case 0x8e: // BINBYTES8
                    Ensure(bytes, offset, sizeof(ulong));
                    ulong binaryLength64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
                    offset += sizeof(ulong);
                    Push(stack, ReadBinary(bytes, ref offset, binaryLength64, limits));
                    break;
                case 0x74: // TUPLE
                    PushMarkedTuple(stack);
                    break;
                case 0x85: // TUPLE1
                    PushFixedTuple(stack, 1);
                    break;
                case 0x86: // TUPLE2
                    PushFixedTuple(stack, 2);
                    break;
                case 0x87: // TUPLE3
                    PushFixedTuple(stack, 3);
                    break;
                case 0x71: // BINPUT
                    Ensure(bytes, offset, 1);
                    StoreMemo(stack, memo, bytes[offset++]);
                    break;
                case 0x72: // LONG_BINPUT
                    Ensure(bytes, offset, sizeof(uint));
                    uint memoIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                    offset += sizeof(uint);
                    StoreMemo(stack, memo, ToBoundedInt(memoIndex, "pickle memo index"));
                    break;
                case 0x68: // BINGET
                    Ensure(bytes, offset, 1);
                    LoadMemo(stack, memo, bytes[offset++]);
                    break;
                case 0x6a: // LONG_BINGET
                    Ensure(bytes, offset, sizeof(uint));
                    uint getIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                    offset += sizeof(uint);
                    LoadMemo(stack, memo, ToBoundedInt(getIndex, "pickle memo index"));
                    break;
                case 0x30: // POP
                    Pop(stack);
                    break;
                case 0x32: // DUP
                    Push(stack, Peek(stack));
                    break;
                default:
                    throw new ReplayFormatException(
                        "replay.unsafe_pickle_opcode",
                        $"battle_results.dat contains disallowed pickle opcode 0x{opcode:x2}.");
            }

            if (stopped)
            {
                break;
            }
        }

        if (!stopped || offset != bytes.Length)
        {
            throw InvalidPickle(
                stopped
                    ? "Pickle data contains trailing bytes."
                    : "Pickle data is missing STOP.");
        }

        if (result is not object[] { Length: 2 } tuple ||
            tuple[0] is not IntegerValue arena ||
            tuple[1] is not BinaryValue protobuf)
        {
            throw InvalidPickle(
                "battle_results.dat must contain exactly (arena integer, protobuf bytes).");
        }

        if (arena.Value < BigInteger.Zero || arena.Value > ulong.MaxValue)
        {
            throw InvalidPickle("The pickle arena identifier is outside the unsigned 64-bit range.");
        }

        return new BattleResultsEnvelope(
            (ulong)arena.Value,
            protobuf.Bytes,
            protobuf.Offset);
    }

    private static BinaryValue ReadBinary(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        ulong declaredLength,
        DecoderLimits limits)
    {
        long maximum = Math.Min(
            limits.MaximumEntryBytes,
            ReplayFormatConstants.MaximumBattleResultsBytes);
        if (declaredLength > (ulong)maximum || declaredLength > int.MaxValue)
        {
            throw new ReplayFormatException(
                "replay.pickle_binary_limit",
                "A pickle byte string exceeds the binary-size limit.");
        }

        int length = checked((int)declaredLength);
        Ensure(bytes, offset, length);
        BinaryValue value = new(bytes.Slice(offset, length).ToArray(), offset);
        offset += length;
        return value;
    }

    private static IntegerValue ReadBinaryInteger(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int length)
    {
        if (length < 0 || length > ReplayFormatConstants.MaximumPickleLongBytes)
        {
            throw new ReplayFormatException(
                "replay.pickle_integer_limit",
                "A pickle integer exceeds the integer byte limit.");
        }

        Ensure(bytes, offset, length);
        BigInteger value = length == 0
            ? BigInteger.Zero
            : new BigInteger(bytes.Slice(offset, length), isUnsigned: false, isBigEndian: false);
        offset += length;
        return new IntegerValue(value);
    }

    private static IntegerValue ReadAsciiInteger(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        bool allowLongSuffix)
    {
        int start = offset;
        int endLimit = Math.Min(
            bytes.Length,
            checked(start + ReplayFormatConstants.MaximumPickleLongBytes + 1));
        while (offset < endLimit && bytes[offset] != (byte)'\n')
        {
            offset++;
        }

        if (offset >= bytes.Length || bytes[offset] != (byte)'\n')
        {
            throw InvalidPickle("A text pickle integer is unterminated or too long.");
        }

        ReadOnlySpan<byte> textBytes = bytes[start..offset++];
        if (allowLongSuffix && !textBytes.IsEmpty && textBytes[^1] == (byte)'L')
        {
            textBytes = textBytes[..^1];
        }

        if (textBytes.Length == 0 ||
            textBytes.Length > ReplayFormatConstants.MaximumPickleLongBytes)
        {
            throw InvalidPickle("A text pickle integer has an invalid length.");
        }

        string text = Encoding.ASCII.GetString(textBytes);
        if (!BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value))
        {
            throw InvalidPickle("A text pickle integer is invalid.");
        }

        return new IntegerValue(value);
    }

    private static void PushMarkedTuple(List<object> stack)
    {
        int markIndex = stack.LastIndexOf(Mark);
        if (markIndex < 0)
        {
            throw InvalidPickle("TUPLE has no matching MARK.");
        }

        object[] values = stack.Skip(markIndex + 1).ToArray();
        stack.RemoveRange(markIndex, stack.Count - markIndex);
        Push(stack, values);
    }

    private static void PushFixedTuple(List<object> stack, int count)
    {
        if (stack.Count < count)
        {
            throw InvalidPickle("A fixed tuple underflowed the pickle stack.");
        }

        object[] values = stack.GetRange(stack.Count - count, count).ToArray();
        stack.RemoveRange(stack.Count - count, count);
        Push(stack, values);
    }

    private static void StoreMemo(List<object> stack, Dictionary<int, object> memo, int index)
    {
        if (index < 0 || index >= ReplayFormatConstants.MaximumPickleStackDepth)
        {
            throw new ReplayFormatException(
                "replay.pickle_memo_limit",
                "The pickle memo index exceeds the limit.");
        }

        memo[index] = Peek(stack);
    }

    private static void LoadMemo(List<object> stack, Dictionary<int, object> memo, int index)
    {
        if (!memo.TryGetValue(index, out object? value))
        {
            throw InvalidPickle("The pickle references an undefined memo value.");
        }

        Push(stack, value);
    }

    private static void Push(List<object> stack, object value)
    {
        if (stack.Count >= ReplayFormatConstants.MaximumPickleStackDepth)
        {
            throw new ReplayFormatException(
                "replay.pickle_stack_limit",
                "battle_results.dat exceeds the pickle stack-depth limit.");
        }

        stack.Add(value);
    }

    private static object Pop(List<object> stack)
    {
        object value = Peek(stack);
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static object Peek(List<object> stack) =>
        stack.Count == 0
            ? throw InvalidPickle("The pickle stack underflowed.")
            : stack[^1];

    private static int ToBoundedInt(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw InvalidPickle($"The {field} exceeds the supported range.");
        }

        return (int)value;
    }

    private static void Ensure(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (length < 0 || offset < 0 || offset > bytes.Length - length)
        {
            throw InvalidPickle("Pickle data ended before the declared value.");
        }
    }

    private static ReplayFormatException InvalidPickle(string detail) =>
        new("replay.invalid_pickle", detail);

    private sealed record IntegerValue(BigInteger Value)
    {
        public IntegerValue(long value)
            : this(new BigInteger(value))
        {
        }
    }

    private sealed record BinaryValue(byte[] Bytes, int Offset);
}
