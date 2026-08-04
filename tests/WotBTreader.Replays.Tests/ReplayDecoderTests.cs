using System.Text.Json;
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
        BattleStats? stats = projection.Participants.Single(
            participant => participant.AccountId == 42).BattleStats;
        Assert.IsNotNull(stats);
        Assert.AreEqual(1200, stats.CreditsEarned);
        Assert.AreEqual(850, stats.BaseXp);
        Assert.AreEqual(2340, stats.DamageDealt);
        Assert.AreEqual(300, stats.DamageAssisted1);
        Assert.AreEqual(120, stats.DamageAssisted2);
        Assert.AreEqual(2575.5f, stats.MmRating);
        Assert.AreEqual(410, stats.DamageBlocked);
        Assert.AreEqual(15, stats.Shots);
        Assert.AreEqual(9, stats.HitsDealt);
        Assert.AreEqual(5, stats.PenetrationsDealt);
        Assert.AreEqual(1, stats.EnemiesDestroyed);
        Assert.IsNull(projection.Participants.Single(
            participant => participant.AccountId is null).BattleStats,
            "A participant without player-results evidence must carry no stats.");
        Assert.AreEqual(2, projection.Positions.Count(
            sample => sample.ParticipantId is not null));
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet"));
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task BasePlayerCreatePacketDecodesAsTypedRawRecordWithArenaIdentity()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay());
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);
        Assert.IsTrue(probeResult.IsSuccess, probeResult.Error?.Message);

        WotbReplayDecoder decoder = new();
        var decodeResult = await decoder.DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value!,
                DecoderLimits.Default),
            CancellationToken.None);
        Assert.IsTrue(decodeResult.IsSuccess, decodeResult.Error?.Message);
        ReplayDecodeProjection projection = decodeResult.Value!;

        // The type-0 packet decodes into a typed raw record carrying the
        // BasePlayerCreate header (author nickname, arena unique id, arena
        // type id) rather than an opaque unknown packet.
        RawRecord? typed = projection.RawRecords.SingleOrDefault(
            record => record.RecordKind == "event-stream.packet" &&
                      record.PropertiesJson?.Contains(
                          "basePlayerCreate",
                          StringComparison.Ordinal) == true);
        Assert.IsNotNull(typed, "A BasePlayerCreate packet should decode as a typed raw record.");
        using JsonDocument document = JsonDocument.Parse(typed!.PropertiesJson!);
        JsonElement bpc = document.RootElement.GetProperty("basePlayerCreate");
        Assert.AreEqual("pilot-a", bpc.GetProperty("authorNickname").GetString());
        Assert.AreEqual(42UL, bpc.GetProperty("arenaUniqueId").GetUInt64());
        Assert.AreEqual(7u, bpc.GetProperty("arenaTypeId").GetUInt32());

        // The synthetic replay's meta.json, battle-results tuple and packet
        // header all carry arena unique id 42, so no identity warning fires.
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("arena identities", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BasePlayerCreateArenaIdentityMismatchEmitsWarning()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(basePlayerCreateArenaId: 999));
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);
        Assert.IsTrue(probeResult.IsSuccess, probeResult.Error?.Message);

        WotbReplayDecoder decoder = new();
        var decodeResult = await decoder.DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value!,
                DecoderLimits.Default),
            CancellationToken.None);
        Assert.IsTrue(decodeResult.IsSuccess, decodeResult.Error?.Message);

        // The packet header (999) disagrees with the battle-results tuple (42),
        // so the third arena-identity source flags the conflict as a warning.
        Assert.IsTrue(decodeResult.Value!.Warnings.Any(
            warning => warning.Contains(
                "BasePlayerCreate packet and battle-results arena identities",
                StringComparison.Ordinal)));
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
