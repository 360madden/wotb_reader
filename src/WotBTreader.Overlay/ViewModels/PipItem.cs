namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One event-feed pip rendered over a tank's nameplate: a damage hit (with
/// the amount) or a destruction, floating briefly. The anchor is the
/// affected tank's projected viewport pixel.
/// </summary>
public sealed record PipItem(
    long EntityId,
    string Kind,
    int Damage,
    double ScreenX,
    double ScreenY);
