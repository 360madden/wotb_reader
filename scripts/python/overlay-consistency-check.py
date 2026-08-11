#!/usr/bin/env python3
"""Headless consistency check for the replay overlay pipeline.

Walks every session given on the command line at a coarse time step against a
running web host and validates the overlay frame contract on real data:

  * every frame resolves (HTTP 200, JSON parses),
  * camera pose fields are finite when present,
  * every tank carries finite world X/Z; screen X/Y finite when in-viewport,
    hpFraction in [0,1], alive boolean, world position inside the map boundary
    once normalized (the minimap's exact math),
  * destroyed tanks stay dead for the rest of the battle,
  * kills arrive in ascending time order with distinct victims,
  * every pip has a finite screen position.

Usage:
  python scripts/python/overlay-consistency-check.py \
      --host http://127.0.0.1:9182 \
      --db .data/treader.db \
      --sessions <session-guid> [<session-guid> ...]

Exit code 0 when every session passes, 1 otherwise. This script is read-only
against the web host and the database.
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import urllib.request
from typing import Any

STEP_SECONDS = 1.0
MAX_TIME_SECONDS = 320.0


def fetch_frame(host: str, session_id: str, t: float) -> dict[str, Any]:
    url = f"{host}/api/v1/sessions/{session_id}/frame?timeSeconds={t}&width=1920&height=1080"
    request = urllib.request.Request(url)
    request.add_header("X-Capability-Token", "dev")
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def finite(value: Any) -> bool:
    return isinstance(value, (int, float)) and value == value and value not in (float("inf"), float("-inf"))


def discover_sessions(db_path: str) -> list[tuple[str, str, str | None]]:
    """Returns (session_id, map_name, map_id) for every session, newest first."""
    connection = sqlite3.connect(db_path)
    try:
        rows = connection.execute(
            "SELECT id, map_name, map_id FROM battle_sessions ORDER BY battle_time_utc DESC"
        ).fetchall()
    finally:
        connection.close()
    return [(row[0], row[1] or "unknown", row[2]) for row in rows]


def normalize_extents(boundaries: list[dict[str, Any]]) -> dict[str, dict[str, float]]:
    by_map: dict[str, dict[str, float]] = {}
    for boundary in boundaries:
        by_map[boundary["mapId"]] = {
            "minX": float(boundary["minX"]),
            "maxX": float(boundary["maxX"]),
            "minZ": float(boundary["minZ"]),
            "maxZ": float(boundary["maxZ"]),
        }
    return by_map


def check_session(
    host: str,
    session_id: str,
    map_name: str,
    map_id: str | None,
    extents: dict[str, dict[str, float]],
) -> list[str]:
    errors: list[str] = []
    # The frame's kills array is an append-only battle log repeated on every
    # frame: once a kill lands, every later frame carries it at the same index.
    expected_kills: list[tuple[int, float]] = []
    dead_entities: set[int] = set()

    max_roster = 0
    t = 0.0
    while t <= MAX_TIME_SECONDS:
        try:
            frame = fetch_frame(host, session_id, t)
        except Exception as exception:  # noqa: BLE001 - report and fail
            errors.append(f"t={t:.0f}: frame failed: {exception}")
            break

        max_roster = max(max_roster, len(frame.get("tanks", [])))
        # Scoreboard invariants: damage dealt is cumulative and non-negative;
        # the kills column sums to the kill-feed size (same attribution).
        for tank in frame.get("tanks", []):
            damage = tank.get("damageDealt")
            if not isinstance(damage, int) or damage < 0:
                errors.append(f"t={t:.0f}: tank {tank.get('entityId')} damageDealt invalid: {damage!r}")
            taken = tank.get("damageTaken")
            if not isinstance(taken, int) or taken < 0:
                errors.append(f"t={t:.0f}: tank {tank.get('entityId')} damageTaken invalid: {taken!r}")
            if not isinstance(tank.get("kills"), int) or tank.get("kills") < 0:
                errors.append(f"t={t:.0f}: tank {tank.get('entityId')} kills invalid: {tank.get('kills')!r}")
        scored = sum(t.get("kills") or 0 for t in frame.get("tanks", []))
        if scored != len(frame.get("kills", [])):
            errors.append(f"t={t:.0f}: kills scored ({scored}) != kill feed size ({len(frame.get('kills', []))})")

        for key in ("cameraX", "cameraY", "cameraZ", "cameraYawRadians", "cameraPitchRadians"):
            value = frame.get(key)
            if value is not None and not finite(value):
                errors.append(f"t={t:.0f}: camera field {key} not finite: {value!r}")

        seen_entities: set[int] = set()
        for tank in frame.get("tanks", []):
            entity_id = tank.get("entityId")
            if entity_id is None:
                errors.append(f"t={t:.0f}: tank without entityId")
                continue
            seen_entities.add(entity_id)

            if not finite(tank.get("worldX")) or not finite(tank.get("worldZ")):
                errors.append(f"t={t:.0f}: tank {entity_id} world X/Z not finite")
            if tank.get("inViewport"):
                if not finite(tank.get("screenX")) or not finite(tank.get("screenY")):
                    errors.append(f"t={t:.0f}: tank {entity_id} in viewport but screen not finite")
            if not (0.0 <= float(tank.get("hpFraction", -1.0)) <= 1.0):
                errors.append(f"t={t:.0f}: tank {entity_id} hpFraction out of range: {tank.get('hpFraction')!r}")
            if not isinstance(tank.get("alive"), bool):
                errors.append(f"t={t:.0f}: tank {entity_id} alive not boolean")

            if not tank.get("alive"):
                if entity_id not in dead_entities:
                    dead_entities.add(entity_id)
            elif entity_id in dead_entities:
                errors.append(f"t={t:.0f}: tank {entity_id} came back alive after death")

            # Minimap math: normalize against the map boundary and require 0..1.
            extent = extents.get(map_id or "") if map_id else None
            if extent is not None:
                span_x = extent["maxX"] - extent["minX"]
                span_z = extent["maxZ"] - extent["minZ"]
                if span_x > 0 and span_z > 0:
                    nx = (float(tank["worldX"]) - extent["minX"]) / span_x
                    nz = (float(tank["worldZ"]) - extent["minZ"]) / span_z
                    if not (-0.02 <= nx <= 1.02 and -0.02 <= nz <= 1.02):
                        errors.append(
                            f"t={t:.0f}: tank {entity_id} normalized ({nx:.2f},{nz:.2f}) outside boundary")

        for pip in frame.get("pips", []):
            if not finite(pip.get("screenX")) or not finite(pip.get("screenY")):
                errors.append(f"t={t:.0f}: pip without finite screen position")

        kills_this_frame = frame.get("kills", [])
        for index, kill in enumerate(kills_this_frame):
            kill_time = float(kill.get("replayTimeSeconds", -1.0))
            victim = int(kill.get("victimEntityId", -1))
            if victim < 0:
                errors.append(f"t={t:.0f}: kill without victim entity id")
                continue
            if index < len(expected_kills):
                # A repeated kill must match the log entry exactly.
                expected_victim, expected_time = expected_kills[index]
                if victim != expected_victim or abs(kill_time - expected_time) > 0.01:
                    errors.append(
                        f"t={t:.0f}: kill[{index}] changed: expected victim "
                        f"{expected_victim} at {expected_time:.2f}s, got {victim} at {kill_time:.2f}s")
            else:
                # A new kill appends in time order with a fresh victim.
                if expected_kills and kill_time < expected_kills[-1][1]:
                    errors.append(
                        f"t={t:.0f}: new kill time {kill_time:.2f}s out of order after "
                        f"{expected_kills[-1][1]:.2f}s")
                if any(victim == existing_victim for existing_victim, _ in expected_kills):
                    errors.append(f"t={t:.0f}: victim {victim} killed twice")
                expected_kills.append((victim, kill_time))

        t += STEP_SECONDS

    if max_roster == 0:
        errors.append("no tanks in any frame")
    elif len(expected_kills) == 0:
        errors.append("no kills observed in a full battle")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="web host base URL, e.g. http://127.0.0.1:9182")
    parser.add_argument("--db", required=True, help="path to the treader sqlite database")
    parser.add_argument("--sessions", nargs="*", help="session guids; defaults to every session in the db")
    args = parser.parse_args()

    host = args.host.rstrip("/")
    all_sessions = discover_sessions(args.db)
    by_id = {sid: (name, mid) for sid, name, mid in all_sessions}
    sessions = args.sessions or [sid for sid, _, _ in all_sessions]

    boundary_request = urllib.request.Request(f"{host}/api/v1/maps/boundaries")
    boundary_request.add_header("X-Capability-Token", "dev")
    with urllib.request.urlopen(boundary_request, timeout=30) as response:
        boundaries = normalize_extents(json.load(response))

    failures = 0
    for session_id in sessions:
        map_name, map_id = by_id.get(session_id, ("unknown", None))
        print(f"== {session_id} ({map_name}) ==")
        errors = check_session(host, session_id, map_name, map_id, boundaries)
        if errors:
            failures += 1
            for error in errors[:12]:
                print(f"  FAIL {error}")
            print(f"  ({len(errors)} errors total)")
        else:
            print("  PASS")

    if failures:
        print(f"\n{len(sessions) - failures}/{len(sessions)} sessions passed.")
        return 1
    print(f"\nAll {len(sessions)} session(s) passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
