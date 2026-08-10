namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// One beacon (persistent world-space POI) rendered by the W2S HUD: the
/// anchor point is the beacon's projected viewport pixel (top-left origin),
/// drawn as a colored pin with its label above. Only beacons in front of the
/// camera with a projection inside the viewport produce items.
/// </summary>
public sealed record BeaconItem(
    string Name,
    double ScreenX,
    double ScreenY,
    string Color,
    double DistanceMeters);
