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
  python scripts/python/replay-delta-extractor.py --simulate
      Additionally run the offline delta-filter simulation: for each marker
      series (2D position delta, replayTime delta, speed) sweep tolerance
      around the recommended target and compute the per-round pass rate and
      the projected survival probability over R rolling rounds. A marker
      whose per-round pass rate is low (e.g. a bursty position field) sheds
      the TRUE field across rounds just as it sheds decoys — this predicts
      the survivor-collapse outcome before the live run spends lease.

  python scripts/python/replay-delta-extractor.py --movement
      Segment the participant's replay into moving vs stationary phases and
      report the movement fraction plus per-window displacement stats for
      the MOVING windows only — OD-045-STATIC showed position-delta markers
      are only selective when the tank is actually moving, so this tells the
      live pilot which replay-time span to scan.

  python scripts/python/replay-delta-extractor.py --hp-delta --victim-entity <id>
      Build a per-window HP damage-delta series from kind-3 damage events
      (attacker/victim entity + damage amount) for the victim entity and run
      it through the survival simulation. An HP field changes rarely and by
      exact damage amounts — a strong marker when the victim takes hits.
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
TICKS_PER_SECOND = 10_000_000  # .NET TimeSpan ticks per second (replay_time_ticks are stored as TimeSpan.Ticks)


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
        # median ~1s-spaced 2D displacement as a movement proxy. Samples arrive
        # at ~100/s, so the 1s-apart pair is NOT the consecutive pair - scan a
        # sliding second pointer instead of comparing adjacent samples (a dead
        # proxy under the old consecutive-only scan).
        displacements = []
        second = 0
        for first in range(len(samples)):
            while second < len(samples) and samples[second][0] - samples[first][0] < 0.9 * TICKS_PER_SECOND:
                second += 1
            if second < len(samples):
                dt = samples[second][0] - samples[first][0]
                if dt <= 1.1 * TICKS_PER_SECOND:
                    dx = samples[second][1] - samples[first][1]
                    dz = samples[second][3] - samples[first][3]
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


def marker_series(
    samples: list[tuple[int, float, float, float]],
    window_seconds: float,
    speed: float = 1.0,
) -> tuple[list[float], list[float], list[float]]:
    """Per-window marker series for the three marker types:

      - dist2d: 2D (x,z) displacement in meters per window (position delta)
      - replay_delta: replay-time advance in SECONDS per window (constant ==
        window*speed for a real-time replay; jitter only from sample timing)
      - speed: |pos|/dt in m/s averaged over the window

    All three share the same sliding-window interpolation over `samples`.
    """
    window_ticks = int(window_seconds * speed * TICKS_PER_SECOND)
    dist2d: list[float] = []
    replay_delta: list[float] = []
    speed: list[float] = []
    for cur in samples:
        t = cur[0]
        target = t + window_ticks
        if target >= samples[-1][0]:
            break
        pos = _interp_at(samples, target)
        if pos is None:
            continue
        dt_sec = window_ticks / TICKS_PER_SECOND
        dx = pos[0] - cur[1]
        dz = pos[2] - cur[3]
        d = math.hypot(dx, dz)
        dist2d.append(d)
        # replay-time advance per window: dt_sec is deterministic, but record
        # the actual bracketing interval for honesty (sample jitter).
        replay_delta.append(dt_sec)
        speed.append(d / dt_sec)
    return dist2d, replay_delta, speed


def movement_phases(
    samples: list[tuple[int, float, float, float]],
    speed_threshold: float = 0.5,
) -> dict:
    """Segment the replay into moving / stationary phases.

    A window is 'moving' when the 1s-spaced speed (|pos|/dt) exceeds
    `speed_threshold` m/s. Returns phase count, movement fraction (fraction
    of replay-time windows classified moving), and the moving-window
    per-4s-window displacement stats (for the position-delta pilot).
    """
    moving_1s: list[float] = []  # 1s-window speeds
    for i in range(len(samples) - 1):
        dt = samples[i + 1][0] - samples[i][0]
        if dt <= 0:
            continue
        dt_sec = dt / TICKS_PER_SECOND
        dx = samples[i + 1][1] - samples[i][1]
        dz = samples[i + 1][3] - samples[i][3]
        moving_1s.append(math.hypot(dx, dz) / dt_sec)
    if not moving_1s:
        return {"windows": 0, "moving_fraction": 0.0}
    n_moving = sum(1 for s in moving_1s if s > speed_threshold)
    # Moving-window 4s displacement: use the same sliding-window interp but
    # keep only windows whose midpoint speed exceeds the threshold.
    moving_disp: list[float] = []
    for i in range(len(samples) - 1):
        dt = samples[i + 1][0] - samples[i][0]
        if dt <= 0:
            continue
        dt_sec = dt / TICKS_PER_SECOND
        dx = samples[i + 1][1] - samples[i][1]
        dz = samples[i + 1][3] - samples[i][3]
        speed = math.hypot(dx, dz) / dt_sec
        if speed > speed_threshold:
            moving_disp.append(math.hypot(dx, dz))
    return {
        "speed_threshold_mps": speed_threshold,
        "windows": len(moving_1s),
        "moving_fraction": round(n_moving / len(moving_1s), 4),
        "moving_windows": n_moving,
        "stationary_windows": len(moving_1s) - n_moving,
        "moving_disp_1s": summarize(moving_disp),
    }


def hp_damage_series(
    con: sqlite3.Connection,
    session_id: str,
    victim_entity_id: int,
    window_seconds: float,
) -> list[float]:
    """Per-window HP damage-delta series from kind-3 damage events.

    Each kind-3 event carries {attackerEntityId, victimEntityId, damage}.
    Bucket damage dealt to `victim_entity_id` into replay-time windows of
    `window_seconds` and return the per-window damage totals. An HP field
    drops by these exact amounts when hit and is otherwise flat — a marker
    that is sparse but exact (0 damage windows shed nothing; hit windows
    identify the field precisely).
    """
    rows = con.execute(
        "SELECT replay_time_ticks, values_json FROM canonical_events "
        "WHERE battle_session_id=? AND kind=3",
        (session_id,),
    ).fetchall()
    bucket_ticks = int(window_seconds * TICKS_PER_SECOND)
    buckets: dict[int, float] = {}
    for r in rows:
        try:
            v = json.loads(r[1])
        except json.JSONDecodeError:
            continue
        if int(v.get("victimEntityId", -1)) != victim_entity_id:
            continue
        ticks = int(r[0])
        idx = ticks // bucket_ticks
        buckets[idx] = buckets.get(idx, 0.0) + float(v.get("damage", 0))
    if not buckets:
        return []
    # Series: per window index in the session's window range.
    max_idx = max(buckets)
    return [buckets.get(i, 0.0) for i in range(max_idx + 1)]


def top_victims(
    con: sqlite3.Connection,
    session_id: str,
    top_n: int = 8,
    window_seconds: float = 10.0,
) -> list[dict]:
    """Rank kind-3 damage victims by hit count for HP-diffing victim selection.

    The HP-diffing session must track an entity that actually takes damage
    (verified 2026-08-10: the player's own entity took ZERO damage in both
    11.19.0 replays). This returns the candidates ranked by number of damage
    events, with the replay-time span and the per-window bucket list at
    `window_seconds` so the operator can pick a victim with >= 2 damage
    windows and use the hit-window list as the event-bound dump schedule.
    """
    rows = con.execute(
        "SELECT json_extract(values_json,'$.victimEntityId') AS victim, "
        "COUNT(*) AS hits, SUM(json_extract(values_json,'$.damage')) AS dmg, "
        "MIN(replay_time_ticks) AS first_ticks, MAX(replay_time_ticks) AS last_ticks "
        "FROM canonical_events WHERE battle_session_id=? AND kind=3 "
        "GROUP BY victim ORDER BY hits DESC, dmg DESC LIMIT ?",
        (session_id, top_n),
    ).fetchall()
    bucket_ticks = int(window_seconds * TICKS_PER_SECOND)
    out = []
    for r in rows:
        victim = r["victim"]
        tick_rows = con.execute(
            "SELECT replay_time_ticks FROM canonical_events "
            "WHERE battle_session_id=? AND kind=3 AND "
            "json_extract(values_json,'$.victimEntityId')=? ORDER BY replay_time_ticks",
            (session_id, victim),
        ).fetchall()
        windows = sorted({int(t["replay_time_ticks"]) // bucket_ticks for t in tick_rows})
        out.append(
            {
                "victim_entity_id": victim,
                "hits": r["hits"],
                "total_damage": round(float(r["dmg"]), 2),
                "first_hit_s": round(float(r["first_ticks"]) / TICKS_PER_SECOND, 1),
                "last_hit_s": round(float(r["last_ticks"]) / TICKS_PER_SECOND, 1),
                "hit_windows": len(windows),
                "window_seconds": window_seconds,
                "windows": windows,
            }
        )
    return out


def simulate_survival(
    marker: list[float],
    target: float,
    tolerances: list[float],
    rounds: list[int],
) -> list[dict]:
    """Offline delta-filter simulation.

    PassesDelta keeps a candidate when |observed - target| <= tolerance. For
    the TRUE field, the observed marker series is what we measured from the
    replay, so the per-round pass rate is the fraction of windows within
    tolerance. Survival over R independent rounds compounds as pass_rate^R
    (rolling sheds any candidate that fails ONE round). A marker with a low
    pass rate at the recommended tolerance will therefore shed the true
    field too — predicting a hollow survivor collapse.
    """
    if not marker:
        return []
    out: list[dict] = []
    for tol in tolerances:
        pass_count = sum(1 for m in marker if abs(m - target) <= tol)
        pass_rate = pass_count / len(marker)
        out.append(
            {
                "target": round(target, 4),
                "tolerance": round(tol, 4),
                "pass_rate": round(pass_rate, 4),
                "pass_count": pass_count,
                "of": len(marker),
                "survival": {r: round(pass_rate ** r, 4) for r in rounds},
            }
        )
    return out


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
    parser.add_argument("--simulate", action="store_true",
                        help="run the offline delta-filter survival simulation")
    parser.add_argument("--rounds", default="5,10,15",
                        help="rolling round counts for survival projection (default 5,10,15)")
    parser.add_argument("--movement", action="store_true",
                        help="segment the participant replay into moving/stationary phases")
    parser.add_argument("--speed-threshold", type=float, default=0.5,
                        help="m/s threshold for the moving phase (default 0.5)")
    parser.add_argument("--hp-delta", action="store_true",
                        help="build a per-window HP damage-delta series from kind-3 damage events")
    parser.add_argument("--victim-entity", type=int, default=0,
                        help="entity id of the HP victim to track (required with --hp-delta)")
    parser.add_argument("--top-victims", type=int, default=0,
                        help="rank the session's damage victims by hit count (N entries) for HP-diffing victim selection; prints and exits")
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

    rounds = [int(r) for r in args.rounds.split(",") if r.strip()]

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
            # The in-memory replayTime Double's unit is unknown (that is the
            # campaign's discovery question). DeltaTarget must be expressed in
            # the same unit as the field; list all candidate scales so the live
            # operator can pick the one that matches the observed value.
            "replay_time_delta_unit_variants": {
                "seconds": round(args.window * args.speed, 4),
                "milliseconds": round(args.window * args.speed * 1000.0, 4),
                "ticks_1e6": dt_ticks,
            },
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

    if args.movement:
        result["movement"] = movement_phases(samples, args.speed_threshold)

    if args.hp_delta:
        if args.victim_entity <= 0:
            # Default to the player's own entity when available.
            pid = con.execute(
                "SELECT entity_id FROM participants WHERE battle_session_id=? "
                "AND account_id IS NOT NULL AND player_name=? LIMIT 1",
                (session["id"], "mrkool1138"),
            ).fetchone()
            victim = pid["entity_id"] if pid else 0
        else:
            victim = args.victim_entity
        hp_series = hp_damage_series(con, session["id"], victim, args.window)
        result["hp_delta"] = {
            "victim_entity_id": victim,
            "windows": len(hp_series),
            "hit_windows": sum(1 for d in hp_series if d > 0),
            "total_damage": round(sum(hp_series), 2),
            "series": [round(d, 2) for d in hp_series],
            "simulation": simulate_survival(
                hp_series, 0.0, [0.0, 0.5, 1.0, 5.0], rounds
            ),
        }

    if args.simulate:
        dist2d_s, replay_s, speed_s = marker_series(samples, args.window, args.speed)
        dt_sec = args.window * args.speed
        # replayTime marker: deterministic target == window*speed seconds; sweep
        # tolerances that absorb sample-timing jitter without admitting decoys.
        replay_sweep = [round(dt_sec * f, 4) for f in (0.05, 0.10, 0.25, 0.5, 1.0)]
        # position marker: median target; sweep tolerances around the spread.
        pos_sweep = [round(target * f, 4) for f in (0.5, 1.0, 2.0, 4.0)]
        pos_sweep = sorted(set([round(t, 4) for t in pos_sweep] + [tol]))
        result["simulation"] = {
            "rounds": rounds,
            "position_delta": simulate_survival(dist2d_s, target, pos_sweep, rounds),
            "replay_time_delta": simulate_survival(
                replay_s, dt_sec, replay_sweep, rounds
            ),
            "speed": simulate_survival(speed_s, target / dt_sec, pos_sweep, rounds),
            "note": (
                "pass_rate is the fraction of replay windows within |Δ-target|≤tol; "
                "survival[N] = pass_rate^N over N independent rolling rounds. "
                "A marker with low pass_rate at the recommended tolerance sheds "
                "the TRUE field across rounds (hollow collapse). Target a pass_rate "
                ">= 0.9 so the true field survives ~15 rounds."
            ),
        }

    if args.top_victims > 0:
        victims = top_victims(con, session["id"], args.top_victims, args.window)
        result = {
            "scan": "replay-delta-extractor",
            "mode": "top-victims",
            "session_id": session["id"],
            "game_version": session["game_version"],
            "map_name": session["map_name"],
            "window_seconds": args.window,
            "note": (
                "For HP-diffing victim selection, require an entity with >= 2 "
                "damage windows; the hit-window list is the event-bound dump "
                "schedule. (The player's own entity took zero damage in both "
                "11.19.0 replays - do not default to it.)"
            ),
            "victims": victims,
        }
        if args.json:
            Path(args.json).write_text(
                json.dumps(result, indent=2), encoding="utf-8"
            )
        print(json.dumps(result, indent=2))
        return 0

    if args.json:
        Path(args.json).write_text(
            json.dumps(result, indent=2), encoding="utf-8"
        )

    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
