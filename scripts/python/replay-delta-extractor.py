#!/usr/bin/env python3
"""replay-delta-extractor.py — derive delta-compare targets from a decoded replay.

Reads a decoded battle session from the treader SQLite store and computes the
replay-derived delta values the rolling campaign's CompareMode='delta' needs:

  - Position delta (Float X/Z): the world-space displacement of a moving
    participant over a transition window. This is the primary pilot target —
    it is unit-consistent (meters) and directly comparable to in-memory Float
    position fields.
  - replayTime delta (Double): the replay-time advance per window, in both
    raw ticks (1e6 ticks/sec) and seconds, so an operator can try scale
    hypotheses against the in-memory Double.

Output is JSON on stdout (or --json PATH). It also prints a ready-to-paste
rolling-driver command line for both the Float-position and Double-replayTime
variants.

Modes:
  python scripts/python/replay-delta-extractor.py
      Use the most recent 11.19.0 session that has position samples, the
      most-moving participant, and the default 4s transition window.

  python scripts/python/replay-delta-extractor.py --session <id> --window 5
  python scripts/python/replay-delta-extractor.py --participant <id> --json out.json
"""

from __future__ import annotations

import argparse
import json
import math
import sqlite3
import statistics
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DEFAULT_DB = REPO_ROOT / ".data" / "treader.db"
TICKS_PER_SECOND = 1_000_000


def pick_session(con: sqlite3.Connection, version_hint: str = "11.19") -> dict:
    """Most recent session of the hinted game version with position samples."""
    cur = con.execute(
        "SELECT s.id, s.game_version, s.map_name, s.duration_ticks, "
        "s.viewpoint_participant_id, COUNT(p.id) AS sample_count "
        "FROM battle_sessions s "
        "JOIN position_samples p ON p.battle_session_id = s.id "
        "WHERE s.game_version LIKE ? "
        "GROUP BY s.id ORDER BY s.battle_time_utc DESC LIMIT 1",
        (version_hint + "%",),
    )
    row = cur.fetchone()
    if row is None:
        cur = con.execute(
            "SELECT s.id, s.game_version, s.map_name, s.duration_ticks, "
            "s.viewpoint_participant_id, COUNT(p.id) AS sample_count "
            "FROM battle_sessions s "
            "JOIN position_samples p ON p.battle_session_id = s.id "
            "GROUP BY s.id ORDER BY s.battle_time_utc DESC LIMIT 1"
        )
        row = cur.fetchone()
    if row is None:
        raise SystemExit("no decoded battle session with position samples found")
    return {
        "id": row[0],
        "game_version": row[1],
        "map_name": row[2],
        "duration_ticks": row[3],
        "viewpoint_participant_id": row[4],
        "sample_count": row[5],
    }


def participant_samples(
    con: sqlite3.Connection, session_id: str, participant_id: str
) -> list[tuple[int, float, float, float]]:
    """(replay_time_ticks, raw_x, raw_y, raw_z) ordered by sequence."""
    rows = con.execute(
        "SELECT replay_time_ticks, raw_x, raw_y, raw_z FROM position_samples "
        "WHERE battle_session_id=? AND participant_id=? ORDER BY sequence",
        (session_id, participant_id),
    ).fetchall()
    return [(int(r[0]), float(r[1]), float(r[2]), float(r[3])) for r in rows]


def moving_participants(
    con: sqlite3.Connection, session_id: str, top: int = 3
) -> list[tuple[str, int]]:
    """Participants with the most samples, preferring ones that actually move."""
    rows = con.execute(
        "SELECT participant_id, COUNT(*) c FROM position_samples "
        "WHERE battle_session_id=? AND participant_id IS NOT NULL "
        "GROUP BY participant_id ORDER BY c DESC LIMIT ?",
        (session_id, top),
    ).fetchall()
    scored = []
    for pid, count in rows:
        samples = participant_samples(con, session_id, pid)
        # median 1s-spaced 2D displacement as a movement proxy
        displacements = []
        for i in range(len(samples) - 1):
            dt = samples[i + 1][0] - samples[i][0]
            if 0.9 * TICKS_PER_SECOND <= dt <= 1.1 * TICKS_PER_SECOND:
                dx = samples[i + 1][1] - samples[i][1]
                dz = samples[i + 1][3] - samples[i][3]
                displacements.append(math.hypot(dx, dz))
        median_disp = statistics.median(displacements) if displacements else 0.0
        scored.append((pid, count, median_disp))
    # Sort by movement first (moving participants are the useful pilot target),
    # then sample count.
    scored.sort(key=lambda t: (-t[2], -t[1]))
    return [(pid, count) for pid, count, _ in scored]


def _interp_at(
    samples: list[tuple[int, float, float, float]],
    target_ticks: int,
) -> tuple[float, float, float] | None:
    """Linearly interpolate (x, y, z) at `target_ticks` using bracketing
    samples. Returns None when outside the sampled range or exactly on a
    sample. Positions are ~1s apart, so interpolation error is negligible for
    a multi-second window."""
    lo = 0
    hi = len(samples) - 1
    if target_ticks <= samples[0][0] or target_ticks >= samples[-1][0]:
        return None
    # binary search for the pair bracketing target_ticks
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if samples[mid][0] <= target_ticks:
            lo = mid
        else:
            hi = mid
    t0, x0, y0, z0 = samples[lo]
    t1, x1, y1, z1 = samples[hi]
    if t1 <= t0:
        return None
    f = (target_ticks - t0) / (t1 - t0)
    return (x0 + (x1 - x0) * f, y0 + (y1 - y0) * f, z0 + (z1 - z0) * f)


def windowed_displacements(
    samples: list[tuple[int, float, float, float]],
    window_seconds: float,
    speed: float = 1.0,
) -> list[dict]:
    """Per-window displacement over the replay via sliding-window interp.

    For each sample at time t where t + window is inside the sampled range,
    interpolate the position at t + window and record the 2D (x,z)
    displacement and per-axis deltas. `window` is replay-time seconds at the
    given speed multiplier. This yields one measurement per ~second of replay
    (dense), unlike a straddling-pair scan.
    """
    window_ticks = int(window_seconds * speed * TICKS_PER_SECOND)
    out: list[dict] = []
    for cur in samples:
        t = cur[0]
        target = t + window_ticks
        if target >= samples[-1][0]:
            break
        pos = _interp_at(samples, target)
        if pos is None:
            continue
        dx = pos[0] - cur[1]
        dz = pos[2] - cur[3]
        out.append(
            {
                "dx": dx,
                "dz": dz,
                "dist2d": math.hypot(dx, dz),
                "dt_ticks": window_ticks,
            }
        )
    return out


def summarize(values: list[float]) -> dict:
    if not values:
        return {"n": 0}
    vals = sorted(values)
    def pct(p: float) -> float:
        idx = min(len(vals) - 1, int(p * (len(vals) - 1)))
        return vals[idx]
    return {
        "n": len(vals),
        "median": round(statistics.median(vals), 4),
        "mean": round(statistics.fmean(vals), 4),
        "p90": round(pct(0.90), 4),
        "max": round(vals[-1], 4),
    }


def recommend(delta_median: float, p90: float, delta_max: float) -> tuple[float, float]:
    """Target = median displacement; tolerance = spread of the distribution.

    Tolerance must cover the observed variance but stay small enough to
    discriminate the field from other values changing at unrelated rates.
    A floor of 10% of the target keeps the pilot from being over-tight on
    jittery telemetry (replay position updates are ~1s apart with noise).
    """
    if delta_median <= 0:
        # Static (or nearly static) window: target 0 with a tolerance derived
        # from the observed jitter, floored to something a moving replay will
        # clear in a later window.
        tol = max(delta_max * 2.0, 0.05)
        return 0.0, round(tol, 4)
    spread = max(p90 - delta_median, delta_median * 0.5)
    tol = round(max(spread, delta_median * 0.10), 4)
    return round(delta_median, 4), tol


def build_command(session_id: str, kind: str, target: float, tol: float, window: float) -> str:
    if kind == "Float":
        return (
            f"# Float position delta pilot (session {session_id}, window {window}s):\n"
            f"powershell -File scripts/roll-replay-time-increased.ps1 "
            f"-CompareMode delta -DeltaTarget {target} -DeltaTolerance {tol} "
            f"-ValueKind Float -TransitionSeconds {window} -MaxRounds 22 "
            f"-HoldAfterRollSeconds 240 -SnapshotMaxBytes 402653184"
        )
    return (
        f"# Double replayTime delta pilot (session {session_id}, window {window}s):\n"
        f"powershell -File scripts/roll-replay-time-increased.ps1 "
        f"-CompareMode delta -DeltaTarget {target} -DeltaTolerance {tol} "
        f"-ValueKind Double -TransitionSeconds {window} -MaxRounds 22 "
        f"-HoldAfterRollSeconds 240 -SnapshotMaxBytes 402653184"
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", default=str(DEFAULT_DB), help="treader sqlite db path")
    parser.add_argument("--session", default="", help="battle session id (default: newest 11.19)")
    parser.add_argument("--participant", default="", help="participant id (default: most-moving)")
    parser.add_argument("--window", type=float, default=4.0, help="transition window seconds (default 4)")
    parser.add_argument("--speed", type=float, default=1.0, help="replay speed multiplier (default 1)")
    parser.add_argument("--json", default="", help="also write JSON to this path")
    args = parser.parse_args(argv)

    db = Path(args.db)
    if not db.exists():
        raise SystemExit(f"database not found: {db}")
    con = sqlite3.connect(str(db))
    con.row_factory = sqlite3.Row

    session = pick_session(con)
    if args.session:
        found = con.execute(
            "SELECT id, game_version, map_name, duration_ticks, "
            "viewpoint_participant_id FROM battle_sessions WHERE id=?",
            (args.session,),
        ).fetchone()
        if found is None:
            raise SystemExit(f"session not found: {args.session}")
        session = dict(found)

    participants = moving_participants(con, session["id"])
    if not participants:
        raise SystemExit("no participants with samples for this session")
    participant_id = args.participant or participants[0][0]
    samples = participant_samples(con, session["id"], participant_id)
    if not samples:
        raise SystemExit(f"no samples for participant {participant_id}")

    wins = windowed_displacements(samples, args.window, args.speed)
    if not wins:
        raise SystemExit("no usable window-straddling sample pairs (samples too sparse)")

    dist2d = [w["dist2d"] for w in wins]
    dxs = [w["dx"] for w in wins]
    dzs = [w["dz"] for w in wins]

    d2 = summarize(dist2d)
    sx = summarize(dxs)
    sz = summarize(dzs)
    target, tol = recommend(d2["median"], d2["p90"], d2["max"])

    # replayTime delta per window in ticks and seconds
    dt_ticks = int(args.window * args.speed * TICKS_PER_SECOND)

    result = {
        "scan": "replay-delta-extractor",
        "session_id": session["id"],
        "game_version": session["game_version"],
        "map_name": session["map_name"],
        "participant_id": participant_id,
        "participant_rank": participants.index((participant_id, next(c for p, c in participants if p == participant_id))),
        "window_seconds": args.window,
        "speed": args.speed,
        "tick_rate": TICKS_PER_SECOND,
        "window_ticks": dt_ticks,
        "sample_pairs": len(wins),
        "displacement_2d": d2,
        "delta_x": sx,
        "delta_z": sz,
        "recommended": {
            "position_delta_target": target,
            "position_delta_tolerance": tol,
            "replay_time_delta_seconds": args.window * args.speed,
            "replay_time_delta_ticks": dt_ticks,
        },
        "commands": {
            "float_position_pilot": build_command(session["id"], "Float", target, tol, args.window),
            # Double replayTime target = window* speed seconds; tolerance floored
            # at 10% of target so a 4s window at 1x allows ~0.4s of timing jitter.
            "double_replay_time_pilot": build_command(
                session["id"], "Double",
                round(args.window * args.speed, 4),
                round(max(tol, args.window * args.speed * 0.10), 4),
                args.window,
            ),
        },
    }

    if args.json:
        Path(args.json).write_text(
            json.dumps(result, indent=2), encoding="utf-8"
        )

    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
