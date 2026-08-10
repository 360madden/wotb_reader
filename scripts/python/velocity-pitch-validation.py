#!/usr/bin/env python3
"""velocity-pitch-validation.py — offline validation of the packet rotation
and derived velocity semantics from a decoded replay.

Reads a decoded battle session from the treader SQLite store and validates,
against the persisted type-10 packet tail (yaw/pitch/roll, migration 5):

  - Velocity series: per-sample finite difference of x/y/z over dt (pairs
    with dt >= 0.05s only — sub-ms duplicate packets produce bogus spikes).
    The ring record's +0x28 triple is the memory-side velocity target; this
    tool produces the replay-derived ground truth (speed + direction) the
    live read will be correlated against.
  - Yaw vs heading: while the tank moves, the packet yaw (radians) must
    agree with the position-derived motion heading. Reversals (180 deg
    mismatches) are EXPECTED and reported separately — the packet yaw is the
    FACING, not the motion vector.
  - Pitch vs slope: over multi-second moving windows, the packet pitch
    tracks the terrain slope with a FLIPPED sign (pitch ≈ -atan2(dY, dH),
    validated to ~1.3 deg stdev on the 11.19.0 replays). Stationary pitch is
    exactly constant (like yaw).
  - Roll: near-zero when stationary and exactly constant there; it varies
    during movement (banking/turns) — the third dynamic rotation axis.

Output is JSON on stdout (or --json PATH), plus a per-replay summary line.
--self-test runs the built-in synthetic fixture (no DB required).

Modes:
  python scripts/python/velocity-pitch-validation.py
      Use the most recent session that has yaw samples, viewpoint entity.
  python scripts/python/velocity-pitch-validation.py --session <id> --entity <id>
  python scripts/python/velocity-pitch-validation.py --self-test
"""

import argparse
import json
import math
import os
import sqlite3
import sys

DEFAULT_DB = os.environ.get("WOTB_DB", ".data/treader.db")

HEADING_TOLERANCE_DEG = 15.0   # |angle diff| (wrap) considered agreement
STATIONARY_SPEED = 0.5         # m/s below which the tank is stationary
MIN_PAIR_DT = 0.05             # s; ignore sub-50ms duplicate-packet pairs
SLOPE_WINDOW_SAMPLES = 40      # ~4s windows for slope-vs-pitch comparison
SLOPE_MIN_TRAVEL = 8.0         # metres of horizontal travel to trust slope
PITCH_TOLERANCE_DEG = 15.0


def load_session(db: str, session_id: str):
    con = sqlite3.connect(db)
    con.row_factory = sqlite3.Row
    cur = con.cursor()
    if not session_id:
        row = cur.execute(
            """SELECT b.id FROM battle_sessions b
               JOIN position_samples p ON p.battle_session_id = b.id
               WHERE p.yaw IS NOT NULL
               GROUP BY b.id ORDER BY MAX(p.replay_time_ticks) DESC LIMIT 1"""
        ).fetchone()
        if row is None:
            raise SystemExit("no session with yaw samples found; pass --session")
        session_id = row["id"]
    session = cur.execute(
        "SELECT * FROM battle_sessions WHERE id = ?", (session_id,)
    ).fetchone()
    if session is None:
        raise SystemExit(f"session not found: {session_id}")
    return con, session


def viewpoint_entity(con, session):
    cur = con.cursor()
    row = cur.execute(
        """SELECT entity_id FROM participants WHERE battle_session_id = ?
           AND id = (SELECT viewpoint_participant_id FROM battle_sessions WHERE id = ?)""",
        (session["id"], session["id"]),
    ).fetchone()
    return row["entity_id"] if row else None


def load_samples(con, session_id, entity_id):
    cur = con.cursor()
    rows = cur.execute(
        """SELECT replay_time_ticks, raw_x, raw_y, raw_z, yaw, pitch, roll
           FROM position_samples
           WHERE battle_session_id = ? AND entity_id = ? AND yaw IS NOT NULL
           ORDER BY replay_time_ticks""",
        (session_id, entity_id),
    ).fetchall()
    return [
        {
            "t": r["replay_time_ticks"] / 1e7,
            "x": r["raw_x"],
            "y": r["raw_y"],
            "z": r["raw_z"],
            "yaw": r["yaw"],
            "pitch": r["pitch"],
            "roll": r["roll"],
        }
        for r in rows
    ]


def wrap_pi(angle):
    while angle > math.pi:
        angle -= 2.0 * math.pi
    while angle < -math.pi:
        angle += 2.0 * math.pi
    return angle


def analyze(samples):
    """Velocity series + rotation-axis validation from a sample timeline."""
    stats = {
        "samples": len(samples),
        "moving_pairs": 0,
        "stationary_pairs": 0,
        "heading_agree": 0,
        "heading_reversal": 0,
        "heading_mismatch": 0,
        "max_speed": 0.0,
        "top_speed_window": 0.0,
        "yaw_constant_when_stationary": 0,
        "pitch_slope_windows": 0,
        "pitch_slope_agree": 0,
        "pitch_slope_sum": 0.0,
        "pitch_slope_stdev": 0.0,
        "pitch_range": [0.0, 0.0],
        "roll_range": [0.0, 0.0],
        "roll_constant_when_stationary": 0,
        "stationary_windows": 0,
    }
    if len(samples) < 2:
        return stats

    # Per-pair finite-difference velocity + yaw-vs-heading.
    for i in range(1, len(samples)):
        a, b = samples[i - 1], samples[i]
        dt = b["t"] - a["t"]
        if dt < MIN_PAIR_DT:
            continue
        dx, dy, dz = b["x"] - a["x"], b["y"] - a["y"], b["z"] - a["z"]
        speed = math.sqrt(dx * dx + dy * dy + dz * dz) / dt
        horizontal = math.sqrt(dx * dx + dz * dz)
        stats["max_speed"] = max(stats["max_speed"], speed)

        if speed >= STATIONARY_SPEED:
            stats["moving_pairs"] += 1
            motion_heading = wrap_pi(math.atan2(dx, dz)) if horizontal >= 0.01 else None
            if motion_heading is not None:
                diff = abs(wrap_pi(a["yaw"] - motion_heading))
                deg = math.degrees(diff)
                if deg <= HEADING_TOLERANCE_DEG:
                    stats["heading_agree"] += 1
                elif deg >= 180.0 - HEADING_TOLERANCE_DEG:
                    stats["heading_reversal"] += 1
                else:
                    stats["heading_mismatch"] += 1
        else:
            stats["stationary_pairs"] += 1

    # Windowed slope-vs-pitch (flipped-sign hypothesis: pitch + slope ~ 0).
    pitch_slope_residuals = []
    for i in range(0, len(samples) - SLOPE_WINDOW_SAMPLES, SLOPE_WINDOW_SAMPLES // 4):
        a = samples[i]
        b = samples[i + SLOPE_WINDOW_SAMPLES]
        dt = b["t"] - a["t"]
        if dt < 3.0 or dt > 5.5:
            continue
        dx, dy, dz = b["x"] - a["x"], b["y"] - a["y"], b["z"] - a["z"]
        horizontal = math.sqrt(dx * dx + dz * dz)
        if horizontal < SLOPE_MIN_TRAVEL:
            continue
        slope = math.atan2(dy, horizontal)
        mid = samples[i + SLOPE_WINDOW_SAMPLES // 2]
        if mid["pitch"] is None:
            continue
        residual = slope + mid["pitch"]  # flipped-sign hypothesis
        pitch_slope_residuals.append(residual)
        stats["pitch_slope_windows"] += 1
        if math.degrees(abs(wrap_pi(residual))) <= PITCH_TOLERANCE_DEG:
            stats["pitch_slope_agree"] += 1

    if pitch_slope_residuals:
        mean = sum(pitch_slope_residuals) / len(pitch_slope_residuals)
        stats["pitch_slope_sum"] = mean
        stats["pitch_slope_stdev"] = math.sqrt(
            sum((r - mean) ** 2 for r in pitch_slope_residuals) / len(pitch_slope_residuals)
        )

    # Stationary constancy: run-length windows where speed stays low.
    stationary_streak = 0
    for i in range(1, len(samples)):
        a, b = samples[i - 1], samples[i]
        dt = b["t"] - a["t"]
        if dt < MIN_PAIR_DT:
            continue
        dx, dy, dz = b["x"] - a["x"], b["y"] - a["y"], b["z"] - a["z"]
        speed = math.sqrt(dx * dx + dy * dy + dz * dz) / dt
        if speed < STATIONARY_SPEED:
            stationary_streak += 1
            if (stationary_streak >= 3 and a["yaw"] is not None and b["yaw"] is not None
                    and abs(wrap_pi(b["yaw"] - a["yaw"])) < 1e-4):
                stats["yaw_constant_when_stationary"] += 1
            if (stationary_streak >= 3 and a["roll"] is not None and b["roll"] is not None
                    and abs(b["roll"] - a["roll"]) < 1e-4):
                stats["roll_constant_when_stationary"] += 1
        else:
            if stationary_streak >= 3:
                stats["stationary_windows"] += 1
            stationary_streak = 0

    pitches = [s["pitch"] for s in samples if s["pitch"] is not None]
    rolls = [s["roll"] for s in samples if s["roll"] is not None]
    if pitches:
        stats["pitch_range"] = [min(pitches), max(pitches)]
    if rolls:
        stats["roll_range"] = [min(rolls), max(rolls)]
    return stats


def self_test():
    """Synthetic fixture: a tank driving +z (yaw 0, pitch -slope), then reversing."""
    samples = []
    t = 0.0
    # Drive +z for 10s at 10 m/s on a slight uphill (slope +0.05 -> pitch -0.05).
    for i in range(101):
        samples.append({"t": t, "x": 0.0, "y": 0.05 * i, "z": i * 1.0,
                        "yaw": 0.0, "pitch": -0.05, "roll": 0.0})
        t += 0.1
    # Reverse: velocity heading flips to pi, yaw STAYS 0 (facing unchanged).
    for i in range(1, 51):
        samples.append({"t": t, "x": 0.0, "y": 5.0 - 0.05 * i, "z": 100.0 - i * 1.0,
                        "yaw": 0.0, "pitch": -0.05, "roll": 0.0})
        t += 0.1
    stats = analyze(samples)
    assert stats["heading_agree"] > 0, "forward motion must agree with yaw"
    assert stats["heading_reversal"] > 0, "reversal must be detected as 180 deg"
    assert stats["heading_mismatch"] == 0, "no genuine mismatches in fixture"
    assert stats["pitch_slope_windows"] > 0, "slope windows must be found"
    assert stats["pitch_slope_agree"] == stats["pitch_slope_windows"], \
        "flipped-sign pitch must agree with slope"
    print("self-test OK: forward agree + reversal + pitch=-slope verified")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", default=DEFAULT_DB, help="treader sqlite db path")
    parser.add_argument("--session", default="", help="battle session id (default: newest with yaw)")
    parser.add_argument("--entity", type=int, default=0, help="entity id (default: viewpoint entity)")
    parser.add_argument("--json", default="", help="also write JSON to this path")
    parser.add_argument("--self-test", action="store_true", help="run the synthetic fixture")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    con, session = load_session(args.db, args.session)
    entity = args.entity or viewpoint_entity(con, session)
    if entity is None:
        raise SystemExit("no viewpoint entity; pass --entity")
    samples = load_samples(con, session["id"], entity)
    stats = analyze(samples)

    report = {
        "command": "velocity-pitch-validation",
        "session": session["id"],
        "map": session["map_name"],
        "entity": entity,
        "samples": stats["samples"],
        "movingPairs": stats["moving_pairs"],
        "stationaryPairs": stats["stationary_pairs"],
        "maxSpeed": round(stats["max_speed"], 3),
        "heading": {
            "agree": stats["heading_agree"],
            "reversal": stats["heading_reversal"],
            "mismatch": stats["heading_mismatch"],
        },
        "yawStationaryConstant": stats["yaw_constant_when_stationary"],
        "pitch": {
            "slopeWindows": stats["pitch_slope_windows"],
            "slopeAgree": stats["pitch_slope_agree"],
            "slopeResidualMean": round(stats["pitch_slope_sum"], 4),
            "slopeResidualStdevDeg": round(math.degrees(stats["pitch_slope_stdev"]), 2),
            "range": [round(v, 4) for v in stats["pitch_range"]],
        },
        "roll": {
            "range": [round(v, 4) for v in stats["roll_range"]],
            "stationaryConstant": stats["roll_constant_when_stationary"],
        },
    }

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=1)

    agree = stats["heading_agree"] + stats["heading_reversal"]
    total = stats["heading_agree"] + stats["heading_reversal"] + stats["heading_mismatch"]
    pct = (100.0 * agree / total) if total else 0.0
    print(
        f"{session['map_name']}: {stats['samples']} samples, moving={stats['moving_pairs']}, "
        f"heading {agree}/{total} ({pct:.0f}% incl. reversals), max speed {stats['max_speed']:.1f} m/s, "
        f"pitch=-slope {stats['pitch_slope_agree']}/{stats['pitch_slope_windows']} "
        f"(residual {stats['pitch_slope_sum']:+.3f} +/- {math.degrees(stats['pitch_slope_stdev']):.1f} deg), "
        f"roll range [{stats['roll_range'][0]:+.3f}, {stats['roll_range'][1]:+.3f}]"
    )
    print(json.dumps(report, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
