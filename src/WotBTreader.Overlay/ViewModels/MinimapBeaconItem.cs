namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One beacon on the replay minimap: normalized 0..1 panel coordinates plus
/// its marker color, so POIs appear on the god-view panel regardless of what
/// the camera can see (beacons are world-anchored offline data).
/// </summary>
public sealed record MinimapBeaconItem(
    string Name,
    string Color,
    double NormalizedX,
    double NormalizedZ);
