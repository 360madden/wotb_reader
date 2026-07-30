#!/usr/bin/env python3
"""offset_check.py — Validate memory-offsets/*.json files for consistency.

Checks every offset file against the schema, verifies SHA256 hash formatting,
reports offset coverage (known vs unknown), and flags suspicious values.

Usage:
  python scripts/python/offset_check.py

Output: timestamped log to .build/offset-check-<datetime>.log
Exit code: 0 on all valid, 1 on any issues found.
"""

import json
import sys
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
OFFSET_DIR = REPO_ROOT / "memory-offsets"
LOG_DIR = REPO_ROOT / ".build"
SCHEMA_PATH = OFFSET_DIR / "schema.json"

FIELD_DEFS = [
    ("replayTime", "double", "Replay playback time in seconds"),
    ("playerHP", "int32", "Player tank HP"),
    ("playerPositionX", "float", "World-space X position"),
    ("playerPositionY", "float", "World-space Y position (height)"),
    ("playerPositionZ", "float", "World-space Z position"),
    ("playerYaw", "float", "Camera yaw in radians"),
    ("cameraPitch", "float", "Camera pitch in radians"),
    ("aliveTankCount", "int32", "Number of tanks alive"),
]

# ── Helpers ──────────────────────────────────────────────────────────────────

def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_log(log_path: Path, msg: str) -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")[:23]
    line = f"[{ts}] {msg}"
    print(line)
    with open(log_path, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def is_hex(s: str, length: int = 64) -> bool:
    if not isinstance(s, str):
        return False
    return len(s) == length and all(c in "0123456789abcdefABCDEF" for c in s)


# ── Validation ───────────────────────────────────────────────────────────────

def validate_schema(log_path: Path, schema: dict) -> list[str]:
    issues: list[str] = []
    if schema.get("schemaVersion") != 1:
        issues.append("schemaVersion is not 1")
    return issues


def validate_offset_file(log_path: Path, path: Path, schema: dict) -> list[str]:
    issues: list[str] = []
    rel = path.relative_to(REPO_ROOT)

    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        return [f"{rel}: invalid JSON — {e}"]

    # Schema version
    sv = data.get("schemaVersion")
    if sv != 1:
        issues.append(f"{rel}: schemaVersion={sv}, expected 1")

    # Game version
    gv = data.get("gameVersion", "")
    if not gv:
        issues.append(f"{rel}: missing gameVersion")

    # SHA256
    sha = data.get("executableSha256", "")
    if not sha:
        issues.append(f"{rel}: missing executableSha256")
    elif not is_hex(sha):
        issues.append(f"{rel}: executableSha256 is not a 64-char hex string")

    # Filename should match game version
    expected_name = f"{gv}.json" if gv else None
    if expected_name and path.name != expected_name:
        issues.append(f"{rel}: filename '{path.name}' should be '{expected_name}'")

    # Offsets
    offsets = data.get("offsets", {})
    if not offsets:
        issues.append(f"{rel}: missing offsets object")
    else:
        known = 0
        unknown = 0
        for field_name, field_type, _desc in FIELD_DEFS:
            value = offsets.get(field_name, 0)
            if value == 0:
                unknown += 1
            else:
                known += 1
                # Plausibility checks
                if field_type in ("float", "double") and value < 0x1000:
                    issues.append(
                        f"{rel}: {field_name}={value} (0x{value:X}) looks too small "
                        f"for a {field_type} offset — likely wrong")
                if value > 0x7FFFFFFF:
                    issues.append(
                        f"{rel}: {field_name}=0x{value:X} exceeds 2GB — likely wrong")

        # Check for extra unknown fields
        for key in offsets:
            if key not in {f[0] for f in FIELD_DEFS}:
                issues.append(f"{rel}: unknown field '{key}' in offsets")

        pct = int(known / len(FIELD_DEFS) * 100) if FIELD_DEFS else 0
        write_log(log_path,
                  f"  {path.name}: {known}/{len(FIELD_DEFS)} known ({pct}%) — "
                  f"confidence={data.get('confidence','?')}")

    # Confidence
    conf = data.get("confidence", "")
    if conf not in ("none", "low", "medium", "high", "verified"):
        issues.append(f"{rel}: unknown confidence '{conf}'")

    # discoveredAtUtc
    disc = data.get("discoveredAtUtc", "")
    if not disc:
        issues.append(f"{rel}: missing discoveredAtUtc")

    return issues


# ── Main ─────────────────────────────────────────────────────────────────────

def main() -> int:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    log_path = LOG_DIR / f"offset-check-{timestamp}.log"
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    write_log(log_path, "=" * 60)
    write_log(log_path, "WotB Treader — Offset File Validator")
    write_log(log_path, f"Started: {now_iso()}")
    write_log(log_path, "=" * 60)

    if not OFFSET_DIR.exists():
        write_log(log_path, f"ERROR: memory-offsets/ not found at {OFFSET_DIR}")
        return 1

    if not SCHEMA_PATH.exists():
        write_log(log_path, f"ERROR: schema.json not found at {SCHEMA_PATH}")
        return 1

    # Load schema
    try:
        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        write_log(log_path, f"ERROR: schema.json is invalid JSON — {e}")
        return 1

    schema_issues = validate_schema(log_path, schema)
    if schema_issues:
        for issue in schema_issues:
            write_log(log_path, f"  SCHEMA ISSUE: {issue}")

    # Find and validate offset files
    offset_files = sorted(
        [p for p in OFFSET_DIR.glob("*.json") if p.name not in ("schema.json", "scanner-state.json")],
        key=lambda p: p.name,
    )

    if not offset_files:
        write_log(log_path, "WARNING: No offset files found.")
        return 1

    write_log(log_path, f"Found {len(offset_files)} offset file(s):")
    write_log(log_path, "")

    all_issues: list[str] = []
    for path in offset_files:
        issues = validate_offset_file(log_path, path, schema)
        all_issues.extend(issues)
        if issues:
            for issue in issues:
                write_log(log_path, f"  ISSUE: {issue}")

    write_log(log_path, "")
    if all_issues:
        write_log(log_path, f"FAIL: {len(all_issues)} issue(s) found.")
        return 1
    else:
        write_log(log_path, "PASS: All offset files are valid.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
