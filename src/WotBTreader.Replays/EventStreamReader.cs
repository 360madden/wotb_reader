using System.Buffers.Binary;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record EventStreamHeader(
    string ClientHash,
    string ClientVersion,
    int EncodedLength);

internal sealed record EventPacket(
    long Ordinal,
    int Offset,
    int EncodedLength,
    uint Type,
    float ClockSeconds,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> EncodedBytes);

internal sealed record EventStreamGap(
    long Ordinal,
    int Offset,
    int Length,
    ReadOnlyMemory<byte> Bytes,
    string Reason);

internal sealed record EventStreamScan(
    EventStreamHeader Header,
    IReadOnlyList<EventPacket> Packets,
    IReadOnlyList<EventStreamGap> Gaps,
    int ResynchronizationCount,
    IReadOnlyList<string> Warnings);

internal static class EventStreamReader
{
    public static EventStreamHeader ReadHeader(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        ReplayBinary.EnsureAvailable(bytes, 0, 12);
        uint magic = ReplayBinary.ReadUInt32(bytes, 0);
        if (magic != ReplayFormatConstants.EventStreamMagic)
        {
            throw new ReplayFormatException(
                "replay.invalid_event_magic",
                "data.wotreplay has an invalid magic value.");
        }

        int offset = 12;
        string clientHash = ReadLengthPrefixedString(bytes, ref offset, "client hash", 128);
        string clientVersion = ReadLengthPrefixedString(bytes, ref offset, "client version", 128);
        ReplayBinary.EnsureAvailable(bytes, offset, 1);
        offset++;
        return new EventStreamHeader(clientHash, clientVersion, offset);
    }

    public static bool IsCompatibleStreamVersion(string version)
    {
        string normalized = WotbReplayDecoder.NormalizeVersion(version);
        return string.Equals(normalized, "11.18.0", StringComparison.Ordinal) ||
               string.Equals(normalized, "11.19.0", StringComparison.Ordinal);
    }

    public static EventStreamScan Scan(
        ReadOnlyMemory<byte> data,
        DecoderLimits limits,
        TimeSpan? expectedDuration,
        CancellationToken cancellationToken)
    {
        EventStreamHeader header = ReadHeader(data);
        int maximumPacketBytes = Math.Min(
            limits.MaximumPacketBytes,
            ReplayFormatConstants.MaximumStrictPacketBytes);
        int maximumPacketCount = Math.Min(
            limits.MaximumPacketCount,
            ReplayFormatConstants.MaximumStrictPacketCount);
        if (maximumPacketBytes <= 0 ||
            maximumPacketCount <= 0 ||
            limits.MaximumResynchronizationBytes < 0)
        {
            throw new ReplayFormatException(
                "replay.invalid_limits",
                "Packet decoder limits must be positive.");
        }

        double expectedSeconds = expectedDuration?.TotalSeconds ?? 0;
        float maximumClock = (float)Math.Clamp(
            Math.Max(expectedSeconds + 60, 600),
            600,
            3_600);

        List<EventPacket> packets = [];
        List<EventStreamGap> gaps = [];
        List<string> warnings = [];
        int offset = header.EncodedLength;
        int resynchronizedBytes = 0;
        int resynchronizationCount = 0;
        float previousClock = 0;
        long ordinal = 0;
        int cancellationStride = 0;

        while (offset < data.Length)
        {
            if ((++cancellationStride & 0x3ff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (packets.Count >= maximumPacketCount)
            {
                throw new ReplayFormatException(
                    "replay.packet_count_limit",
                    "data.wotreplay exceeds the packet-count limit.");
            }

            if (TryReadPacket(
                    data,
                    offset,
                    maximumPacketBytes,
                    previousClock,
                    maximumClock,
                    out EventPacket? packet))
            {
                EventPacket decodedPacket = packet!;
                packets.Add(decodedPacket with { Ordinal = ordinal++ });
                previousClock = decodedPacket.ClockSeconds;
                offset += decodedPacket.EncodedLength;
                continue;
            }

            int invalidStart = offset;
            int candidate = FindResynchronization(
                data,
                offset + 1,
                maximumPacketBytes,
                previousClock,
                maximumClock,
                limits.MaximumResynchronizationBytes - resynchronizedBytes,
                cancellationToken);
            if (candidate < 0)
            {
                int remaining = data.Length - invalidStart;
                gaps.Add(new EventStreamGap(
                    ordinal++,
                    invalidStart,
                    remaining,
                    data.Slice(invalidStart, remaining),
                    "unrecoverable-tail"));
                warnings.Add("The event stream ended with malformed data that could not be resynchronized.");
                break;
            }

            int skipped = candidate - invalidStart;
            resynchronizedBytes = checked(resynchronizedBytes + skipped);
            resynchronizationCount++;
            TreaderDiagnostics.PacketResynchronizations.Add(1);
            gaps.Add(new EventStreamGap(
                ordinal++,
                invalidStart,
                skipped,
                data.Slice(invalidStart, skipped),
                "resynchronization-gap"));
            warnings.Add(
                FormattableString.Invariant(
                    $"The event stream resynchronized after {skipped} malformed byte(s)."));
            offset = candidate;
        }

        return new EventStreamScan(
            header,
            packets,
            gaps,
            resynchronizationCount,
            warnings);
    }

    private static bool TryReadPacket(
        ReadOnlyMemory<byte> data,
        int offset,
        int maximumPacketBytes,
        float previousClock,
        float maximumClock,
        out EventPacket? packet)
    {
        packet = null;
        if (offset < 0 || data.Length - offset < 12)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = data.Span;
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
        float clock = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 8)..]));
        if (declaredLength > maximumPacketBytes ||
            declaredLength > int.MaxValue)
        {
            return false;
        }

        int encodedLength;
        try
        {
            encodedLength = checked(12 + (int)declaredLength);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (encodedLength > data.Length - offset)
        {
            return false;
        }

        // 11.18 writes one EOF-aligned 0xffffffff packet with clock zero after
        // the battle timeline. It is a real framed record, not corruption.
        bool isEndSentinel =
            type == uint.MaxValue &&
            clock == 0 &&
            encodedLength == data.Length - offset;
        if (!isEndSentinel &&
            (type > byte.MaxValue ||
             !float.IsFinite(clock) ||
             clock < 0 ||
             clock > maximumClock ||
             clock + 1.0f < previousClock))
        {
            return false;
        }

        packet = new EventPacket(
            Ordinal: 0,
            Offset: offset,
            EncodedLength: encodedLength,
            Type: type,
            ClockSeconds: clock,
            Payload: data.Slice(offset + 12, (int)declaredLength),
            EncodedBytes: data.Slice(offset, encodedLength));
        return true;
    }

    private static int FindResynchronization(
        ReadOnlyMemory<byte> data,
        int start,
        int maximumPacketBytes,
        float previousClock,
        float maximumClock,
        int remainingBudget,
        CancellationToken cancellationToken)
    {
        if (remainingBudget <= 0)
        {
            return -1;
        }

        int limit = Math.Min(data.Length - 12, checked(start + remainingBudget));
        for (int candidate = start; candidate <= limit; candidate++)
        {
            if (((candidate - start) & 0x3ff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!TryReadPacket(
                    data,
                    candidate,
                    maximumPacketBytes,
                    previousClock,
                    maximumClock,
                    out EventPacket? first))
            {
                continue;
            }

            EventPacket decodedFirst = first!;
            int nextOffset = checked(candidate + decodedFirst.EncodedLength);
            if (nextOffset == data.Length ||
                (data.Length - nextOffset >= 12 &&
                 TryReadPacket(
                     data,
                     nextOffset,
                     maximumPacketBytes,
                     decodedFirst.ClockSeconds,
                     maximumClock,
                     out _)))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static string ReadLengthPrefixedString(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        string field,
        int maximumBytes)
    {
        ReplayBinary.EnsureAvailable(bytes, offset, 1);
        int length = bytes[offset++];
        if (length > maximumBytes)
        {
            throw new ReplayFormatException(
                "replay.event_header_limit",
                $"The event-stream {field} exceeds its byte limit.");
        }

        ReplayBinary.EnsureAvailable(bytes, offset, length);
        string value = ReplayBinary.DecodeUtf8(bytes.Slice(offset, length), field);
        offset += length;
        return value;
    }
}
