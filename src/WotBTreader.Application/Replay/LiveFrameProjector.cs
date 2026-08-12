using WotBTreader.Application.Game;
using WotBTreader.Core;
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
///   miss, never a guessed position. Names/team come from the optional
///   per-id decoded-roster join (design
///   <c>docs/operations/live-roster-name-join-design.md</c>): an id present
///   in the map gets its participant's name/tank/clan/team, everything else
///   stays null — never guessed. HP is honest: when the L1 entity-base read
///   delivered current/max health they map to <c>HpFraction</c>/
///   <c>CurrentHealth</c>/<c>MaxHealth</c> (the alive byte rides along);
///   otherwise the DTO's "unknown" representation — <c>HpFraction 0</c>
///   with <c>MaxHealth/CurrentHealth 0</c> — renders as an empty/unknown
///   HP bar. Never fabricated.
/// - Damage (G2 consumption): only the OWN row (the request's
///   <c>OwnEntityId</c>) can carry <c>DamageDealt</c> — the coordinator read
///   the own Avatar's battle-stats dword0 (honest, fail-closed). All other
///   rows keep 0 (unknown). DamageTaken/Kills stay 0 in live mode.
/// - No pips/kills/beacons: those are decode-projection features and live
///   mode honestly has none.
/// </summary>
public static class LiveFrameProjector
{
    public static OverlayFrameProjection Project(
        LiveFrameReadResult frame,
        double verticalFovRadians,
        double viewportWidth,
        double viewportHeight,
        IReadOnlyDictionary<long, Participant>? participants = null)
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
                viewportHeight,
                participants))
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
            && double.IsFinite(pose.Z))
        {
            // CAM-010 (2026-08-11): GameCamera posA (+0x38) is stored
            // (x, z, y) — world Y and Z are swapped in memory. The W2S seam
            // MUST yz-swap world→camera space: eye = (X, Z, Y). Without the
            // swap the eye lands `sqrt(dx^2 + 2*(tank.z - tank.y)^2)` from
            // the tank (the old "23.57 m third-person offset" artifact).
            double eyeX = pose.X;
            double eyeY = pose.Z;
            double eyeZ = pose.Y;

            // CAM-012 (2026-08-11): the basis rows are the camera's world
            // axes — forward = -row1, up = row2 (verified: look-at collapses
            // to 0.4-6.7 deg, avg 1.7 deg, at the turret-level aim point).
            // Convert the world forward into the packet yaw/pitch convention
            // WorldToScreen.Project expects (yaw 0 -> +Z, +pi/2 -> +X).
            // The memory yaw/pitch fields (+0x50/+0x54/+0x58) are NOT in that
            // convention (DAVA left-handed) — no yaw/pitch sign combo from
            // them reproduces the aim direction (CAM-012 sweep), so the
            // authoritative orientation source is the basis.
            if (pose.Basis is { Length: >= 9 } basis
                && double.IsFinite(basis[3]) && double.IsFinite(basis[4])
                && double.IsFinite(basis[5]))
            {
                // forward = -row1 = (-basis[3], -basis[4], -basis[5]).
                double fx = -basis[3];
                double fy = -basis[4];
                double fz = -basis[5];
                double forwardLength = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                if (forwardLength > 1e-6)
                {
                    fx /= forwardLength;
                    fy /= forwardLength;
                    fz /= forwardLength;
                }

                // Packet convention: fy = sin(pitch), fx/fz = tan(yaw) with
                // yaw 0 -> +Z, +pi/2 -> +X.
                double pitch = Math.Asin(Math.Clamp(fy, -1.0, 1.0));
                double yaw = Math.Atan2(fx, fz);
                if (double.IsFinite(yaw) && double.IsFinite(pitch))
                {
                    return new OverlayCamera(
                        eyeX, eyeY, eyeZ,
                        yaw,
                        pitch,
                        RollRadians: null);
                }
            }

            // Legacy fallback (no basis persisted): the raw pose yaw/pitch
            // fields. Documented best-effort — the DAVA-vs-packet mismatch
            // means the orientation may be off; the basis path is preferred.
            if (double.IsFinite(pose.YawRadians) && double.IsFinite(pose.PitchRadians))
            {
                return new OverlayCamera(
                    eyeX, eyeY, eyeZ,
                    pose.YawRadians,
                    pose.PitchRadians,
                    RollRadians: null);
            }
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
        double viewportHeight,
        IReadOnlyDictionary<long, Participant>? participants)
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

        // Per-id decoded-roster join (fail-closed): an id in the map gets
        // its participant's identity; anything else stays null (the live
        // frame is the source of truth for what exists — never guessed).
        string? playerName = null;
        string? tankName = null;
        string? clanTag = null;
        int? teamNumber = null;
        if (participants is not null
            && participants.TryGetValue(tank.EntityId, out Participant? participant))
        {
            playerName = participant.PlayerName;
            tankName = participant.TankName;
            clanTag = participant.ClanTag;
            teamNumber = participant.TeamNumber;
        }

        // L1 health mapping (honest): real values only when the entity-base
        // read delivered both; otherwise the DTO's unknown representation
        // (fraction 0, healths 0) so the HUD renders an empty bar. The alive
        // byte rides along only when health evidence is present.
        double hpFraction = 0;
        bool alive = true;
        long maxHealth = 0;
        long currentHealth = 0;
        if (tank.HpCurrent is float hpCurrent
            && tank.HpMax is float hpMax
            && hpMax > 0)
        {
            currentHealth = (long)hpCurrent;
            maxHealth = (long)hpMax;
            hpFraction = Math.Clamp(hpCurrent / hpMax, 0.0, 1.0);
            alive = tank.Alive ?? true;
        }

        return new ProjectedTank(
            tank.EntityId,
            PlayerName: playerName,
            TankName: tankName,
            ClanTag: clanTag,
            TeamNumber: teamNumber,
            HpFraction: hpFraction,
            Alive: alive,
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
            DamageDealt: tank.DamageDealt ?? 0,
            DamageTaken: 0,
            Kills: 0,
            MaxHealth: maxHealth,
            CurrentHealth: currentHealth);
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
