#!/usr/bin/env python3
"""replay-delta-extractor.py — derive delta-compare targets from a decoded replay.

Reads a decoded battle session from the treader SQLite store and computes the
replay-derived delta values the rolling campaign's CompareMode='delta' needs:

  - Position delta (Float X/Z): the world-space displacement of a moving
    participant over a transition window. This is the primary pilot target —
    it is unit-consistent (meters) and directly comparable to in-memory Float
    position fields.
  - replayTime delta (Double): the replay-time advance per window, in both
    raw ticks (.NET TimeSpan ticks, 1e7/sec - replay_time_ticks are stored
    as TimeSpan.Ticks; see --self-test) and seconds, so an operator can try
    scale hypotheses against the in-memory Double.

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

  python scripts/python/replay-delta-extractor.py --damage-dealt [--attacker-entity <id>]
      The increment-direction mirror: bucket the damage DEALT BY the target
      (default the session's viewpoint/player entity) into windows and emit
      the event-bound dump schedule for a scoreboard damage-dealt correlation
      (hp-diff --direction increment). Verified 2026-08-10: the player dealt
      damage in both 11.19.0 replays (5 events each, >= 2 windows) — unlike
      HP, the player's own stat IS a viable correlation target.

  python scripts/python/replay-delta-extractor.py --heading-delta
      Heading-change (turn) series for the facing campaign: per-window delta
      of BOTH the position-derived motion heading and the packet yaw
      (position_samples.yaw, migration 5), movement-gated (a stationary tank
      has no meaningful heading) and wrap-aware (deltas normalized to
      [-pi, pi], so a 359->1 deg turn is +2 deg, not -358 deg). Reports the
      per-window turn distribution (radians + degrees), how often the wrap
      normalization actually mattered, and a recommended yaw-delta target for
      the live pilot. Verified 2026-08-10 on both 11.19.0 replays: yaw and
      motion heading agree while moving forward, so their per-window deltas
      must match too.
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


def pick_yaw_session(con: sqlite3.Connection) -> dict:
    """Most recent 11.19 session WITH packet-yaw samples (migration 5).

    `pick_session` selects by battle_time alone and can land on an older
    duplicate decode whose yaw column is NULL (the yaw decoder shipped after
    the first import). The facing/heading modes need the yaw series, so they
    select from the subset that actually has it.
    """
    cur = con.execute(
        "SELECT s.id, s.game_version, s.map_name, s.duration_ticks, "
        "s.viewpoint_participant_id, COUNT(p.id) AS sample_count "
        "FROM battle_sessions s "
        "JOIN position_samples p ON p.battle_session_id = s.id "
        "WHERE s.game_version LIKE '11.19%' AND p.yaw IS NOT NULL "
        "GROUP BY s.id ORDER BY s.battle_time_utc DESC LIMIT 1"
    )
    row = cur.fetchone()
    if row is None:
        raise SystemExit(
            "no 11.19 session with packet-yaw samples found; "
            "re-decode a replay with the migration-5 yaw decoder"
        )
    return {
        "id": row[0],
        "game_version": row[1],
        "map_name": row[2],
        "duration_ticks": row[3],
        "viewpoint_participant_id": row[4],
        "sample_count": row[5],
    }


def participant_samples(
    con: sqlite3.Connection,
    session_id: str,
    participant_id: str,
    with_yaw: bool = False,
) -> list[tuple[int, float, float, float, float | None]]:
    """(replay_time_ticks, raw_x, raw_y, raw_z[, yaw]) ordered by sequence.

    `yaw` is the packet-derived facing (position_samples.yaw, migration 5)
    and is None when `with_yaw` is False or the sample predates the column.
    """
    yaw_col = ", yaw" if with_yaw else ""
    rows = con.execute(
        f"SELECT replay_time_ticks, raw_x, raw_y, raw_z{yaw_col} FROM position_samples "
        "WHERE battle_session_id=? AND participant_id=? ORDER BY sequence",
        (session_id, participant_id),
    ).fetchall()
    if with_yaw:
        return [
            (int(r[0]), float(r[1]), float(r[2]), float(r[3]), None if r[4] is None else float(r[4]))
            for r in rows
        ]
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


def wrap_pi(angle: float) -> float:
    """Normalize an angle (radians) to [-pi, pi] — the wrap-aware convention
    used by velocity-pitch-validation.py for yaw-vs-heading diffs."""
    while angle > math.pi:
        angle -= 2.0 * math.pi
    while angle < -math.pi:
        angle += 2.0 * math.pi
    return angle


def unwrap_radians(values: list[float]) -> list[float]:
    """Unwrap a wrapped angle series (radians) so consecutive values differ
    by less than pi. Used to detect ±pi seam crossings that the wrapped
    deltas alone would hide (a 359->1 deg turn wraps to +2 deg, not -358)."""
    if not values:
        return []
    out = [values[0]]
    for v in values[1:]:
        prev = out[-1]
        while v - prev > math.pi:
            v -= 2.0 * math.pi
        while v - prev < -math.pi:
            v += 2.0 * math.pi
        out.append(v)
    return out


def heading_delta_series(
    samples: list[tuple[int, float, float, float, float | None]],
    window_seconds: float,
    speed: float = 1.0,
    speed_threshold: float = 0.5,
) -> dict:
    """Movement-gated, wrap-aware per-window heading-change series.

    For every consecutive-sample pair whose 1s-window speed exceeds
    `speed_threshold` (a stationary tank has no meaningful heading), compute:
      - motion heading delta: atan2(dx,dz) change across the window
      - packet yaw delta: yaw[t+window] - yaw[t] when yaw is available
    Deltas are normalized to [-pi, pi] (wrap-aware), so a 359->1 deg turn
    reads +2 deg, not -358 deg. `wrap_crossings` counts the windows where
    the unwrapped delta differs from the wrapped one — i.e. where the
    ±pi seam actually mattered.
    Returns the turn distribution (radians + degrees), the crossing count,
    and a recommended yaw-delta target/tolerance for the live pilot.
    """
    window_ticks = int(window_seconds * speed * TICKS_PER_SECOND)
    motion_deltas: list[float] = []
    yaw_deltas: list[float] = []
    moving_windows = 0
    # Seam crossings: adjacent RAW yaw pairs with |y - x| > pi. Both values
    # live in [-pi, pi], so the raw difference can only exceed pi when the
    # ±pi seam lies between the two samples on the shorter arc — the case a
    # naive (non-wrap-aware) delta gets wrong by ~2*pi. The wrapped deltas
    # below are the corrected values.
    yaw_raw = [s[4] for s in samples if s[4] is not None]
    wrap_crossings = sum(
        1 for x, y in zip(yaw_raw, yaw_raw[1:]) if abs(y - x) > math.pi
    )
    # Unwrap the packet yaw series so endpoint yaw can be INTERPOLATED
    # across the ±pi seam instead of losing the window when the exact tick
    # has no sample (samples are ~1s apart; exact 4s-aligned ticks are rare).
    yaw_t: list[int] = [s[0] for s in samples if s[4] is not None]
    yaw_uw: list[float] = unwrap_radians(yaw_raw)
    # unwrapped yaw per sample index (None where the sample has no yaw)
    uw_by_sample: list[float | None] = []
    yi = 0
    for s in samples:
        if s[4] is not None:
            uw_by_sample.append(yaw_uw[yi])
            yi += 1
        else:
            uw_by_sample.append(None)

    def yaw_uw_at(target_ticks: int) -> float | None:
        """UNWRAPPED yaw interpolated at a target tick; None outside the yaw
        sample range. The wrapped value is wrap_pi() of this."""
        if not yaw_uw or target_ticks <= yaw_t[0] or target_ticks >= yaw_t[-1]:
            return None
        lo, hi = 0, len(yaw_t) - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if yaw_t[mid] <= target_ticks:
                lo = mid
            else:
                hi = mid
        f = (target_ticks - yaw_t[lo]) / (yaw_t[hi] - yaw_t[lo])
        return yaw_uw[lo] + (yaw_uw[hi] - yaw_uw[lo]) * f

    # Build the moving-window endpoint list first (interpolated x/z when the
    # exact target tick has no sample, exactly like marker_series).
    ends: list[tuple[float, float]] = []
    for a in samples:
        target = a[0] + window_ticks
        if target >= samples[-1][0]:
            break
        pos = _interp_at([(s[0], s[1], s[2], s[3]) for s in samples], target)
        if pos is None:
            # Exactly on a sample: use it directly.
            exact = next((s for s in samples if s[0] == target), None)
            ends.append((exact[1], exact[3]) if exact else (samples[-1][1], samples[-1][3]))
        else:
            ends.append((pos[0], pos[2]))

    for i in range(len(samples) - 1):
        a = samples[i]
        b = samples[i + 1]
        dt = b[0] - a[0]
        if dt <= 0:
            continue
        dt_sec = dt / TICKS_PER_SECOND
        speed_mps = math.hypot(b[1] - a[1], b[3] - a[3]) / dt_sec
        if speed_mps <= speed_threshold:
            continue
        if i >= len(ends):
            break
        end_x, end_z = ends[i]
        h1 = wrap_pi(math.atan2(b[1] - a[1], b[3] - a[3])) if math.hypot(b[1] - a[1], b[3] - a[3]) >= 0.01 else None
        h2 = wrap_pi(math.atan2(end_x - a[1], end_z - a[3])) if math.hypot(end_x - a[1], end_z - a[3]) >= 0.01 else None
        moving_windows += 1
        if h1 is not None and h2 is not None:
            motion_deltas.append(wrap_pi(h2 - h1))
        if a[4] is not None:
            uw_end = yaw_uw_at(a[0] + window_ticks)
            if uw_end is not None:
                wrapped = wrap_pi(wrap_pi(uw_end) - a[4])
                yaw_deltas.append(wrapped)

    def deg(vals: list[float]) -> dict:
        return summarize([math.degrees(v) for v in vals])

    yaw_deg = [math.degrees(v) for v in yaw_deltas]
    return {
        "window_seconds": window_seconds,
        "speed_threshold_mps": speed_threshold,
        "moving_windows": moving_windows,
        "seam_crossings": wrap_crossings,
        "motion_heading_delta_rad": summarize(motion_deltas) if motion_deltas else {"n": 0},
        "motion_heading_delta_deg": deg(motion_deltas) if motion_deltas else {"n": 0},
        "packet_yaw_delta_deg": deg(yaw_deltas) if yaw_deltas else {"n": 0},
        "recommended": {
            # The live yaw-delta pilot compares per-window in-memory facing
            # change against the packet yaw delta; the target is the median
            # turn (usually ~0 deg over a short window with occasional
            # maneuvers) so tolerance comes from the observed spread.
            "yaw_delta_target_deg": round(statistics.median(yaw_deg), 4) if yaw_deg else 0.0,
            "yaw_delta_tolerance_deg": round(
                max(
                    (sum(1 for v in yaw_deg if abs(v) > 1.0) / len(yaw_deg))
                    * max((abs(v) for v in yaw_deg), default=0.0),
                    1.0,
                ),
                4,
            ) if yaw_deg else 1.0,
        },
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


def dealt_damage_series(
    con: sqlite3.Connection,
    session_id: str,
    attacker_entity_id: int,
    window_seconds: float,
) -> list[float]:
    """Per-window damage-dealt series from kind-3 damage events.

    The mirror of `hp_damage_series`: bucket the damage DEALT BY
    `attacker_entity_id` (the scoreboard damage-dealt counter's increments)
    into replay-time windows. A damage-dealt counter RISES by exactly these
    amounts when the target lands a hit and is otherwise flat — the same
    sparse-but-exact marker family as HP, in the increment direction.
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
        if int(v.get("attackerEntityId", -1)) != attacker_entity_id:
            continue
        ticks = int(r[0])
        idx = ticks // bucket_ticks
        buckets[idx] = buckets.get(idx, 0.0) + float(v.get("damage", 0))
    if not buckets:
        return []
    max_idx = max(buckets)
    return [buckets.get(i, 0.0) for i in range(max_idx + 1)]


def dealt_dump_schedule(
    con: sqlite3.Connection,
    session_id: str,
    attacker_entity_id: int,
    padding_seconds: float = 0.2,
) -> list[dict]:
    """Per-hit dump schedule for the damage-dealt (increment) live session.

    Same event-bound shape as `hp_dump_schedule`, keyed on the ATTACKER id:
    dump just BEFORE and AFTER each hit the target landed so the change
    window captures exactly that counter increment.
    """
    rows = con.execute(
        "SELECT replay_time_ticks, json_extract(values_json,'$.damage') AS dmg "
        "FROM canonical_events WHERE battle_session_id=? AND kind=3 AND "
        "json_extract(values_json,'$.attackerEntityId')=? ORDER BY replay_time_ticks",
        (session_id, attacker_entity_id),
    ).fetchall()
    schedule = []
    for row in rows:
        if row[1] is None or float(row[1]) <= 0.0:
            # A 0/unparseable-damage event cannot move the counter; a dump
            # pair around it would waste two lease-bounded dumps.
            continue
        t = float(row[0]) / TICKS_PER_SECOND
        damage = float(row[1])
        schedule.append(
            {
                "hit_replay_s": round(t, 2),
                "damage": round(damage, 2),
                "dump_before_s": round(t - padding_seconds, 2),
                "dump_after_s": round(t + padding_seconds, 2),
            }
        )
    return schedule


def yaw_dump_schedule(
    con: sqlite3.Connection,
    session_id: str,
    entity_id: int,
    turn_threshold_rad: float = 0.1,
    padding_seconds: float = 0.2,
    min_turn_span_ticks: int = 1,
) -> list[dict]:
    """Dump-pair schedule for the facing/yaw (L2) live session.

    Mirrors `hp_dump_schedule` for the yaw track: emit one dump pair per
    TURN SEGMENT whose cumulative packet-yaw change exceeds
    `turn_threshold_rad` (the L2 picker rule: > 0.1 rad = 2x the 0.05 rad
    match tolerance), bracketing the segment at +/- `padding_seconds` so the
    change window captures exactly that turn. The correlator's TURN windows
    (|expected| > the match tolerance) form the score denominator; the driver adds
    stationary CONTROL dump pairs (packet yaw exactly constant) for the
    flatness denominator.

    The yaw series is ~1 sample/s, so a turn usually spans several samples:
    consecutive samples whose wrapped yaw change stays >= the threshold are
    merged into ONE segment (one dump pair per segment, not per sample),
    which keeps the change window cleanly bounded to the actual rotation.
    """
    rows = con.execute(
        "SELECT replay_time_ticks, yaw FROM position_samples "
        "WHERE battle_session_id=? AND entity_id=? AND yaw IS NOT NULL "
        "ORDER BY replay_time_ticks",
        (session_id, entity_id),
    ).fetchall()
    if not rows:
        return []

    # Accumulate a turn segment while consecutive steps keep the SAME sign
    # (a slow turn spanning several ~1s samples is still one turn); close the
    # segment when the direction reverses or a gap occurs, and emit a dump
    # pair when the segment's cumulative |delta| reaches the threshold.
    schedule: list[dict] = []
    seg_start_ticks: int | None = None
    seg_start_yaw: float | None = None
    prev_ticks: int | None = None
    prev_yaw: float | None = None
    seg_sign: float = 0.0

    def close_segment(end_ticks: int, end_yaw: float) -> None:
        nonlocal seg_start_ticks, seg_start_yaw, seg_sign
        if seg_start_ticks is None or seg_start_yaw is None:
            return
        delta = wrap_pi(end_yaw - seg_start_yaw)
        if abs(delta) >= turn_threshold_rad and (
            end_ticks - seg_start_ticks >= min_turn_span_ticks
        ):
            start_s = seg_start_ticks / TICKS_PER_SECOND
            end_s = end_ticks / TICKS_PER_SECOND
            schedule.append(
                {
                    "turn_replay_s": round((start_s + end_s) / 2.0, 2),
                    "expected_delta_rad": round(delta, 4),
                    "expected_delta_deg": round(math.degrees(delta), 2),
                    "dump_before_s": round(start_s - padding_seconds, 2),
                    "dump_after_s": round(end_s + padding_seconds, 2),
                }
            )
        seg_start_ticks = None
        seg_start_yaw = None
        seg_sign = 0.0

    for row in rows:
        t = row["replay_time_ticks"]
        y = float(row["yaw"])
        if prev_ticks is None:
            prev_ticks, prev_yaw = t, y
            seg_start_ticks, seg_start_yaw = t, y
            continue
        step = wrap_pi(y - prev_yaw)
        step_sign = math.copysign(1.0, step) if abs(step) > 1e-6 else 0.0
        if step_sign != 0.0 and seg_sign != 0.0 and step_sign != seg_sign:
            # Direction reversal ends the turn segment.
            close_segment(prev_ticks, prev_yaw)
            seg_start_ticks, seg_start_yaw = t, y
        elif t - prev_ticks > 2 * TICKS_PER_SECOND:
            # A sample gap ends the segment (a turn across a gap is two
            # observations, not one continuous rotation).
            close_segment(prev_ticks, prev_yaw)
            seg_start_ticks, seg_start_yaw = t, y
        seg_sign = step_sign if step_sign != 0.0 else seg_sign
        prev_ticks, prev_yaw = t, y
    close_segment(prev_ticks, prev_yaw)

    # Post-process: the driver dumps at every distinct time and windows form
    # between consecutive dumps. Overlapping pairs (close adjacent turns)
    # would split into a RESIDUAL window whose |expected| sits in the dead
    # band between the control threshold and the 0.05 rad match tolerance —
    # the correlator classifies such windows as CONTROL (the field's delta
    # <= tolerance reads as "unchanged"), so they cannot contribute turn
    # evidence and must not dilute the score. So merge overlapping pairs into
    # one window
    # (min dump_before .. max dump_after) and recompute the expected delta
    # from the actual packet yaw at those endpoints; then drop any merged
    # window whose |expected| stays below the picker threshold (a wiggle, not
    # a usable turn).
    if not schedule:
        return schedule

    def yaw_nearest(ticks: int) -> float | None:
        """Nearest-sample packet yaw (the correlator's YawLookup semantics)."""
        if ticks <= sample_ticks[0] or ticks >= sample_ticks[-1]:
            return None
        lo, hi = 0, len(sample_ticks) - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if sample_ticks[mid] <= ticks:
                lo = mid
            else:
                hi = mid
        if abs(ticks - sample_ticks[lo]) <= abs(ticks - sample_ticks[hi]):
            return sample_yaws[lo]
        return sample_yaws[hi]

    sample_ticks = [r["replay_time_ticks"] for r in rows]
    sample_yaws = [float(r["yaw"]) for r in rows]
    merged: list[dict] = []
    for entry in schedule:
        before_s = entry["dump_before_s"]
        after_s = entry["dump_after_s"]
        if merged and before_s < merged[-1]["dump_after_s"]:
            merged[-1]["dump_after_s"] = max(merged[-1]["dump_after_s"], after_s)
        else:
            merged.append({"turn_replay_s": entry["turn_replay_s"],
                           "dump_before_s": before_s, "dump_after_s": after_s})

    out: list[dict] = []
    for entry in merged:
        start_ticks = int(entry["dump_before_s"] * TICKS_PER_SECOND)
        end_ticks = int(entry["dump_after_s"] * TICKS_PER_SECOND)
        start_yaw = yaw_nearest(start_ticks)
        end_yaw = yaw_nearest(end_ticks)
        if start_yaw is None or end_yaw is None:
            continue
        delta = wrap_pi(end_yaw - start_yaw)
        if abs(delta) < turn_threshold_rad:
            continue
        entry["expected_delta_rad"] = round(delta, 4)
        entry["expected_delta_deg"] = round(math.degrees(delta), 2)
        entry["turn_replay_s"] = round(
            (entry["dump_before_s"] + entry["dump_after_s"]) / 2.0, 2)
        out.append(entry)
    return out


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


def self_test(con: sqlite3.Connection) -> list[dict]:
    """Pin TICKS_PER_SECOND against the decoded DB (regression guard).

    The 2026-08-10 10x unit bug (TICKS_PER_SECOND was 10^6 while the DB
    stores .NET ticks at 10^7/s) silently scaled every seconds output and
    the hit-window bucketing. This check fails loudly if the constant ever
    drifts again: for the newest sessions, duration_ticks / TICKS_PER_SECOND
    must be a plausible WoTB battle length (120-900 s), the position sample
    span must land within the duration, and no event may exceed it.
    """
    checks: list[dict] = []
    sessions = con.execute(
        "SELECT id, map_name, duration_ticks FROM battle_sessions "
        "ORDER BY battle_time_utc DESC LIMIT 3"
    ).fetchall()
    for sid, map_name, duration_ticks in sessions:
        duration_s = duration_ticks / TICKS_PER_SECOND
        ok_duration = 120.0 <= duration_s <= 900.0
        max_pos = con.execute(
            "SELECT MAX(replay_time_ticks) FROM position_samples "
            "WHERE battle_session_id=?", (sid,)
        ).fetchone()[0]
        max_event = con.execute(
            "SELECT MAX(replay_time_ticks) FROM canonical_events "
            "WHERE battle_session_id=?", (sid,)
        ).fetchone()[0]
        pos_s = (max_pos or 0) / TICKS_PER_SECOND
        event_s = (max_event or 0) / TICKS_PER_SECOND
        ok_span = pos_s > 0 and abs(pos_s - duration_s) / duration_s < 0.25
        ok_events = event_s <= duration_s * 1.05 + 5.0
        checks.append(
            {
                "session_id": sid,
                "map_name": map_name,
                "duration_s": round(duration_s, 1),
                "ok_duration_120_900s": ok_duration,
                "position_max_s": round(pos_s, 1),
                "ok_position_span": ok_span,
                "event_max_s": round(event_s, 1),
                "ok_events_within_duration": ok_events,
                "pass": ok_duration and ok_span and ok_events,
            }
        )
    return checks


def hp_dump_schedule(
    con: sqlite3.Connection,
    session_id: str,
    victim_entity_id: int,
    padding_seconds: float = 0.2,
) -> list[dict]:
    """Per-hit dump schedule for the HP-diffing live session.

    Event-bound observation: for every damage event against the victim, emit a
    dump time just BEFORE and just AFTER the hit so the trusted reader's two
    dumps bracket exactly that event - the resulting change window captures
    the drop, and the correlator sums only that event's damage. Plus a note
    that flat control dumps belong in the gap segments (no damage). Times are
    real replay seconds (replay_time_ticks / 10^7).
    """
    rows = con.execute(
        "SELECT replay_time_ticks, json_extract(values_json,'$.damage') AS dmg "
        "FROM canonical_events WHERE battle_session_id=? AND kind=3 AND "
        "json_extract(values_json,'$.victimEntityId')=? ORDER BY replay_time_ticks",
        (session_id, victim_entity_id),
    ).fetchall()
    schedule = []
    for row in rows:
        t = float(row[0]) / TICKS_PER_SECOND
        damage = float(row[1]) if row[1] is not None else 0.0
        schedule.append(
            {
                "hit_replay_s": round(t, 2),
                "damage": round(damage, 2),
                "dump_before_s": round(t - padding_seconds, 2),
                "dump_after_s": round(t + padding_seconds, 2),
            }
        )
    return schedule


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
    parser.add_argument("--heading-delta", action="store_true",
                        help="movement-gated, wrap-aware per-window heading/turn series (motion heading + packet yaw deltas)")
    parser.add_argument("--yaw-dump", action="store_true",
                        help="emit the dump-pair schedule for the facing (L2) live session: one pair per turn segment with |packet yaw delta| >= threshold (0.1 rad default), plus the stationary control times")
    parser.add_argument("--turn-threshold", type=float, default=0.1,
                        help="radians threshold for a turn segment in --yaw-dump (default 0.1 = 2x the 0.05 rad match tolerance)")
    parser.add_argument("--speed-threshold", type=float, default=0.5,
                        help="m/s threshold for the moving phase (default 0.5)")
    parser.add_argument("--hp-delta", action="store_true",
                        help="build a per-window HP damage-delta series from kind-3 damage events")
    parser.add_argument("--victim-entity", type=int, default=0,
                        help="entity id of the HP victim to track (required with --hp-delta)")
    parser.add_argument("--damage-dealt", action="store_true",
                        help="build the scoreboard damage-dealt (increment) series from the target's attacker-side kind-3 events")
    parser.add_argument("--attacker-entity", type=int, default=0,
                        help="entity id whose DEALT damage to track (default: the session's viewpoint/player entity)")
    parser.add_argument("--top-victims", type=int, default=0,
                        help="rank the session's damage victims by hit count (N entries) for HP-diffing victim selection; prints and exits")
    parser.add_argument("--self-test", action="store_true",
                        help="pin TICKS_PER_SECOND against the decoded DB (battle-length sanity + position/event spans); exits non-zero on drift")
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
                "ticks_1e7": dt_ticks,
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

    if args.heading_delta:
        # The facing series needs packet yaw, which the default pick may not
        # have (older duplicate decodes predate migration 5).
        session = pick_yaw_session(con)
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
        yaw_samples = participant_samples(con, session["id"], participant_id, with_yaw=True)
        heading = heading_delta_series(yaw_samples, args.window, args.speed, args.speed_threshold)
        result["session_id"] = session["id"]
        result["map_name"] = session["map_name"]
        result["participant_id"] = participant_id
        result["heading_delta"] = heading
        result["heading_delta"] = heading
        if "commands" not in result:
            result["commands"] = {}
        result["commands"]["facing_yaw_delta_pilot"] = build_command(
            session["id"],
            "Float",
            heading["recommended"]["yaw_delta_target_deg"],
            heading["recommended"]["yaw_delta_tolerance_deg"],
            args.window,
        )

    if args.yaw_dump:
        # The facing dump schedule needs packet yaw (migration 5+).
        session = pick_yaw_session(con)
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
        entity_row = con.execute(
            "SELECT entity_id FROM position_samples WHERE battle_session_id=? "
            "AND participant_id=? AND entity_id IS NOT NULL LIMIT 1",
            (session["id"], participant_id),
        ).fetchone()
        target_entity = entity_row["entity_id"] if entity_row else participant_id
        schedule = yaw_dump_schedule(
            con,
            session["id"],
            target_entity,
            turn_threshold_rad=args.turn_threshold,
        )
        result["session_id"] = session["id"]
        result["map_name"] = session["map_name"]
        result["participant_id"] = participant_id
        result["entity_id"] = target_entity
        result["yaw_dump"] = {
            "turn_threshold_rad": args.turn_threshold,
            "turn_segments": len(schedule),
            "schedule": schedule,
        }
        if "commands" not in result:
            result["commands"] = {}
        result["commands"]["yaw_diff"] = (
            "wotbtreader-cli yaw-diff <snapshots.json> "
            f"--session {session['id']} --victim {target_entity}"
        )

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
            "dump_schedule": hp_dump_schedule(con, session["id"], victim),
            "commands": {
                "hp_diff": (
                    f"wotbtreader-cli hp-diff <snapshots.json> "
                    f"--session {session['id']} --victim {victim} "
                    f"--mode lenient"
                ),
            },
        }

    if args.damage_dealt:
        if args.attacker_entity <= 0:
            # Default to the viewpoint (player's own) entity: the scoreboard
            # damage-dealt counter is the PLAYER's stat, so the target is the
            # entity the viewpoint tank controls.
            vpid = session.get("viewpoint_participant_id")
            row = (
                con.execute(
                    "SELECT entity_id FROM participants "
                    "WHERE battle_session_id=? AND id=?",
                    (session["id"], vpid),
                ).fetchone()
                if vpid
                else None
            )
            attacker = row["entity_id"] if row else 0
        else:
            attacker = args.attacker_entity
        dealt_series = dealt_damage_series(con, session["id"], attacker, args.window)
        result["damage_dealt"] = {
            "attacker_entity_id": attacker,
            "windows": len(dealt_series),
            "hit_windows": sum(1 for d in dealt_series if d > 0),
            "total_damage": round(sum(dealt_series), 2),
            "series": [round(d, 2) for d in dealt_series],
            "simulation": simulate_survival(
                dealt_series, 0.0, [0.0, 0.5, 1.0, 5.0], rounds
            ),
            "dump_schedule": dealt_dump_schedule(con, session["id"], attacker),
            "commands": {
                "hp_diff": (
                    f"wotbtreader-cli hp-diff <snapshots.json> "
                    f"--session {session['id']} --victim {attacker} "
                    f"--mode lenient --direction increment"
                ),
            },
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

    if args.self_test:
        checks = self_test(con)
        result = {
            "scan": "replay-delta-extractor",
            "mode": "self-test",
            "ticks_per_second": TICKS_PER_SECOND,
            "checks": checks,
            "pass": all(check["pass"] for check in checks),
        }
        print(json.dumps(result, indent=2))
        return 0 if result["pass"] else 2

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
