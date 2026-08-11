namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One tank on the replay minimap: normalized 0..1 panel coordinates (u =
/// world X across the map boundary, v = world Z down it) plus the team and
/// alive state for dot styling. Unlike nameplates, minimap entries are
/// camera-independent god-view — every roster tank with a position sample
/// appears regardless of what the camera can see.
/// </summary>
public sealed record MinimapItem(
    long EntityId,
    double NormalizedX,
    double NormalizedZ,
    int? TeamNumber,
    bool Alive);
