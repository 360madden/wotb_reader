using WotBTreader.Application.Replay;
using WotBTreader.TestSupport;

namespace WotBTreader.Replays.Tests;

[TestClass]
public sealed class BinaryReaderSecurityTests
{
    [TestMethod]
    public void RestrictedPickleReadsOnlyExpectedTuple()
    {
        byte[] protobuf = [0x08, 0x01];
        BattleResultsEnvelope envelope =
            RestrictedPickleReader.ReadBattleResultsEnvelope(
                SyntheticReplayFactory.CreatePickle(protobuf),
                DecoderLimits.Default);

        Assert.AreEqual<ulong>(42, envelope.ArenaIdentity);
        CollectionAssert.AreEqual(protobuf, envelope.Protobuf.ToArray());
    }

    [TestMethod]
    public void RestrictedPickleRejectsCodeLoadingOpcode()
    {
        byte[] unsafePickle =
        [
            0x80, 0x02,
            0x63, // GLOBAL: deliberately unsupported, never resolved.
            (byte)'o', (byte)'s', (byte)'\n',
            (byte)'s', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'\n',
            0x2e,
        ];

        ReplayFormatException exception = Assert.ThrowsExactly<ReplayFormatException>(
            () => RestrictedPickleReader.ReadBattleResultsEnvelope(
                unsafePickle,
                DecoderLimits.Default));
        Assert.AreEqual("replay.unsafe_pickle_opcode", exception.Code);
    }

    [TestMethod]
    public void RestrictedPickleRejectsTruncatedBinary()
    {
        byte[] truncated =
        [
            0x80, 0x02,
            0x4b, 0x01,
            0x54, 0x10, 0x00, 0x00, 0x00,
            0x01,
        ];

        ReplayFormatException exception = Assert.ThrowsExactly<ReplayFormatException>(
            () => RestrictedPickleReader.ReadBattleResultsEnvelope(
                truncated,
                DecoderLimits.Default));
        Assert.AreEqual("replay.invalid_pickle", exception.Code);
    }

    [TestMethod]
    public void ProtobufReaderPreservesOffsetsAndUnknownWireValues()
    {
        byte[] message =
        [
            0x08, 0x96, 0x01,
            0x15, 0x78, 0x56, 0x34, 0x12,
            0x1a, 0x03, 0x61, 0x62, 0x63,
        ];
        ProtobufBudget budget = new(10);

        IReadOnlyList<ProtobufField> fields = ProtobufWireReader.ReadMessage(
            message,
            DecoderLimits.Default,
            budget);

        Assert.HasCount(3, fields);
        Assert.AreEqual<ulong>(150, fields[0].NumericValue!.Value);
        Assert.AreEqual(3, fields[2].ValueLength);
        Assert.AreEqual(8, fields[2].Offset);
        CollectionAssert.AreEqual("abc"u8.ToArray(), fields[2].Bytes.ToArray());
    }

    [TestMethod]
    public void ProtobufReaderRejectsUnterminatedVarint()
    {
        byte[] message = [0x08, 0x80];

        ReplayFormatException exception = Assert.ThrowsExactly<ReplayFormatException>(
            () => ProtobufWireReader.ReadMessage(
                message,
                DecoderLimits.Default,
                new ProtobufBudget(10)));
        Assert.AreEqual("replay.truncated_protobuf", exception.Code);
    }

    [TestMethod]
    public void ProtobufReaderEnforcesFieldBudget()
    {
        byte[] message = [0x08, 0x01, 0x10, 0x02];

        ReplayFormatException exception = Assert.ThrowsExactly<ReplayFormatException>(
            () => ProtobufWireReader.ReadMessage(
                message,
                DecoderLimits.Default,
                new ProtobufBudget(1)));
        Assert.AreEqual("replay.protobuf_field_limit", exception.Code);
    }

    [TestMethod]
    public void EventScannerAcceptsDocumentedEndSentinel()
    {
        byte[] eventStream = SyntheticReplayFactory.CreateEventStream(
            insertMalformedGap: false,
            includeEndSentinel: true);

        EventStreamScan scan = EventStreamReader.Scan(
            eventStream,
            DecoderLimits.Default,
            TimeSpan.FromSeconds(120),
            CancellationToken.None);

        Assert.IsEmpty(scan.Gaps);
        Assert.AreEqual(uint.MaxValue, scan.Packets[^1].Type);
    }
}
