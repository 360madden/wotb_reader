using WotBTreader.Application.Replay;
using WotBTreader.Core;
using WotBTreader.TestSupport;

namespace WotBTreader.Replays.Tests;

[TestClass]
public sealed class ReplayDecoderTests
{
    [TestMethod]
    public async Task ValidReplayDecodesEvidenceBackedRosterAndPositions()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay());
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);

        Assert.IsTrue(probeResult.IsSuccess, probeResult.Error?.Message);
        Assert.IsNotNull(probeResult.Value);
        WotbReplayDecoder decoder = new();
        var decodeResult = await decoder.DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value,
                DecoderLimits.Default),
            CancellationToken.None);

        Assert.IsTrue(decodeResult.IsSuccess, decodeResult.Error?.Message);
        ReplayDecodeProjection projection = decodeResult.Value!;
        Assert.HasCount(2, projection.Participants);
        Assert.HasCount(2, projection.Positions);
        Assert.IsTrue(
            projection.DecodeRun.Capabilities.HasFlag(ReplayCapability.EntityMapping));
        Assert.IsTrue(
            projection.DecodeRun.Capabilities.HasFlag(ReplayCapability.Positions));
        Assert.IsNotNull(projection.Session?.ViewpointParticipantId);
        Assert.IsTrue(projection.Participants.All(
            participant => participant.BotStatus == BotStatus.Unknown));
        Assert.AreEqual(1, projection.Participants.Count(
            participant => participant.AccountId is null));
        Assert.AreEqual(
            17,
            projection.Participants.Single(
                participant => participant.AccountId is null).VehicleCompactDescriptor);
        Assert.AreEqual(2, projection.Positions.Count(
            sample => sample.ParticipantId is not null));
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet"));
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task UnsupportedVersionIsReportedWithoutGuessing()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(version: "11.20.0"));
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);

        Assert.IsTrue(probeResult.IsSuccess);
        Assert.IsNotNull(probeResult.Value);
        Assert.IsFalse(new WotbReplayDecoder().CanDecode(probeResult.Value));
        Assert.IsTrue(probeResult.Warnings.Any(
            warning => warning.Contains("not supported", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DecodeResynchronizesDeterministicallyAndPreservesGap()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(insertMalformedGap: true));
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);
        Assert.IsTrue(probeResult.IsSuccess);

        var decodeResult = await new WotbReplayDecoder().DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value!,
                DecoderLimits.Default),
            CancellationToken.None);

        Assert.IsTrue(decodeResult.IsSuccess, decodeResult.Error?.Message);
        Assert.HasCount(2, decodeResult.Value!.Positions);
        RawRecord gap = decodeResult.Value.RawRecords.Single(
            record => record.RecordKind == "event-stream.gap");
        Assert.AreEqual(3, gap.Evidence.Length);
        Assert.AreEqual(64, gap.Evidence.Sha256.Value.Length);
    }

    [TestMethod]
    public async Task AccountlessParticipantRetainsUnknownBotStatus()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay());
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);
        var decodeResult = await new WotbReplayDecoder().DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value!,
                DecoderLimits.Default),
            CancellationToken.None);

        Participant accountless = decodeResult.Value!.Participants.Single(
            participant => participant.AccountId is null);
        Assert.AreEqual("unit-b", accountless.PlayerName);
        Assert.AreEqual(BotStatus.Unknown, accountless.BotStatus);
        Assert.AreEqual(EvidenceConfidence.Unknown, accountless.BotStatusConfidence);
    }
}
