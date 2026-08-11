using WotBTreader.Application.Game;
using WotBTreader.Core.Discovery;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Replay;

/// <summary>
/// Projects a composed live frame (<see cref="LiveFrameReadResult"/> from the
/// gated `/discover/live-frame` surface) to viewport pixels — the
/// <c>LiveFrameSource</c> seam that lets the overlay switch from replay
/// frames to live frames without touching its render path. The output is the
/// SAME <see cref="OverlayFrameProjection"/> shape the replay path produces,
/// so every consumer (CLI preview, loopback web HUD) maps it identically and
/// the two sources can never disagree on projection math.
///
/// Honest-by-design mapping (never fabricated):
/// - Camera: the CAM-001 pose only when <see cref="CameraPoseStatus.Resolved"/>
///   AND every component is finite; otherwise the origin fallback — exactly
///   the replay projector's "no viewpoint evidence" shape.
/// - Tanks: only <c>Resolved</c> ring records with finite position are
///   projected; a region that resolved but failed to decode is a per-tank
///   miss, never a guessed position. Names/team are null (live mode has no
///   decoded roster join yet), and HP is the DTO's "unknown" representation —
///   <c>HpFraction 0</c> with <c>MaxHealth/CurrentHealth 0</c> — which renders
///   as an empty/unknown HP bar until the L1 live session lands.
/// - No pips/kills/beacons: those are decode-projection features and live
///   mode honestly has none.
/// </summary>
public static class LiveFrameProjector
{
    public static OverlayFrameProjection Project(
        LiveFrameReadResult frame,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);

        OverlayCamera camera = BuildCamera(frame.Camera);
        var tanks = frame.Tanks
            .Where(IsProjectable)
            .Select(tank => ProjectTank(
                tank,
                camera,
                verticalFovRadians,
                viewportWidth,
                viewportHeight))
            .OrderBy(tank => tank.DistanceMeters)
            .ToList();

        return new OverlayFrameProjection(
            TimeSpan.FromSeconds(frame.ReplayTimeSeconds ?? 0.0),
            camera.X,
            camera.Y,
            camera.Z,
            camera.YawRadians,
            camera.PitchRadians,
            tanks,
            Beacons: [],
            Pips: [],
            Kills: []);
    }

    private static OverlayCamera BuildCamera(CameraPoseReadResult? pose)
    {
        if (pose is { Status: CameraPoseStatus.Resolved }
            && double.IsFinite(pose.X)
            && double.IsFinite(pose.Y)
            && double.IsFinite(pose.Z)
            && double.IsFinite(pose.YawRadians)
            && double.IsFinite(pose.PitchRadians))
        {
            return new OverlayCamera(
                pose.X,
                pose.Y,
                pose.Z,
                pose.YawRadians,
                pose.PitchRadians,
                RollRadians: null);
        }

        // No usable live pose: the origin fallback (same shape as the replay
        // projector's missing-viewpoint camera) — never a fabricated pose.
        return new OverlayCamera(0, 0, 0, null, null, null);
    }

    private static bool IsProjectable(LiveFrameTankState tank)
        => tank.Status == Type10EntityPositionStatus.Resolved
            && tank.X is float x && float.IsFinite(x)
            && tank.Y is float y && float.IsFinite(y)
            && tank.Z is float z && float.IsFinite(z);

    private static ProjectedTank ProjectTank(
        LiveFrameTankState tank,
        OverlayCamera camera,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight)
    {
        double x = tank.X!.Value;
        double y = tank.Y!.Value;
        double z = tank.Z!.Value;
        ScreenPoint? point = WorldToScreen.Project(
            camera,
            verticalFovRadians,
            viewportWidth,
            viewportHeight,
            x,
            y,
            z);
        return new ProjectedTank(
            tank.EntityId,
            PlayerName: null,
            TankName: null,
            ClanTag: null,
            TeamNumber: null,
            HpFraction: 0,
            Alive: true,
            DistanceMeters: DistanceMeters(camera, x, y, z),
            x,
            z,
            point is null ? null : point.Value.X,
            point is null ? null : point.Value.Y,
            point is null ? null : point.Value.Depth,
            point is not null && point.Value.IsInsideViewport(viewportWidth, viewportHeight),
            tank.YawRadians is float yaw && float.IsFinite(yaw)
                ? WorldToScreen.ScreenHeadingDegrees(
                    camera,
                    verticalFovRadians,
                    viewportWidth,
                    viewportHeight,
                    x,
                    y,
                    z,
                    yaw)
                : null,
            DamageDealt: 0,
            DamageTaken: 0,
            Kills: 0,
            MaxHealth: 0,
            CurrentHealth: 0);
    }

    private static double DistanceMeters(
        OverlayCamera camera,
        double x,
        double y,
        double z)
    {
        double dx = x - camera.X;
        double dy = y - camera.Y;
        double dz = z - camera.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
