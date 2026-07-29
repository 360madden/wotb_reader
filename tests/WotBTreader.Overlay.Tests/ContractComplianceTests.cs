using System.Text.Json;
using WotBTreader.Overlay.Contracts;

namespace WotBTreader.Overlay.Tests;

/// <summary>
/// Proves the overlay DTOs and the published wire contracts agree on the JSON
/// format by deserializing the same fixture with both type sets and asserting
/// key fields match. This is a stopgap: once the overlay consumes
/// <c>WotBTreader.ApiContracts</c> directly, agreement is a compile-time
/// property and these fixtures no longer prove anything.
/// </summary>
[TestClass]
public sealed class ContractComplianceTests
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private const string SessionPageJson = """
        {
          "offset": 5,
          "limit": 10,
          "count": 42,
          "items": [
            {
              "decodeRun": {
                "decodeRunId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
                "sourceArtifactId": "f0e1d2c3-b4a5-6789-0cde-f123456789ab",
                "decoderId": "wotb-replay",
                "decoderVersion": "1.2.3",
                "schemaVersion": "1.0",
                "status": "succeeded",
                "capabilities": [ "Metadata", "Positions" ],
                "startedAtUtc": "2026-07-26T10:00:00.0000000+00:00",
                "completedAtUtc": "2026-07-26T10:00:02.5000000+00:00",
                "failureCode": null,
                "failureSummary": null
              },
              "session": {
                "battleSessionId": "11111111-2222-3333-4444-555555555555",
                "gameVersion": "11.18.0.7",
                "arenaIdentity": null,
                "mapId": "maps/test_map",
                "mapName": "Test Map",
                "battleTimeUtc": "2026-07-26T09:55:00.0000000+00:00",
                "duration": "0:05:30.0000000",
                "viewpointParticipantId": null
              },
              "participantCount": 14,
              "positionCount": 1024,
              "eventCount": 57,
              "rawRecordCount": 2048
            }
          ]
        }
        """;

    private const string SessionDetailJson = """
        {
          "decodeRun": {
            "decodeRunId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
            "sourceArtifactId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
            "decoderId": "wotb-replay",
            "decoderVersion": "1.2.3",
            "schemaVersion": "1.0",
            "status": "succeeded",
            "capabilities": [ "Positions" ],
            "startedAtUtc": "2026-07-26T11:00:00.0000000+00:00",
            "completedAtUtc": "2026-07-26T11:00:03.0000000+00:00",
            "failureCode": null,
            "failureSummary": null
          },
          "session": {
            "battleSessionId": "22222222-3333-4444-5555-666666666666",
            "gameVersion": "11.18.0.7",
            "arenaIdentity": "arena-42",
            "mapId": null,
            "mapName": null,
            "battleTimeUtc": "2026-07-26T10:00:00.0000000+00:00",
            "duration": "0:05:00.0000000",
            "viewpointParticipantId": null
          },
          "participants": [
            {
              "participantId": "33333333-4444-5555-6666-777777777777",
              "entityId": 100,
              "teamNumber": 1,
              "playerName": "Alpha",
              "clanTag": "CLN",
              "vehicleCompactDescriptor": null,
              "tankId": "tank-01",
              "tankName": "Test Tank",
              "tankClass": "mediumTank",
              "botStatus": "Human",
              "botStatusConfidence": "High"
            }
          ],
          "positions": [
            {
              "participantId": "33333333-4444-5555-6666-777777777777",
              "entityId": 100,
              "sequence": 42,
              "replayTime": "0:03:15.5000000",
              "rawX": -50.0,
              "rawY": 0.0,
              "rawZ": 75.5,
              "normalizedX": 0.25,
              "normalizedY": -0.75,
              "rawCoordinateSpace": "ReplayRaw",
              "normalizedCoordinateSpace": "ArenaNormalized"
            }
          ],
          "positionsTruncated": false,
          "totalPositionCount": 1,
          "eventCount": 3,
          "rawRecordCount": 64,
          "warnings": [ "synthetic" ]
        }
        """;

    // ── Session page compliance ──────────────────────────────────────

    [TestMethod]
    public void HostAndOverlay_SessionPage_AgreeOnAllFields()
    {
        ApiContracts.SessionPageResponse hostPage =
            JsonSerializer.Deserialize<ApiContracts.SessionPageResponse>(
                SessionPageJson, JsonOptions)!;
        SessionPageResponse overlayPage =
            JsonSerializer.Deserialize<SessionPageResponse>(SessionPageJson, JsonOptions)!;

        Assert.IsNotNull(hostPage);
        Assert.IsNotNull(overlayPage);
        Assert.AreEqual(hostPage.Offset, overlayPage.Offset);
        Assert.AreEqual(hostPage.Limit, overlayPage.Limit);
        Assert.AreEqual(hostPage.Count, overlayPage.Count);
        Assert.AreEqual(hostPage.Items.Count, overlayPage.Items.Count);

        var hostItem = hostPage.Items[0];
        var overlayItem = overlayPage.Items[0];
        Assert.AreEqual(hostItem.ParticipantCount, overlayItem.ParticipantCount);
        Assert.AreEqual(hostItem.PositionCount, overlayItem.PositionCount);
        Assert.AreEqual(hostItem.EventCount, overlayItem.EventCount);
        Assert.AreEqual(hostItem.RawRecordCount, overlayItem.RawRecordCount);
    }

    [TestMethod]
    public void HostAndOverlay_DecodeRun_AgreeOnAllFields()
    {
        ApiContracts.DecodeRunResponse hostRun =
            JsonSerializer.Deserialize<ApiContracts.SessionPageResponse>(
                SessionPageJson, JsonOptions)!.Items[0].DecodeRun;
        DecodeRunResponse overlayRun =
            JsonSerializer.Deserialize<SessionPageResponse>(
                SessionPageJson, JsonOptions)!.Items[0].DecodeRun;

        Assert.AreEqual(hostRun.DecodeRunId, overlayRun.DecodeRunId);
        Assert.AreEqual(hostRun.SourceArtifactId, overlayRun.SourceArtifactId);
        Assert.AreEqual(hostRun.DecoderId, overlayRun.DecoderId);
        Assert.AreEqual(hostRun.DecoderVersion, overlayRun.DecoderVersion);
        Assert.AreEqual(hostRun.SchemaVersion, overlayRun.SchemaVersion);
        Assert.AreEqual(hostRun.Status, overlayRun.Status);
        CollectionAssert.AreEqual(
            (System.Collections.ICollection)hostRun.Capabilities,
            (System.Collections.ICollection)overlayRun.Capabilities);
        Assert.AreEqual(hostRun.StartedAtUtc, overlayRun.StartedAtUtc);
        Assert.AreEqual(hostRun.CompletedAtUtc, overlayRun.CompletedAtUtc);
        Assert.AreEqual(hostRun.FailureCode, overlayRun.FailureCode);
        Assert.AreEqual(hostRun.FailureSummary, overlayRun.FailureSummary);
    }

    [TestMethod]
    public void HostAndOverlay_BattleSession_AgreeOnAllFields()
    {
        ApiContracts.BattleSessionResponse? hostSession =
            JsonSerializer.Deserialize<ApiContracts.SessionPageResponse>(
                SessionPageJson, JsonOptions)!.Items[0].Session;
        BattleSessionResponse? overlaySession =
            JsonSerializer.Deserialize<SessionPageResponse>(
                SessionPageJson, JsonOptions)!.Items[0].Session;

        Assert.IsNotNull(hostSession);
        Assert.IsNotNull(overlaySession);
        Assert.AreEqual(hostSession.BattleSessionId, overlaySession.BattleSessionId);
        Assert.AreEqual(hostSession.GameVersion, overlaySession.GameVersion);
        Assert.AreEqual(hostSession.ArenaIdentity, overlaySession.ArenaIdentity);
        Assert.AreEqual(hostSession.MapId, overlaySession.MapId);
        Assert.AreEqual(hostSession.MapName, overlaySession.MapName);
        Assert.AreEqual(hostSession.BattleTimeUtc, overlaySession.BattleTimeUtc);
        Assert.AreEqual(hostSession.Duration, overlaySession.Duration);
        Assert.AreEqual(hostSession.ViewpointParticipantId, overlaySession.ViewpointParticipantId);
    }

    // ── Session detail compliance ────────────────────────────────────

    [TestMethod]
    public void HostAndOverlay_Participant_AgreeOnAllFields()
    {
        ApiContracts.ParticipantResponse hostP =
            JsonSerializer.Deserialize<ApiContracts.SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!.Participants[0];
        ParticipantResponse overlayP =
            JsonSerializer.Deserialize<SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!.Participants[0];

        Assert.AreEqual(hostP.ParticipantId, overlayP.ParticipantId);
        Assert.AreEqual(hostP.EntityId, overlayP.EntityId);
        Assert.AreEqual(hostP.TeamNumber, overlayP.TeamNumber);
        Assert.AreEqual(hostP.PlayerName, overlayP.PlayerName);
        Assert.AreEqual(hostP.ClanTag, overlayP.ClanTag);
        Assert.AreEqual(hostP.TankId, overlayP.TankId);
        Assert.AreEqual(hostP.TankName, overlayP.TankName);
        Assert.AreEqual(hostP.TankClass, overlayP.TankClass);
        Assert.AreEqual(hostP.BotStatus, overlayP.BotStatus);
        Assert.AreEqual(hostP.BotStatusConfidence, overlayP.BotStatusConfidence);
    }

    [TestMethod]
    public void HostAndOverlay_PositionSample_AgreeOnAllFields()
    {
        ApiContracts.PositionSampleResponse hostPos =
            JsonSerializer.Deserialize<ApiContracts.SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!.Positions[0];
        PositionSampleResponse overlayPos =
            JsonSerializer.Deserialize<SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!.Positions[0];

        Assert.AreEqual(hostPos.ParticipantId, overlayPos.ParticipantId);
        Assert.AreEqual(hostPos.EntityId, overlayPos.EntityId);
        Assert.AreEqual(hostPos.Sequence, overlayPos.Sequence);
        Assert.AreEqual(hostPos.ReplayTime, overlayPos.ReplayTime);
        Assert.AreEqual(hostPos.RawX, overlayPos.RawX);
        Assert.AreEqual(hostPos.RawY, overlayPos.RawY);
        Assert.AreEqual(hostPos.RawZ, overlayPos.RawZ);
        Assert.AreEqual(hostPos.NormalizedX, overlayPos.NormalizedX);
        Assert.AreEqual(hostPos.NormalizedY, overlayPos.NormalizedY);
        Assert.AreEqual(hostPos.RawCoordinateSpace, overlayPos.RawCoordinateSpace);
        Assert.AreEqual(hostPos.NormalizedCoordinateSpace, overlayPos.NormalizedCoordinateSpace);
    }

    [TestMethod]
    public void HostAndOverlay_SessionDetail_AgreeOnEnvelopeFields()
    {
        ApiContracts.SessionDetailResponse hostDetail =
            JsonSerializer.Deserialize<ApiContracts.SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!;
        SessionDetailResponse overlayDetail =
            JsonSerializer.Deserialize<SessionDetailResponse>(
                SessionDetailJson, JsonOptions)!;

        Assert.AreEqual(hostDetail.PositionsTruncated, overlayDetail.PositionsTruncated);
        Assert.AreEqual(hostDetail.TotalPositionCount, overlayDetail.TotalPositionCount);
        Assert.AreEqual(hostDetail.EventCount, overlayDetail.EventCount);
        Assert.AreEqual(hostDetail.RawRecordCount, overlayDetail.RawRecordCount);
        Assert.AreEqual(hostDetail.Participants.Count, overlayDetail.Participants.Count);
        Assert.AreEqual(hostDetail.Positions.Count, overlayDetail.Positions.Count);
        Assert.AreEqual(hostDetail.Warnings.Count, overlayDetail.Warnings.Count);
    }
}
