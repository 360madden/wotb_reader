#!/usr/bin/env python3
"""Batch rehearsal cross-check (stdlib only).

Three modes for the X2/X3 batch N-entity rehearsal
(docs/operations/batch-entity-read-design.md,
docs/operations/live-roster-read-design.md):

  --roster      print the decoded roster (participant entity ids in team
                order) + session duration, as JSON on stdout.
  --compare     read a batch dumps file (schema
                wotbtreader.od.batch-rehearsal.dumps.v1, written by
                scripts/invoke-batch-rehearsal.ps1), decode each ring-record
                dump's position float32 triple at +0x10, and compare it
                against the decoded position sample nearest the batch's
                replay-clock label. Prints the verdict table; exit 0 = all
                compared pairs match within the tolerance, 1 = at least one
                miss, 2 = nothing comparable (no verdict).
  --enumeration read a live roster-enumeration file (schema
                wotbtreader.od.batch-rehearsal.roster-enum.v1, written by
                scripts/invoke-batch-rehearsal.ps1 -EnumerateLive) and
                compare the enumerated avatar-family ids against the decoded
                participants roster: matched / missing (decoded but not
                enumerated) / extra (enumerated but not decoded) + filter
                precision. Exit 0 = EXACT SET MATCH (no missing, no extra),
                1 = any mismatch, 2 = nothing comparable.

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
ENUM_SCHEMA = "wotbtreader.od.batch-rehearsal.roster-enum.v1"
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
            decoded = _nearest_sample(connection, session_id, entity_id, label)
            if decoded is None:
                skipped += 1
                print(f"    entity {entity_id}: no decoded sample near t={label:g}s - skipped")
                continue
            # A Resolved entity WITH decoded ground truth always counts
            # against the verdict: an unreadable dump (truncated/short
            # region) is an automatic MISS, never a silent skip.
            compared += 1
            memory = _position_from_dump(entity.get("regionBase64") or "")
            if memory is None:
                misses.append(f"t={label:g}s entity {entity_id}: unreadable dump")
                print(f"    entity {entity_id}: dump too short/unreadable - FAIL")
                continue
            delta = math.dist(memory, decoded)
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


def enumeration_compare(connection: sqlite3.Connection, enum_path: str) -> int:
    """Compare a live roster enumeration against the decoded participants.

    The enumeration (schema ENUM_SCHEMA, written by
    invoke-batch-rehearsal.ps1 -EnumerateLive) carries the avatar-family ids
    enumerated from the game's own BWEntities maps plus the movement-filter
    precision counters. This measures the X3 filter precision: an EXACT SET
    MATCH (every decoded participant enumerated, nothing extra) is the
    strongest possible agreement; missing ids mean the enumeration missed
    participants; extra ids mean the movement-filter vtable gate admitted
    non-avatar entities. Fail-closed: an enumeration that claims
    TraversalLimitExceeded or a non-Resolved status is never compared.
    """
    with open(enum_path, "r", encoding="utf-8") as handle:
        enum = json.load(handle)
    if enum.get("schema") != ENUM_SCHEMA:
        raise SystemExit(f"error: {enum_path} is not a {ENUM_SCHEMA} file")
    session_id = enum.get("sessionId")
    if not session_id:
        raise SystemExit("error: enumeration file has no sessionId")
    if enum.get("status") != "Resolved":
        print(f"batch-rehearsal: enumeration status '{enum.get('status')}' "
              f"is not Resolved - fail-closed, no comparison")
        return 1
    if enum.get("traversalLimited"):
        print("batch-rehearsal: enumeration TraversalLimited - fail-closed, "
              "a partial roster is never compared")
        return 1

    decoded = set(roster(connection, session_id)["entityIds"])
    enumerated = set(int(entry) for entry in enum.get("entityIds", []))
    matched = sorted(decoded & enumerated)
    missing = sorted(decoded - enumerated)
    extra = sorted(enumerated - decoded)
    precision = (
        len(matched) / len(enumerated) if enumerated else 0.0
    )
    recall = len(matched) / len(decoded) if decoded else 0.0

    print(f"batch-rehearsal: enumeration {session_id}")
    print(f"  decoded roster: {len(decoded)} entities, "
          f"enumerated: {len(enumerated)} (candidatesSeen "
          f"{enum.get('candidatesSeen')}, filteredOut {enum.get('filteredOut')})")
    print(f"  matched {len(matched)}, missing {len(missing)}, extra {len(extra)}")
    print(f"  filter precision {precision:.3f}, recall {recall:.3f}")
    for entity_id in missing:
        print(f"  MISSING decoded participant {entity_id}")
    for entity_id in extra:
        print(f"  EXTRA enumerated id {entity_id}")

    if not decoded and not enumerated:
        print("batch-rehearsal: NO-VERDICT - nothing comparable")
        return 2
    if not missing and not extra:
        print("batch-rehearsal: PASS - enumeration matches the decoded roster")
        return 0
    print("batch-rehearsal: FAIL - enumeration does not match the decoded roster")
    return 1


def _self_test_compare() -> int:
    """Verdict-level check on an in-memory DB (no repo files touched)."""
    import tempfile

    connection = sqlite3.connect(":memory:")
    connection.row_factory = sqlite3.Row
    connection.executescript(
        """
        CREATE TABLE battle_sessions (id TEXT PRIMARY KEY, duration_ticks INTEGER);
        CREATE TABLE position_samples (
            id TEXT PRIMARY KEY, battle_session_id TEXT NOT NULL,
            entity_id INTEGER, replay_time_ticks INTEGER NOT NULL,
            raw_x REAL NOT NULL, raw_y REAL NOT NULL, raw_z REAL NOT NULL);
        INSERT INTO battle_sessions (id, duration_ticks) VALUES ('s', 100000000);
        INSERT INTO position_samples VALUES
            ('a', 's', 1, 50000000, 10.0, 20.0, 30.0),
            ('b', 's', 2, 50000000, 40.0, 50.0, 60.0);
        """
    )

    def payload(x: float, y: float, z: float) -> str:
        blob = bytearray(64)
        struct.pack_into("<fff", blob, POSITION_OFFSET, x, y, z)
        return base64.b64encode(bytes(blob)).decode("ascii")

    def dumps_file(entities: list) -> str:
        path = Path(tempfile.gettempdir()) / "batch-rehearsal-self-test.json"
        path.write_text(
            json.dumps(
                {
                    "schema": DUMP_SCHEMA,
                    "sessionId": "s",
                    "regionAnchor": "ring-record",
                    "regionLength": 64,
                    "times": [
                        {
                            "replayTimeSeconds": 5.0,
                            "sameDecodedClockProven": True,
                            "entities": entities,
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        return str(path)

    # Matching dumps -> PASS.
    clean = dumps_file(
        [
            {"entityId": 1, "status": "Resolved", "regionBase64": payload(10.0, 20.0, 30.0)},
            {"entityId": 2, "status": "Resolved", "regionBase64": payload(40.0, 50.0, 60.0)},
        ]
    )
    assert compare(connection, clean, 2.0) == 0
    # A Resolved entity with ground truth but a truncated dump must FAIL the
    # verdict (regression-pins the unreadable-dump bug class).
    truncated = dumps_file(
        [
            {"entityId": 1, "status": "Resolved", "regionBase64": base64.b64encode(b"1234").decode("ascii")},
        ]
    )
    assert compare(connection, truncated, 2.0) == 1
    return 0


def _self_test_enumeration() -> int:
    """Verdict-level check of the X3 enumeration comparison (in-memory DB)."""
    import tempfile

    connection = sqlite3.connect(":memory:")
    connection.row_factory = sqlite3.Row
    connection.executescript(
        """
        CREATE TABLE battle_sessions (id TEXT PRIMARY KEY, duration_ticks INTEGER);
        CREATE TABLE participants (
            id TEXT PRIMARY KEY, battle_session_id TEXT NOT NULL,
            entity_id INTEGER, team_number INTEGER);
        INSERT INTO battle_sessions (id, duration_ticks) VALUES ('s', 100000000);
        INSERT INTO participants VALUES
            ('p1', 's', 101, 1), ('p2', 's', 102, 1), ('p3', 's', 103, 2);
        """
    )

    def enum_file(entity_ids: list, status: str = "Resolved",
                  traversal_limited: bool = False, candidates_seen: int = 3,
                  filtered_out: int = 0) -> str:
        path = Path(tempfile.gettempdir()) / "batch-rehearsal-enum-self-test.json"
        path.write_text(
            json.dumps(
                {
                    "schema": ENUM_SCHEMA,
                    "sessionId": "s",
                    "status": status,
                    "candidatesSeen": candidates_seen,
                    "filteredOut": filtered_out,
                    "moduleRooted": True,
                    "traversalLimited": traversal_limited,
                    "entityIds": entity_ids,
                }
            ),
            encoding="utf-8",
        )
        return str(path)

    # Exact set match -> PASS.
    assert enumeration_compare(
        connection, enum_file([101, 102, 103], candidates_seen=5, filtered_out=2)
    ) == 0
    # Missing a decoded participant -> FAIL.
    assert enumeration_compare(connection, enum_file([101, 102])) == 1
    # Extra enumerated id -> FAIL.
    assert enumeration_compare(connection, enum_file([101, 102, 103, 999])) == 1
    # Fail-closed: TraversalLimited is never compared.
    assert enumeration_compare(
        connection, enum_file([101, 102, 103], traversal_limited=True)
    ) == 1
    return 0


def self_test() -> int:
    """Synthetic fixture tests of the decode + tolerance + verdict logic."""
    payload = bytearray(0x38)
    struct.pack_into("<fff", payload, POSITION_OFFSET, 1.0, 2.0, 3.0)
    encoded = base64.b64encode(bytes(payload)).decode("ascii")
    assert _position_from_dump(encoded) == (1.0, 2.0, 3.0)
    # A short dump fails closed (never a guessed position).
    assert _position_from_dump(base64.b64encode(b"1234").decode("ascii")) is None
    # Tolerance math sanity.
    assert math.dist((0.0, 0.0, 0.0), (3.0, 4.0, 0.0)) == 5.0
    assert _self_test_compare() == 0
    assert _self_test_enumeration() == 0
    print("batch-rehearsal-crosscheck: self-test PASS")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Batch rehearsal cross-check")
    parser.add_argument("--db", default=str(DEFAULT_DB), help="treader sqlite db path")
    parser.add_argument("--session", default="", help="decoded battle session id")
    parser.add_argument("--roster", action="store_true",
                        help="print the decoded roster + duration as JSON")
    parser.add_argument("--dumps", default="", help="batch dumps file (compare mode)")
    parser.add_argument("--enumeration", default="",
                        help="live roster-enumeration file (X3 compare mode)")
    parser.add_argument("--tolerance", type=float, default=2.0,
                        help="position match tolerance in meters (default 2.0)")
    parser.add_argument("--self-test", action="store_true",
                        help="run the synthetic self-test and exit")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    modes = sum(bool(flag) for flag in (args.roster, args.dumps, args.enumeration))
    if modes > 1:
        raise SystemExit("error: --roster, --dumps, and --enumeration are "
                         "mutually exclusive")
    if not args.session:
        raise SystemExit("error: --session is required (or pass --self-test)")

    connection = _connect(args.db, args.session)
    if args.roster:
        print(json.dumps(roster(connection, args.session)))
        return 0
    if args.enumeration:
        return enumeration_compare(connection, args.enumeration)
    if not args.dumps:
        raise SystemExit("error: pass --roster, --dumps, or --enumeration")
    if args.tolerance <= 0:
        raise SystemExit("error: --tolerance must be positive")
    return compare(connection, args.dumps, args.tolerance)


if __name__ == "__main__":
    sys.exit(main())
