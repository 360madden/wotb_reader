namespace WotBTreader.Overlay.ViewModels;

/// <summary>One battle session row in the session list.</summary>
public sealed record SessionRow(
    Guid BattleSessionId,
    string MapLabel,
    DateTimeOffset BattleTimeUtc,
    int ParticipantCount,
    int PositionCount);
