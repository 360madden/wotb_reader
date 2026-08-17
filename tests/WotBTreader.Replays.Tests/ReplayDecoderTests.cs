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
        // The type-10 packet tail's rotation is persisted: the factory wrote
        // yaw 0.75f (radians) on the first packet's payload +36.
        PositionSample? firstPosition = projection.Positions
            .OrderBy(sample => sample.Sequence)
            .FirstOrDefault();
        Assert.IsNotNull(firstPosition);
        Assert.AreEqual(0.75, firstPosition!.Yaw!.Value, 1e-6);
        Assert.AreEqual(0.0, firstPosition!.Pitch!.Value, 1e-6);
        Assert.AreEqual(0.0, firstPosition!.Roll!.Value, 1e-6);
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet"));
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SpawnHealthFirstBroadcastPerEntityEmitsMaxHealthObserved()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(includeSpawnHealth: true));
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

        // First broadcast per roster entity = max HP; the later lower-health
        // re-broadcast (650) and the non-roster entity (999) must not emit.
        CanonicalEvent[] maxHealthEvents = projection.Events
            .Where(ev => ev.Kind == CanonicalEventKind.MaxHealthObserved)
            .OrderBy(ev => ev.EntityId)
            .ToArray();
        Assert.HasCount(2, maxHealthEvents);
        Assert.AreEqual(100, maxHealthEvents[0].EntityId);
        Assert.AreEqual(700, JsonDocument.Parse(maxHealthEvents[0].ValuesJson)
            .RootElement.GetProperty("maxHealth").GetInt32());
        Assert.AreEqual(200, maxHealthEvents[1].EntityId);
        Assert.AreEqual(500, JsonDocument.Parse(maxHealthEvents[1].ValuesJson)
            .RootElement.GetProperty("maxHealth").GetInt32());

        // The type-5 packets are also preserved as typed raw records.
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet" &&
                      record.PropertiesJson?.Contains(
                          "spawnHealth",
                          StringComparison.Ordinal) == true));
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task HealthChangeLedgerComputesDamageFromHpDeltas()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(
                includeSpawnHealth: true,
                includeHealthChange: true));
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

        // Ledger: 100 starts at 700, takes 100 -> 600 (attacker 200), then is
        // destroyed by 200 with 600 remaining (credited to the killer). 200
        // starts at 500, takes 50 -> 450 (attacker 100).
        CanonicalEvent[] damageEvents = projection.Events
            .Where(ev => ev.Kind == CanonicalEventKind.Damage)
            .OrderBy(ev => ev.Sequence)
            .ToArray();
        Assert.HasCount(3, damageEvents);

        (long Victim, long Attacker, int Damage)[] expected =
        [
            (100, 200, 100),
            (200, 100, 50),
            (100, 200, 600),
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            using JsonDocument values = JsonDocument.Parse(damageEvents[i].ValuesJson);
            Assert.AreEqual(
                expected[i].Victim,
                values.RootElement.GetProperty("victimEntityId").GetInt64());
            Assert.AreEqual(
                expected[i].Attacker,
                values.RootElement.GetProperty("attackerEntityId").GetInt64());
            Assert.AreEqual(
                expected[i].Damage,
                values.RootElement.GetProperty("damage").GetInt32());
        }

        // Per-attacker sums: attacker 200 dealt 100 + 600 = 700.
        long attacker200Total = damageEvents
            .Select(ev => JsonDocument.Parse(ev.ValuesJson))
            .Where(doc => doc.RootElement.GetProperty("attackerEntityId").GetInt64() == 200)
            .Sum(doc => doc.RootElement.GetProperty("damage").GetInt32());
        Assert.AreEqual(700, attacker200Total);

        // The 0xFFFD destroy marker also emits a Destroyed event for the
        // victim (the ledger destroy signal is the primary death instant).
        CanonicalEvent[] destroyed = projection.Events
            .Where(ev => ev.Kind == CanonicalEventKind.Destroyed)
            .ToArray();
        Assert.HasCount(1, destroyed);
        Assert.AreEqual(100, destroyed[0].EntityId);
        Assert.AreEqual(TimeSpan.FromSeconds(3.0), destroyed[0].ReplayTime);

        // Ledger invariants (the verify-hp-ledger.py checks, synthetic
        // form): every victim's taken <= its max health, and attacker-side
        // totals equal victim-side totals exactly.
        long takenBy100 = damageEvents
            .Where(ev => ev.EntityId == 100)
            .Sum(ev => JsonDocument.Parse(ev.ValuesJson).RootElement.GetProperty("damage").GetInt32());
        long takenBy200 = damageEvents
            .Where(ev => ev.EntityId == 200)
            .Sum(ev => JsonDocument.Parse(ev.ValuesJson).RootElement.GetProperty("damage").GetInt32());
        Assert.AreEqual(700, takenBy100); // max 700, destroyed -> 0 remaining
        Assert.AreEqual(50, takenBy200);  // max 500, still alive
        Assert.AreEqual(attacker200Total + 50, takenBy100 + takenBy200);

        // The health-change packets are preserved as typed raw records.
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet" &&
                      record.PropertiesJson?.Contains(
                          "healthChange",
                          StringComparison.Ordinal) == true));
        Assert.IsFalse(projection.Warnings.Any(
            warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ShotImpactMirrorDecodesPenetratingAndNonPenetratingHits()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(includeShotImpact: true));
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

        // Three damage-with-payload packets (2x `01 12` + 1x `01 11`) emit
        // ShotImpact events; the `01 02` short companion stays raw.
        CanonicalEvent[] impacts = projection.Events
            .Where(ev => ev.Kind == CanonicalEventKind.ShotImpact)
            .OrderBy(ev => ev.ReplayTime)
            .ToArray();
        Assert.HasCount(3, impacts);

        // t=4.0: `01 12` penetrating hit on 200 (result 0x03), attributed to
        // attacker 100 via the matching subtype-8 packet.
        using (JsonDocument values = JsonDocument.Parse(impacts[0].ValuesJson))
        {
            Assert.AreEqual(200, values.RootElement.GetProperty("victimEntityId").GetInt64());
            Assert.AreEqual(3, values.RootElement.GetProperty("hitResult").GetInt32());
            Assert.IsTrue(values.RootElement.GetProperty("penetrated").GetBoolean());
            Assert.AreEqual(100, values.RootElement.GetProperty("attackerEntityId").GetInt64());
            Assert.AreEqual(
                "a6a5e0a2a8b1",
                values.RootElement.GetProperty("shellSignatureHex").GetString());
        }

        // t=4.1: `01 12` non-penetrating bounce on 100 (result 0x00),
        // attributed to attacker 200.
        using (JsonDocument values = JsonDocument.Parse(impacts[1].ValuesJson))
        {
            Assert.AreEqual(100, values.RootElement.GetProperty("victimEntityId").GetInt64());
            Assert.AreEqual(0, values.RootElement.GetProperty("hitResult").GetInt32());
            Assert.IsFalse(values.RootElement.GetProperty("penetrated").GetBoolean());
            Assert.AreEqual(200, values.RootElement.GetProperty("attackerEntityId").GetInt64());
        }

        // t=4.2: `01 11` penetrating hit on 200 (result 0x03) with NO subtype-8
        // attribution — attackerEntityId stays null, never fabricated.
        using (JsonDocument values = JsonDocument.Parse(impacts[2].ValuesJson))
        {
            Assert.AreEqual(200, values.RootElement.GetProperty("victimEntityId").GetInt64());
            Assert.AreEqual(3, values.RootElement.GetProperty("hitResult").GetInt32());
            Assert.IsTrue(values.RootElement.GetProperty("penetrated").GetBoolean());
            Assert.AreEqual(
                JsonValueKind.Null,
                values.RootElement.GetProperty("attackerEntityId").ValueKind);
        }

        // The short companion (`01 02`) is not a damage-with-payload variant,
        // so it remains an unknown raw record (packetType 32) rather than
        // emitting a ShotImpact.
        Assert.IsTrue(projection.RawRecords.Any(
            record => record.RecordKind == "event-stream.packet" &&
                      record.PropertiesJson?.Contains(
                          "\"packetType\":32",
                          StringComparison.Ordinal) == true));
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
    public async Task DestroyMarkerEmitsSingleDestroyedEventPerRosterEntity()
    {
        ReplayInput input = SyntheticReplayFactory.CreateInput(
            SyntheticReplayFactory.CreateReplay(includeDestroyMarker: true));
        WotbReplayProbe probe = new();
        var probeResult = await probe.ProbeAsync(
            input,
            DecoderLimits.Default,
            CancellationToken.None);
        Assert.IsTrue(probeResult.IsSuccess, probeResult.Error?.Message);

        var decodeResult = await new WotbReplayDecoder().DecodeAsync(
            new ReplayDecodeRequest(
                input,
                DecodeRunId.New(),
                probeResult.Value!,
                DecoderLimits.Default),
            CancellationToken.None);
        Assert.IsTrue(decodeResult.IsSuccess, decodeResult.Error?.Message);

        CanonicalEvent[] destroyed = decodeResult.Value!.Events
            .Where(ev => ev.Kind == CanonicalEventKind.Destroyed)
            .ToArray();
        // Two markers fire for entity 100 (t=3.0 and t=3.1) but only the
        // first emits a Destroyed event; the marker for non-roster entity
        // 999 is ignored entirely.
        Assert.HasCount(1, destroyed);
        Assert.AreEqual(100L, destroyed[0].EntityId);
        Assert.AreEqual(TimeSpan.FromSeconds(3), destroyed[0].ReplayTime);
        Assert.IsNotNull(destroyed[0].ParticipantId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(destroyed[0].ValuesJson));
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
