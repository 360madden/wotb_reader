#!/usr/bin/env python3
"""verify-hp-ledger.py — read-only HP-ledger invariant check on decoded replays.

Validates the decoded canonical HP data (type-5 max-health broadcasts,
subtype-1 health-change ledger, Destroyed events) for one or more battle
sessions directly against the treader SQLite store. This is the durable,
re-runnable form of the 2026-08-11 validation:

  * HP conservation — for every entity with a MaxHealthObserved event,
    current = maxHealth - damageTaken is never negative (destroyed tanks
    land at exactly 0 because the destroy marker credits the remaining HP;
    survivors keep positive HP).
  * Ledger balance — total damage dealt (attacker side) equals total damage
    taken (victim side) within the session.
  * Alive alignment — every tank whose ledger reached max health has a
    Destroyed event and vice versa (the subtype-1 0xFFFD destroy marker and
    the position destroy marker share one dedupe set; both replays verified
    with 0 mismatches).
  * battle_results cross-check — for every participant WITH battle stats,
    decoded per-attacker damage equals battle_results damage_dealt exactly
    (players who left the battle have NULL stats and are skipped, but their
    decoded damage is still true and HP-conserving).

Usage:
  python scripts/python/verify-hp-ledger.py --db <path> [--session <guid> ...]
  python scripts/python/verify-hp-ledger.py --db <path> --latest 2

With no --session, every battle session is checked. --latest N checks the N
most recently decoded sessions. Exit code 0 when every checked session
passes, 1 otherwise. Read-only against the database.
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from typing import Any

# CanonicalEventKind numeric values (TelemetryModels.cs): 3 = Damage,
# 4 = Destroyed, 7 = MaxHealthObserved.
KIND_DAMAGE = 3
KIND_DESTROYED = 4
KIND_MAX_HEALTH = 7


def verify_session(cur: sqlite3.Cursor, session_id: str, map_name: str | None) -> list[str]:
    errors: list[str] = []

    max_health: dict[int, int] = {}
    for entity_id, values_json in cur.execute(
        "SELECT entity_id, values_json FROM canonical_events "
        "WHERE battle_session_id = ? AND kind = ?",
        (session_id, KIND_MAX_HEALTH),
    ):
        value = json.loads(values_json).get("maxHealth", 0)
        # First broadcast per entity wins (decoder guarantee); a duplicate
        # row with a different value would be a decoder regression.
        if entity_id in max_health and max_health[entity_id] != value:
            errors.append(f"entity {entity_id}: conflicting maxHealth {max_health[entity_id]} vs {value}")
        max_health.setdefault(entity_id, value)

    taken: dict[int, int] = {}
    dealt: dict[int, int] = {}
    for entity_id, values_json in cur.execute(
        "SELECT entity_id, values_json FROM canonical_events "
        "WHERE battle_session_id = ? AND kind = ?",
        (session_id, KIND_DAMAGE),
    ):
        payload = json.loads(values_json)
        damage = payload.get("damage", 0)
        taken[entity_id] = taken.get(entity_id, 0) + damage
        attacker = payload.get("attackerEntityId")
        if attacker is not None:
            dealt[attacker] = dealt.get(attacker, 0) + damage

    destroyed: set[int] = {
        row[0]
        for row in cur.execute(
            "SELECT entity_id FROM canonical_events "
            "WHERE battle_session_id = ? AND kind = ?",
            (session_id, KIND_DESTROYED),
        )
    }

    # 1. HP conservation: current = max - taken must never go negative.
    for entity_id in sorted(max_health):
        maximum = max_health[entity_id]
        damage_taken = taken.get(entity_id, 0)
        current = maximum - damage_taken
        if current < 0:
            errors.append(
                f"entity {entity_id}: HP went negative (max {maximum}, taken {damage_taken})"
            )

    # 2. Ledger balance: attacker-side totals equal victim-side totals.
    if sum(dealt.values()) != sum(taken.values()):
        errors.append(
            f"ledger imbalance: dealt {sum(dealt.values())} != taken {sum(taken.values())}"
        )

    # 3. Alive alignment: ledger-dead iff Destroyed event exists.
    ledger_dead = {entity_id for entity_id in max_health if taken.get(entity_id, 0) >= max_health[entity_id]}
    for entity_id in sorted(ledger_dead - destroyed):
        errors.append(f"entity {entity_id}: ledger reached 0 HP but no Destroyed event")
    for entity_id in sorted(destroyed - ledger_dead):
        errors.append(
            f"entity {entity_id}: Destroyed event but ledger has {max_health.get(entity_id, 0) - taken.get(entity_id, 0)} HP remaining"
        )

    # 4. battle_results cross-check for participants WITH battle stats.
    for player_name, entity_id, stats_json in cur.execute(
        "SELECT player_name, entity_id, battle_stats_json FROM participants "
        "WHERE battle_session_id = ?",
        (session_id,),
    ):
        if not stats_json:
            # Player left the battle; WoT records no results for them. Their
            # decoded damage is still true and covered by checks 1-3.
            continue
        battle_damage = json.loads(stats_json).get("damageDealt", 0) or 0
        decoded = dealt.get(entity_id, 0)
        if battle_damage != decoded:
            errors.append(
                f"{player_name} (entity {entity_id}): decoded damage {decoded} != battle_results {battle_damage}"
            )

    if not errors:
        summary = (
            f"PASS session {session_id} ({map_name or '?'}): {len(max_health)} tanks, "
            f"{len(destroyed)} destroyed, conservation + balance + alive alignment + "
            f"battle_results cross-check all OK"
        )
        print(summary)
    return errors


def discover_sessions(cur: sqlite3.Cursor, limit: int | None) -> list[tuple[str, str | None]]:
    query = (
        "SELECT bs.id, bs.map_name FROM battle_sessions bs "
        "JOIN decode_runs dr ON dr.id = bs.decode_run_id "
        "ORDER BY dr.completed_at_utc DESC"
    )
    if limit is not None:
        query += f" LIMIT {int(limit)}"
    return [(row[0], row[1]) for row in cur.execute(query)]


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify the decoded HP ledger invariants.")
    parser.add_argument("--db", required=True, help="Path to the treader SQLite database.")
    parser.add_argument("--session", action="append", help="Battle session id to check (repeatable).")
    parser.add_argument("--latest", type=int, help="Check the N most recently decoded sessions.")
    args = parser.parse_args()

    connection = sqlite3.connect(args.db)
    cursor = connection.cursor()

    if args.session:
        sessions = [(session_id, None) for session_id in args.session]
    else:
        sessions = discover_sessions(cursor, args.latest)

    if not sessions:
        print("No battle sessions found.", file=sys.stderr)
        return 1

    all_errors: list[str] = []
    for session_id, map_name in sessions:
        all_errors.extend(verify_session(cursor, session_id, map_name))

    connection.close()
    if all_errors:
        print(f"FAIL: {len(all_errors)} error(s):", file=sys.stderr)
        for error in all_errors:
            print(f"  {error}", file=sys.stderr)
        return 1
    print(f"All {len(sessions)} session(s) pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
