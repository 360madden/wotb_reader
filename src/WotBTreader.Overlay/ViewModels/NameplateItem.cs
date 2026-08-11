namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One nameplate rendered by the W2S HUD: the anchor point is the tank's
/// projected viewport pixel (top-left origin), and the label/HP bar draw
/// above it. Only tanks in front of the camera with a projection inside the
/// viewport produce items; the player's own tank (distance ~0) is excluded.
/// Items are collected far-to-near (depth descending) so that, when two
/// nameplates overlap, the nearer tank's plate draws on top (WPF paints
/// later canvas children over earlier ones).
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
    double Depth,
    double? ScreenHeadingDegrees,
    // Cumulative battle statistics at the frame time, shown as a compact
    // totals line under the HP bar (damage dealt + kills).
    long DamageDealt = 0,
    long Kills = 0,
    // Exact health from the decoded ledger: max from the type-5 spawn
    // broadcast, current = max − damage received (0 when max is unknown) —
    // rendered as a "current / max" readout next to the HP bar.
    long MaxHealth = 0,
    long CurrentHealth = 0);
