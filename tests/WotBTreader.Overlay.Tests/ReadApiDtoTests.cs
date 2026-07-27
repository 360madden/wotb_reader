using System.Text.Json;
using WotBTreader.Overlay.Contracts;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class ReadApiDtoTests
{
    private const string SessionPageJson = """
        {
          "offset": 40,
          "limit": 20,
          "count": 137,
          "items": [
            {
              "decodeRun": {
                "decodeRunId": "0f8fad5b-d9cb-469f-a165-70867728950e",
                "sourceArtifactId": "9c9b5f84-7a1e-4c8e-9f3d-2d1f0a8c6b5e",
                "decoderId": "wotb-replay",
                "decoderVersion": "1.2.3",
                "schemaVersion": "1.0",
                "status": "failed",
                "capabilities": [ "battle-session", "positions" ],
                "startedAtUtc": "2026-07-26T10:00:00+00:00",
                "completedAtUtc": "2026-07-26T10:00:05.2500000+00:00",
                "failureCode": "source-truncated",
                "failureSummary": "synthetic failure summary"
              },
              "session": {
                "battleSessionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                "gameVersion": "11.4.0.1234",
                "arenaIdentity": "arena-4711",
                "mapId": "maps/05_synthetic_ridge",
                "mapName": "Synthetic Ridge",
                "battleTimeUtc": "2026-07-26T09:55:12.5000000+00:00",
                "duration": "0:03:38.4642200",
                "viewpointParticipantId": "participant-01"
              },
              "participantCount": 14,
              "positionCount": 1024,
              "eventCount": 57,
              "rawRecordCount": 4096
            }
          ]
        }
        """;

    private const string SessionDetailJson = """
        {
          "decodeRun": {
            "decodeRunId": "6b1c9e52-3d4a-4f7b-9c0d-1e2f3a4b5c6d",
            "sourceArtifactId": "aa10bb20-cc30-dd40-ee50-ff60aa70bb80",
            "decoderId": "wotb-replay",
            "decoderVersion": "1.2.3",
            "schemaVersion": "1.0",
            "status": "succeeded",
            "capabilities": [ "battle-session" ],
            "startedAtUtc": "2026-07-25T18:30:00+00:00",
            "completedAtUtc": "2026-07-25T18:30:02+00:00",
            "failureCode": null,
            "failureSummary": null
          },
          "session": {
            "battleSessionId": "5d2b7c9a-1e3f-4a5b-8c6d-7e8f9a0b1c2d",
            "gameVersion": null,
            "arenaIdentity": "arena-9001",
            "mapId": "maps/11_synthetic_valley",
            "mapName": "Synthetic Valley",
            "battleTimeUtc": "2026-07-25T18:00:00+00:00",
            "duration": "0:07:00",
            "viewpointParticipantId": null
          },
          "participants": [
            {
              "participantId": "participant-01",
              "entityId": 4242,
              "teamNumber": 2,
              "playerName": "synthetic-player-01",
              "clanTag": "SYN",
              "tankId": "synthetic-tank-01",
              "tankName": "Synthetic Medium",
              "tankClass": "mediumTank",
              "botStatus": "unknown",
              "botStatusConfidence": null
            }
          ],
          "positions": [
            {
              "participantId": "participant-01",
              "entityId": 4242,
              "sequence": 17,
              "replayTime": "0:01:02.5000000",
              "rawX": -12.5,
              "rawY": 3.25,
              "rawZ": 101.75,
              "normalizedX": 0.5,
              "normalizedY": -0.25,
              "rawCoordinateSpace": "replay-world",
              "normalizedCoordinateSpace": "arena-normalized"
            }
          ],
          "positionsTruncated": true,
          "totalPositionCount": 9001,
          "eventCount": 12,
          "rawRecordCount": 345,
          "warnings": [ "synthetic warning: positions truncated" ]
        }
        """;

    [TestMethod]
    public void SessionPageResponse_DeserializesAllFields()
    {
        SessionPageResponse? page = JsonSerializer.Deserialize<SessionPageResponse>(SessionPageJson, JsonSerializerOptions.Web);

        Assert.IsNotNull(page);
        Assert.AreEqual(40, page.Offset);
        Assert.AreEqual(20, page.Limit);
        Assert.AreEqual(137, page.Count);
        Assert.AreEqual(1, page.Items.Count);

        SessionSummaryResponse summary = page.Items[0];
        Assert.AreEqual(14, summary.ParticipantCount);
        Assert.AreEqual(1024, summary.PositionCount);
        Assert.AreEqual(57, summary.EventCount);
        Assert.AreEqual(4096, summary.RawRecordCount);

        DecodeRunResponse decodeRun = summary.DecodeRun;
        Assert.AreEqual("0f8fad5b-d9cb-469f-a165-70867728950e", decodeRun.DecodeRunId);
        Assert.AreEqual("9c9b5f84-7a1e-4c8e-9f3d-2d1f0a8c6b5e", decodeRun.SourceArtifactId);
        Assert.AreEqual("wotb-replay", decodeRun.DecoderId);
        Assert.AreEqual("1.2.3", decodeRun.DecoderVersion);
        Assert.AreEqual("1.0", decodeRun.SchemaVersion);
        Assert.AreEqual("failed", decodeRun.Status);
        Assert.AreEqual(2, decodeRun.Capabilities.Count);
        Assert.AreEqual("battle-session", decodeRun.Capabilities[0]);
        Assert.AreEqual("positions", decodeRun.Capabilities[1]);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero), decodeRun.StartedAtUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 26, 10, 0, 5, 250, TimeSpan.Zero), decodeRun.CompletedAtUtc);
        Assert.AreEqual("source-truncated", decodeRun.FailureCode);
        Assert.AreEqual("synthetic failure summary", decodeRun.FailureSummary);

        BattleSessionResponse session = summary.Session;
        Assert.AreEqual("3fa85f64-5717-4562-b3fc-2c963f66afa6", session.BattleSessionId);
        Assert.AreEqual("11.4.0.1234", session.GameVersion);
        Assert.AreEqual("arena-4711", session.ArenaIdentity);
        Assert.AreEqual("maps/05_synthetic_ridge", session.MapId);
        Assert.AreEqual("Synthetic Ridge", session.MapName);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 26, 9, 55, 12, 500, TimeSpan.Zero), session.BattleTimeUtc);
        Assert.AreEqual(new TimeSpan(2_184_642_200), session.Duration);
        Assert.AreEqual("participant-01", session.ViewpointParticipantId);
    }

    [TestMethod]
    public void SessionDetailResponse_DeserializesAllFields()
    {
        SessionDetailResponse? detail = JsonSerializer.Deserialize<SessionDetailResponse>(SessionDetailJson, JsonSerializerOptions.Web);

        Assert.IsNotNull(detail);

        DecodeRunResponse decodeRun = detail.DecodeRun;
        Assert.AreEqual("6b1c9e52-3d4a-4f7b-9c0d-1e2f3a4b5c6d", decodeRun.DecodeRunId);
        Assert.AreEqual("aa10bb20-cc30-dd40-ee50-ff60aa70bb80", decodeRun.SourceArtifactId);
        Assert.AreEqual("wotb-replay", decodeRun.DecoderId);
        Assert.AreEqual("1.2.3", decodeRun.DecoderVersion);
        Assert.AreEqual("1.0", decodeRun.SchemaVersion);
        Assert.AreEqual("succeeded", decodeRun.Status);
        Assert.AreEqual(1, decodeRun.Capabilities.Count);
        Assert.AreEqual("battle-session", decodeRun.Capabilities[0]);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 25, 18, 30, 0, TimeSpan.Zero), decodeRun.StartedAtUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 25, 18, 30, 2, TimeSpan.Zero), decodeRun.CompletedAtUtc);
        Assert.IsNull(decodeRun.FailureCode);
        Assert.IsNull(decodeRun.FailureSummary);

        BattleSessionResponse session = detail.Session;
        Assert.AreEqual("5d2b7c9a-1e3f-4a5b-8c6d-7e8f9a0b1c2d", session.BattleSessionId);
        Assert.IsNull(session.GameVersion);
        Assert.AreEqual("arena-9001", session.ArenaIdentity);
        Assert.AreEqual("maps/11_synthetic_valley", session.MapId);
        Assert.AreEqual("Synthetic Valley", session.MapName);
        Assert.AreEqual(new DateTimeOffset(2026, 7, 25, 18, 0, 0, TimeSpan.Zero), session.BattleTimeUtc);
        Assert.AreEqual(new TimeSpan(4_200_000_000), session.Duration);
        Assert.IsNull(session.ViewpointParticipantId);

        Assert.AreEqual(1, detail.Participants.Count);
        ParticipantResponse participant = detail.Participants[0];
        Assert.AreEqual("participant-01", participant.ParticipantId);
        Assert.AreEqual(4242L, participant.EntityId);
        Assert.AreEqual(2, participant.TeamNumber);
        Assert.AreEqual("synthetic-player-01", participant.PlayerName);
        Assert.AreEqual("SYN", participant.ClanTag);
        Assert.AreEqual("synthetic-tank-01", participant.TankId);
        Assert.AreEqual("Synthetic Medium", participant.TankName);
        Assert.AreEqual("mediumTank", participant.TankClass);
        Assert.AreEqual("unknown", participant.BotStatus);
        Assert.IsNull(participant.BotStatusConfidence);

        Assert.AreEqual(1, detail.Positions.Count);
        PositionSampleResponse position = detail.Positions[0];
        Assert.AreEqual("participant-01", position.ParticipantId);
        Assert.AreEqual(4242L, position.EntityId);
        Assert.AreEqual(17L, position.Sequence);
        Assert.AreEqual(new TimeSpan(625_000_000), position.ReplayTime);
        Assert.AreEqual(-12.5, position.RawX);
        Assert.AreEqual(3.25, position.RawY);
        Assert.AreEqual(101.75, position.RawZ);
        Assert.AreEqual(0.5, position.NormalizedX);
        Assert.AreEqual(-0.25, position.NormalizedY);
        Assert.AreEqual("replay-world", position.RawCoordinateSpace);
        Assert.AreEqual("arena-normalized", position.NormalizedCoordinateSpace);

        Assert.IsTrue(detail.PositionsTruncated);
        Assert.AreEqual(9001, detail.TotalPositionCount);
        Assert.AreEqual(12, detail.EventCount);
        Assert.AreEqual(345, detail.RawRecordCount);
        Assert.AreEqual(1, detail.Warnings.Count);
        Assert.AreEqual("synthetic warning: positions truncated", detail.Warnings[0]);
    }
}
