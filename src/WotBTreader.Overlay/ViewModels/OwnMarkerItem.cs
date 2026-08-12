namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// The "honest self marker" (name-join design step 4): when the player's own
/// tank (the decoded viewpoint id, <c>OwnEntityId</c>) projects OFF the
/// viewport — chase camera cuts, death-cam pans, the battle-intro cinematic —
/// the HUD draws a chevron at the viewport edge pointing back at the hull.
/// The point is the projection clamped to the viewport rect with a margin;
/// <see cref="AngleRadians"/> points from the marker toward the tank's actual
/// projection (0 = +X, +π/2 = +Y, viewport pixels top-left origin). No item
/// is produced when the own tank is on-screen (the player sees it), when the
/// id is unknown (never guessed), or when the projection is missing.
/// </summary>
public sealed record OwnMarkerItem(
    double ScreenX,
    double ScreenY,
    double AngleRadians);
