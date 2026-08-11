namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One scoreboard row for the HUD: a tank's cumulative battle statistics at
/// the current frame time. Name is resolved from the frame's roster; damage
/// dealt and kills come from the decoded damage/destroy events attributed to
/// the tank as attacker.
/// </summary>
public sealed record ScoreboardItem(
    long EntityId,
    string PlayerName,
    int? TeamNumber,
    long DamageDealt,
    long DamageTaken,
    long Kills,
    double HpFraction,
    bool Alive);
