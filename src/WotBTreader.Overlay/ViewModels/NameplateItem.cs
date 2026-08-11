namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One nameplate rendered by the W2S HUD: the anchor point is the tank's
/// projected viewport pixel (top-left origin), and the label/HP bar draw
/// above it. Only tanks in front of the camera with a projection inside the
/// viewport produce items; the player's own tank (distance ~0) is excluded.
/// </summary>
public sealed record NameplateItem(
    long EntityId,
    double ScreenX,
    double ScreenY,
    string Label,
    int? TeamNumber,
    double HpFraction,
    bool Alive,
    double DistanceMeters,
    double? ScreenHeadingDegrees,
    // Cumulative battle statistics at the frame time, shown as a compact
    // totals line under the HP bar (damage dealt + kills).
    long DamageDealt = 0,
    long Kills = 0);
