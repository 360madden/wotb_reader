namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One kill-feed entry for the HUD: the destroyed tank's name (resolved from
/// the frame's roster) and the killer's name when attribution succeeded.
/// Killer text falls back to a neutral label for environmental kills.
/// </summary>
public sealed record KillItem(
    long VictimEntityId,
    long? KillerEntityId,
    string VictimLabel,
    string KillerLabel,
    double ReplayTimeSeconds);
