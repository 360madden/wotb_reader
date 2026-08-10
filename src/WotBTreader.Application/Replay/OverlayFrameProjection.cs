using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Replay;

/// <summary>
/// One tank projected onto the viewport: world data plus the screen pixel
/// (null when the tank is at/behind the camera or the camera carries no
/// rotation evidence — never drawn).
/// </summary>
public sealed record ProjectedTank(
    long EntityId,
    string? PlayerName,
    string? TankName,
    string? ClanTag,
    int? TeamNumber,
    double HpFraction,
    bool Alive,
    double DistanceMeters,
    double? ScreenX,
    double? ScreenY,
    double? Depth,
    bool InViewport);

/// <summary>
/// One beacon projected onto the viewport. Screen coordinates are null when
/// the beacon is at/behind the camera or the camera carries no rotation
/// evidence — never drawn.
/// </summary>
public sealed record ProjectedBeacon(
    string Name,
    string Color,
    double DistanceMeters,
    double? ScreenX,
    double? ScreenY,
    double? Depth,
    bool InViewport);

/// <summary>
/// A renderable instant of the replay overlay: the viewpoint camera, every
/// roster tank, and every visible beacon projected to viewport pixels.
/// Consumed by the CLI (<c>overlay-frame</c>) and the loopback web host's
/// frame endpoint; the WPF HUD renders these directly over the game window.
/// </summary>
public sealed record OverlayFrameProjection(
    TimeSpan ReplayTime,
    double? CameraX,
    double? CameraY,
    double? CameraZ,
    double? CameraYawRadians,
    double? CameraPitchRadians,
    IReadOnlyList<ProjectedTank> Tanks,
    IReadOnlyList<ProjectedBeacon> Beacons);

/// <summary>
/// Projects an <see cref="OverlayFrame"/> to viewport pixels via
/// <see cref="WorldToScreen"/>. The single projection path shared by every
/// consumer, so the CLI preview and the live web HUD can never disagree.
/// Tanks are sorted by distance (nearest first).
/// </summary>
public static class OverlayFrameProjector
{
    public static OverlayFrameProjection Project(
        OverlayFrame frame,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight,
        IReadOnlyList<OverlayBeacon>? beacons = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var tanks = frame.Tanks
            .Select(tank =>
            {
                ScreenPoint? point = WorldToScreen.Project(
                    frame.Camera,
                    verticalFovRadians,
                    viewportWidth,
                    viewportHeight,
                    tank.X,
                    tank.Y,
                    tank.Z);
                return new ProjectedTank(
                    tank.EntityId,
                    tank.PlayerName,
                    tank.TankName,
                    tank.ClanTag,
                    tank.TeamNumber,
                    tank.HpFraction,
                    tank.Alive,
                    tank.DistanceMeters,
                    point is null ? null : point.Value.X,
                    point is null ? null : point.Value.Y,
                    point is null ? null : point.Value.Depth,
                    point is not null && point.Value.IsInsideViewport(viewportWidth, viewportHeight));
            })
            .OrderBy(tank => tank.DistanceMeters)
            .ToList();

        var visibleBeacons = (beacons ?? [])
            .Where(beacon =>
                (beacon.VisibleFrom is null || beacon.VisibleFrom <= frame.ReplayTime) &&
                (beacon.VisibleUntil is null || beacon.VisibleUntil >= frame.ReplayTime))
            .Select(beacon =>
            {
                ScreenPoint? point = WorldToScreen.Project(
                    frame.Camera,
                    verticalFovRadians,
                    viewportWidth,
                    viewportHeight,
                    beacon.X,
                    beacon.Y,
                    beacon.Z);
                return new ProjectedBeacon(
                    beacon.Name,
                    beacon.Color,
                    DistanceMeters(beacon, frame.Camera),
                    point is null ? null : point.Value.X,
                    point is null ? null : point.Value.Y,
                    point is null ? null : point.Value.Depth,
                    point is not null && point.Value.IsInsideViewport(viewportWidth, viewportHeight));
            })
            .OrderBy(beacon => beacon.DistanceMeters)
            .ToList();

        return new OverlayFrameProjection(
            frame.ReplayTime,
            frame.Camera.X,
            frame.Camera.Y,
            frame.Camera.Z,
            frame.Camera.YawRadians,
            frame.Camera.PitchRadians,
            tanks,
            visibleBeacons);
    }

    private static double DistanceMeters(OverlayBeacon beacon, OverlayCamera camera)
    {
        double dx = beacon.X - camera.X;
        double dy = beacon.Y - camera.Y;
        double dz = beacon.Z - camera.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
