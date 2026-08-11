#!/usr/bin/env python3
"""Batch rehearsal cross-check (stdlib only).

Two modes for the X2 batch N-entity rehearsal
(docs/operations/batch-entity-read-design.md):

  --roster   print the decoded roster (participant entity ids in team order)
             + session duration, as JSON on stdout.
  --compare  read a batch dumps file (schema
             wotbtreader.od.batch-rehearsal.dumps.v1, written by
             scripts/invoke-batch-rehearsal.ps1), decode each ring-record
             dump's position float32 triple at +0x10, and compare it against
             the decoded position sample nearest the batch's replay-clock
             label. Prints the verdict table; exit 0 = all compared pairs
             match within the tolerance, 1 = at least one miss, 2 = nothing
             comparable (no verdict).

The published position chain reads the float32 triple at ring-record
[record + 0x10] (PositionRecordOffset 0x10, ring stride 0x38), so a dump
anchored at 'ring-record' carries the entity's world position at the dump's
replay-clock label — the same coordinate space as the decoded
position_samples. Alignment proven here transfers to the live mode as-is.
"""

from __future__ import annotations

import argparse
import base64
import json
import math
import sqlite3
import struct
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DB = REPO_ROOT / ".data" / "treader.db"

DUMP_SCHEMA = "wotbtreader.od.batch-rehearsal.dumps.v1"
POSITION_OFFSET = 0x10  # float32 x/y/z triple on the ring record
TICKS_PER_SECOND = 10_000_000  # .NET TimeSpan ticks; see record-diffing-groundwork


def _connect(db: str, session_id: str) -> sqlite3.Connection:
    connection = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    row = connection.execute(
        "SELECT id, duration_ticks FROM battle_sessions WHERE id = ?",
        (session_id,),
    ).fetchone()
    if row is None:
        raise SystemExit(f"error: no battle session {session_id} in {db}")
    return connection


def roster(connection: sqlite3.Connection, session_id: str) -> dict:
    rows = connection.execute(
        """
        SELECT entity_id FROM participants
        WHERE battle_session_id = ? AND entity_id IS NOT NULL
        ORDER BY team_number, entity_id
        """,
        (session_id,),
    ).fetchall()
    entity_ids = [int(row["entity_id"]) for row in rows]
    duration_ticks = connection.execute(
        "SELECT duration_ticks FROM battle_sessions WHERE id = ?",
        (session_id,),
    ).fetchone()["duration_ticks"]
    return {
        "entityIds": entity_ids,
        "durationSeconds": round((duration_ticks or 0) / TICKS_PER_SECOND, 3),
    }


def _nearest_sample(
    connection: sqlite3.Connection, session_id: str, entity_id: int, replay_time_s: float
) -> tuple[float, float, float] | None:
    target_ticks = round(replay_time_s * TICKS_PER_SECOND)
    row = connection.execute(
        """
        SELECT raw_x, raw_y, raw_z FROM position_samples
        WHERE battle_session_id = ? AND entity_id = ?
        ORDER BY ABS(replay_time_ticks - ?) LIMIT 1
        """,
        (session_id, entity_id, target_ticks),
    ).fetchone()
    if row is None:
        return None
    return (float(row["raw_x"]), float(row["raw_y"]), float(row["raw_z"]))


def _position_from_dump(base64_payload: str) -> tuple[float, float, float] | None:
    try:
        payload = base64.b64decode(base64_payload, validate=True)
    except (ValueError, TypeError):
        return None
    if len(payload) < POSITION_OFFSET + 12:
        return None
    return struct.unpack_from("<fff", payload, POSITION_OFFSET)


def compare(connection: sqlite3.Connection, dumps_path: str, tolerance: float) -> int:
    with open(dumps_path, "r", encoding="utf-8") as handle:
        dumps = json.load(handle)
    if dumps.get("schema") != DUMP_SCHEMA:
        raise SystemExit(f"error: {dumps_path} is not a {DUMP_SCHEMA} file")
    session_id = dumps.get("sessionId")
    if not session_id:
        raise SystemExit("error: dumps file has no sessionId")

    compared = 0
    matched = 0
    skipped = 0
    misses: list[str] = []
    print(f"batch-rehearsal: session {session_id} "
          f"(anchor {dumps.get('regionAnchor')}, tolerance {tolerance:g} m)")
    for time_entry in dumps.get("times", []):
        label = time_entry.get("replayTimeSeconds")
        if label is None:
            skipped += 1
            print("  (time entry without a replay-time label skipped)")
            continue
        print(f"  t={label:7.1f}s sameDecodedClockProven="
              f"{bool(time_entry.get('sameDecodedClockProven'))}")
        for entity in time_entry.get("entities", []):
            entity_id = entity.get("entityId")
            status = entity.get("status")
            if status != "Resolved":
                skipped += 1
                print(f"    entity {entity_id}: not Resolved ({status}) - skipped")
                continue
            memory = _position_from_dump(entity.get("regionBase64") or "")
            if memory is None:
                skipped += 1
                misses.append(f"t={label:g}s entity {entity_id}: unreadable dump")
                print(f"    entity {entity_id}: dump too short/unreadable - FAIL")
                continue
            decoded = _nearest_sample(connection, session_id, entity_id, label)
            if decoded is None:
                skipped += 1
                print(f"    entity {entity_id}: no decoded sample near t={label:g}s - skipped")
                continue
            delta = math.dist(memory, decoded)
            compared += 1
            ok = delta <= tolerance
            matched += 1 if ok else 0
            if not ok:
                misses.append(
                    f"t={label:g}s entity {entity_id}: delta {delta:.2f} m > {tolerance:g} m")
            print(
                f"    entity {entity_id}: mem ({memory[0]:8.2f}, {memory[1]:8.2f}, "
                f"{memory[2]:8.2f}) decoded ({decoded[0]:8.2f}, {decoded[1]:8.2f}, "
                f"{decoded[2]:8.2f}) delta {delta:6.2f} m {'OK' if ok else 'MISS'}")

    print(f"batch-rehearsal: matched {matched}/{compared} compared pairs "
          f"({skipped} skipped, no verdict)")
    if compared == 0:
        print("batch-rehearsal: NO-VERDICT - nothing comparable")
        return 2
    if matched != compared:
        for miss in misses:
            print(f"batch-rehearsal: MISS {miss}")
        return 1
    print("batch-rehearsal: PASS - every compared position matches the decoded replay")
    return 0


def self_test() -> int:
    """Synthetic fixture test of the pure decode + tolerance logic (no DB)."""
    payload = bytearray(0x38)
    struct.pack_into("<fff", payload, POSITION_OFFSET, 1.0, 2.0, 3.0)
    encoded = base64.b64encode(bytes(payload)).decode("ascii")
    assert _position_from_dump(encoded) == (1.0, 2.0, 3.0)
    # A short dump fails closed (never a guessed position).
    assert _position_from_dump(base64.b64encode(b"1234").decode("ascii")) is None
    # Tolerance math sanity.
    assert math.dist((0.0, 0.0, 0.0), (3.0, 4.0, 0.0)) == 5.0
    print("batch-rehearsal-crosscheck: self-test PASS")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Batch rehearsal cross-check")
    parser.add_argument("--db", default=str(DEFAULT_DB), help="treader sqlite db path")
    parser.add_argument("--session", default="", help="decoded battle session id")
    parser.add_argument("--roster", action="store_true",
                        help="print the decoded roster + duration as JSON")
    parser.add_argument("--dumps", default="", help="batch dumps file (compare mode)")
    parser.add_argument("--tolerance", type=float, default=2.0,
                        help="position match tolerance in meters (default 2.0)")
    parser.add_argument("--self-test", action="store_true",
                        help="run the synthetic self-test and exit")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.roster and args.dumps:
        raise SystemExit("error: --roster and --dumps are mutually exclusive")
    if not args.session:
        raise SystemExit("error: --session is required (or pass --self-test)")

    connection = _connect(args.db, args.session)
    if args.roster:
        print(json.dumps(roster(connection, args.session)))
        return 0
    if not args.dumps:
        raise SystemExit("error: pass --roster or --dumps <file>")
    if args.tolerance <= 0:
        raise SystemExit("error: --tolerance must be positive")
    return compare(connection, args.dumps, args.tolerance)


if __name__ == "__main__":
    sys.exit(main())
