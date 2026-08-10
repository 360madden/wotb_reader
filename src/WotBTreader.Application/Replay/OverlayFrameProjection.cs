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
/// A renderable instant of the replay overlay: the viewpoint camera and
/// every roster tank projected to viewport pixels. Consumed by the CLI
/// (<c>overlay-frame</c>) and the loopback web host's frame endpoint; the
/// WPF HUD renders these directly over the game window.
/// </summary>
public sealed record OverlayFrameProjection(
    TimeSpan ReplayTime,
    double? CameraX,
    double? CameraY,
    double? CameraZ,
    double? CameraYawRadians,
    double? CameraPitchRadians,
    IReadOnlyList<ProjectedTank> Tanks);

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
        double viewportHeight)
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

        return new OverlayFrameProjection(
            frame.ReplayTime,
            frame.Camera.X,
            frame.Camera.Y,
            frame.Camera.Z,
            frame.Camera.YawRadians,
            frame.Camera.PitchRadians,
            tanks);
    }
}
