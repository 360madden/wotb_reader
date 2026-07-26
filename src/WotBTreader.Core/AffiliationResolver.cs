namespace WotBTreader.Core;

public static class AffiliationResolver
{
    public static Affiliation RelativeTo(Participant participant, Participant? viewpoint)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (viewpoint is null ||
            participant.TeamNumber is null ||
            viewpoint.TeamNumber is null)
        {
            return Affiliation.Unknown;
        }

        return participant.TeamNumber == viewpoint.TeamNumber
            ? Affiliation.Friendly
            : Affiliation.Enemy;
    }
}
