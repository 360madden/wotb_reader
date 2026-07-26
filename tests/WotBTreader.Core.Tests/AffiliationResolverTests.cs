namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class AffiliationResolverTests
{
    [TestMethod]
    public void RelativeTo_SameTeam_IsFriendly()
    {
        Participant viewpoint = CreateParticipant(teamNumber: 1);
        Participant participant = CreateParticipant(teamNumber: 1);

        Affiliation result = AffiliationResolver.RelativeTo(participant, viewpoint);

        Assert.AreEqual(Affiliation.Friendly, result);
    }

    [TestMethod]
    public void RelativeTo_DifferentTeam_IsEnemy()
    {
        Participant viewpoint = CreateParticipant(teamNumber: 1);
        Participant participant = CreateParticipant(teamNumber: 2);

        Affiliation result = AffiliationResolver.RelativeTo(participant, viewpoint);

        Assert.AreEqual(Affiliation.Enemy, result);
    }

    [TestMethod]
    public void RelativeTo_MissingTeamEvidence_IsUnknown()
    {
        Participant viewpoint = CreateParticipant(teamNumber: 1);
        Participant participant = CreateParticipant(teamNumber: null);

        Affiliation result = AffiliationResolver.RelativeTo(participant, viewpoint);

        Assert.AreEqual(Affiliation.Unknown, result);
    }

    [TestMethod]
    public void Participant_NameAlone_DoesNotChangeUnknownBotStatus()
    {
        Participant participant = CreateParticipant(teamNumber: 1) with
        {
            PlayerName = ":bot-looking-name:",
            BotStatus = BotStatus.Unknown,
        };

        Assert.AreEqual(BotStatus.Unknown, participant.BotStatus);
    }

    private static Participant CreateParticipant(int? teamNumber)
    {
        SourceArtifactId artifactId = SourceArtifactId.New();
        return new Participant(
            ParticipantId.New(),
            BattleSessionId.New(),
            AccountId: null,
            EntityId: null,
            TeamNumber: teamNumber,
            PlayerName: null,
            ClanTag: null,
            VehicleCompactDescriptor: null,
            TankId: null,
            TankName: null,
            TankClass.Unknown,
            BotStatus.Unknown,
            EvidenceConfidence.Unknown,
            new EvidenceReference(
                artifactId,
                ArchiveEntry: "meta.json",
                Offset: 0,
                Length: 0,
                new ContentHash(new string('0', ContentHash.Sha256HexLength))));
    }
}
