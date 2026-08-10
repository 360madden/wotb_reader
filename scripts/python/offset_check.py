#!/usr/bin/env python3
"""offset_check.py — Validate memory-offsets/*.json files for consistency.

Checks every offset file against the schema, verifies SHA256 hash formatting,
reports offset coverage (known vs unknown), and flags suspicious values.

Modes:
  python scripts/python/offset_check.py
      Standard validation of memory-offsets/*.json against schema.json.

  python scripts/python/offset_check.py --check-schema
      Also cross-verify the documented schema in offline/memory-offsets.md
      against schema.json, the version files, and this validator's own
      constants — closing the loop between the pack and the real evidence.

Output: timestamped log to .build/offset-check-<datetime>.log
Exit code: 0 on all valid, 1 on any issues found.
"""

import json
import re
import sys
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
OFFSET_DIR = REPO_ROOT / "memory-offsets"
LOG_DIR = REPO_ROOT / ".build"
SCHEMA_PATH = OFFSET_DIR / "schema.json"
DOC_PACK_PATH = REPO_ROOT / "offline" / "memory-offsets.md"

# The only confidence values the schema, the pack doc, and OffsetConfidence
# (Core/OffsetModels.cs) agree on. Never add "verified" here — it is not a
# file-level confidence level anywhere in the contract.
CONFIDENCE_VALUES = ("none", "low", "medium", "high")

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

FIELD_NAMES = {name for name, _type, _desc in FIELD_DEFS}

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


# ── Schema meta-file validation ──────────────────────────────────────────────

def validate_schema(log_path: Path, schema: dict) -> list[str]:
    """Validate schema.json itself (it is a draft 2020-12 meta-schema, not a
    version file — it has no `schemaVersion` property of its own)."""
    issues: list[str] = []

    if not str(schema.get("$schema", "")).startswith("https://json-schema.org/draft/2020-12"):
        issues.append("schema.json $schema is not draft 2020-12")

    if schema.get("type") != "object":
        issues.append("schema.json type is not 'object'")

    required = set(schema.get("required", []))
    if required != {"schemaVersion", "gameVersion", "offsets"}:
        issues.append(
            f"schema.json required={sorted(required)}, expected "
            "[gameVersion, offsets, schemaVersion]")

    offsets = schema.get("properties", {}).get("offsets", {})
    schema_fields = set(offsets.get("properties", {}).keys())
    if schema_fields != FIELD_NAMES:
        issues.append(
            f"schema.json offsets.properties={sorted(schema_fields)}, expected "
            f"{sorted(FIELD_NAMES)}")

    offsets_required = set(offsets.get("required", []))
    if offsets_required and offsets_required != FIELD_NAMES:
        issues.append(
            f"schema.json offsets.required={sorted(offsets_required)}, expected "
            f"{sorted(FIELD_NAMES)}")

    if offsets.get("additionalProperties") is not False:
        issues.append("schema.json offsets.additionalProperties is not false")

    confidence_enum = set(schema.get("properties", {}).get("confidence", {}).get("enum", []))
    if confidence_enum and confidence_enum != set(CONFIDENCE_VALUES):
        issues.append(
            f"schema.json confidence.enum={sorted(confidence_enum)}, expected "
            f"{sorted(CONFIDENCE_VALUES)}")

    return issues


# ── Pack documentation cross-verification (--check-schema) ──────────────────

def extract_documented_schema(doc_path: Path) -> dict[str, set[str]]:
    """Parse offline/memory-offsets.md for the documented schema contract.

    Returns {offset_fields, confidence, required} as sets of strings, derived
    from the example JSON block(s), the confidence-levels table, and the
    "Required:" prose line.
    """
    text = doc_path.read_text(encoding="utf-8")

    offset_fields: set[str] = set()
    confidence_levels: set[str] = set()
    required_fields: set[str] = set()

    # Example JSON block(s) — authoritative for the 8 field names.
    for block in re.findall(r"```json\s*\n(.*?)```", text, re.DOTALL):
        try:
            data = json.loads(block)
        except json.JSONDecodeError:
            continue
        if not isinstance(data, dict):
            continue
        offsets = data.get("offsets")
        if isinstance(offsets, dict):
            offset_fields |= set(offsets.keys())
        for key in ("schemaVersion", "gameVersion", "offsets"):
            if key in data:
                required_fields.add(key)
        if isinstance(data.get("confidence"), str):
            confidence_levels.add(data["confidence"])

    # Confidence-levels table (| `none` | Placeholder, ... |).
    table_section = re.search(r"## Confidence levels\s*\n(.*?)(?=\n## |\Z)", text, re.DOTALL)
    if table_section:
        for line in table_section.group(1).splitlines():
            row = re.match(r"\|\s*`([^`]+)`\s*\|", line)
            if row:
                confidence_levels.add(row.group(1).strip())

    # "Required: `schemaVersion`, `gameVersion`, `offsets` (all 8 fields,
    # `additionalProperties: false`)." — the prose may wrap across lines and
    # the parenthetical also contains a backtick token, so only match plain
    # identifier tokens (no colons/spaces) and keep searching line by line.
    for line in text.splitlines():
        if line.strip().startswith("Required:"):
            required_fields |= set(
                re.findall(r"`([A-Za-z][A-Za-z0-9]*)`", line))

    return {
        "offset_fields": offset_fields,
        "confidence": confidence_levels,
        "required": required_fields,
    }


def check_documented_schema(log_path: Path, doc: dict[str, set[str]]) -> list[str]:
    """Cross-verify the parsed pack-doc contract against schema.json and this
    validator's own constants. Returns issues (empty = consistent)."""
    issues: list[str] = []

    if not doc["offset_fields"]:
        issues.append("could not extract offset field names from offline/memory-offsets.md")

    try:
        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        issues.append(f"schema.json is invalid JSON — {e}")
        return issues

    schema_offsets = schema.get("properties", {}).get("offsets", {})
    schema_fields = set(schema_offsets.get("properties", {}).keys())
    schema_required = set(schema.get("required", []))
    schema_conf = set(schema.get("properties", {}).get("confidence", {}).get("enum", []))

    # doc <-> schema.json
    if doc["offset_fields"] and schema_fields and doc["offset_fields"] != schema_fields:
        issues.append(
            "field drift: pack doc=" + ",".join(sorted(doc["offset_fields"]))
            + " schema.json=" + ",".join(sorted(schema_fields)))
    if doc["required"] and schema_required and doc["required"] != schema_required:
        issues.append(
            "required drift: pack doc=" + ",".join(sorted(doc["required"]))
            + " schema.json=" + ",".join(sorted(schema_required)))
    if doc["confidence"] and schema_conf and doc["confidence"] != schema_conf:
        issues.append(
            "confidence drift: pack doc=" + ",".join(sorted(doc["confidence"]))
            + " schema.json=" + ",".join(sorted(schema_conf)))

    # doc <-> validator constants
    if doc["offset_fields"] and FIELD_NAMES != doc["offset_fields"]:
        issues.append(
            "FIELD_DEFS drift vs pack doc: validator=" + ",".join(sorted(FIELD_NAMES))
            + " doc=" + ",".join(sorted(doc["offset_fields"])))
    if doc["confidence"] and set(CONFIDENCE_VALUES) != doc["confidence"]:
        issues.append(
            "CONFIDENCE_VALUES drift vs pack doc: validator="
            + ",".join(sorted(CONFIDENCE_VALUES))
            + " doc=" + ",".join(sorted(doc["confidence"])))

    # schema.json <-> validator constants
    if schema_fields and FIELD_NAMES != schema_fields:
        issues.append(
            "FIELD_DEFS drift vs schema.json: validator=" + ",".join(sorted(FIELD_NAMES))
            + " schema=" + ",".join(sorted(schema_fields)))
    if schema_conf and set(CONFIDENCE_VALUES) != schema_conf:
        issues.append(
            "CONFIDENCE_VALUES drift vs schema.json: validator="
            + ",".join(sorted(CONFIDENCE_VALUES))
            + " schema=" + ",".join(sorted(schema_conf)))

    if not issues:
        write_log(log_path,
                  "  Cross-check: pack doc <-> schema.json <-> validator constants — consistent.")
    return issues


def validate_against_documented_schema(
    log_path: Path, path: Path, doc: dict[str, set[str]]) -> list[str]:
    """Verify one version file against the pack's documented schema. Returns
    issues (empty = conforms to the documentation)."""
    issues: list[str] = []
    rel = path.relative_to(REPO_ROOT)

    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        return [f"{rel}: invalid JSON — {e}"]

    offsets = data.get("offsets", {})
    doc_fields = doc["offset_fields"]
    if doc_fields:
        missing = doc_fields - set(offsets.keys())
        extra = set(offsets.keys()) - doc_fields
        if missing:
            issues.append(
                f"{rel}: documented fields missing from offsets: "
                + ", ".join(sorted(missing)))
        if extra:
            issues.append(
                f"{rel}: undocumented extra offset fields: "
                + ", ".join(sorted(extra)))

    conf = data.get("confidence")
    if doc["confidence"] and conf not in doc["confidence"]:
        issues.append(
            f"{rel}: confidence '{conf}' not in documented levels "
            + ", ".join(sorted(doc["confidence"])))

    return issues


# ── Validation ───────────────────────────────────────────────────────────────

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

    # SHA256: an all-zero confidence-none file is an intentional placeholder.
    # Placeholders stay runtime-unsupported; populated evidence must carry an
    # exact executable hash.
    confidence = data.get("confidence", "")
    is_placeholder = confidence == "none" and all(
        data.get("offsets", {}).get(field, 0) == 0 for field in FIELD_NAMES)
    sha = data.get("executableSha256", "")
    if is_placeholder and sha == "":
        pass
    elif not sha:
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
    if conf not in CONFIDENCE_VALUES:
        issues.append(f"{rel}: unknown confidence '{conf}'")

    # discoveredAtUtc: placeholders intentionally use null; promoted or
    # candidate evidence must record when it was discovered.
    disc = data.get("discoveredAtUtc", "")
    if is_placeholder and disc is None:
        pass
    elif not disc:
        issues.append(f"{rel}: missing discoveredAtUtc")

    # chains: the additive pointer-chain section (2026-08-09, G0 draft -
    # memory-offsets schema extension). A chained field MUST keep its offsets
    # value 0 (the runtime observation path computes moduleBase + offset and
    # cannot represent a chain; a non-zero value would corrupt reads), and
    # each hop must be well-formed. Absent chains = no-op (current files).
    chains = data.get("chains")
    if chains is not None:
        if not isinstance(chains, dict) or not chains:
            issues.append(f"{rel}: 'chains' must be a non-empty object")
        else:
            chain_kinds = {"rootRva", "memberOffset", "recordOffset", "ringIndex"}
            for field_name, hops in chains.items():
                if field_name not in FIELD_NAMES:
                    issues.append(f"{rel}: chains key '{field_name}' is not a known field")
                offset_value = offsets.get(field_name, 0)
                if offset_value != 0:
                    issues.append(
                        f"{rel}: chained field '{field_name}' has non-zero offsets "
                        f"value {offset_value} — chained fields must stay 0 "
                        f"(runtime reads moduleBase + offset)")
                if not isinstance(hops, list) or not hops:
                    issues.append(f"{rel}: chains['{field_name}'] must be a non-empty array")
                    continue
                for hop in hops:
                    if not isinstance(hop, dict) or hop.get("kind") not in chain_kinds:
                        issues.append(
                            f"{rel}: chains['{field_name}'] hop has invalid kind "
                            f"(expected one of {sorted(chain_kinds)})")
                        continue
                    value = hop.get("value")
                    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
                        issues.append(
                            f"{rel}: chains['{field_name}'] hop value must be a "
                            f"non-negative integer")
                    elif value > 0x7FFFFFFF:
                        issues.append(
                            f"{rel}: chains['{field_name}'] hop value 0x{value:X} "
                            f"exceeds 2GB — likely wrong")
                    if hop.get("kind") == "ringIndex":
                        for key in ("indexOffset", "stride"):
                            extra = hop.get(key)
                            if not isinstance(extra, int) or isinstance(extra, bool) or extra < 0:
                                issues.append(
                                    f"{rel}: chains['{field_name}'] ringIndex hop must "
                                    f"have a non-negative integer '{key}'")
                    # Note cross-check: the FIRST hex literal in the note is the
                    # canonical form of this hop's value (later hexes are
                    # vtable RVAs / strides). Catches hex<->decimal transcription
                    # drift (e.g. the G0 grill: 0x04095C88 written as 67518856).
                    note = hop.get("note")
                    if isinstance(note, str):
                        m = re.search(r"0x([0-9A-Fa-f]+)", note)
                        if m and int(m.group(1), 16) != value:
                            issues.append(
                                f"{rel}: chains['{field_name}'] hop value "
                                f"{value} disagrees with its note hex "
                                f"0x{m.group(1)} (expected "
                                f"{int(m.group(1), 16)})")
                # Shape: exactly one rootRva first, exactly one recordOffset
                # last, and only memberOffset/ringIndex in between.
                first = hops[0].get("kind")
                last = hops[-1].get("kind")
                middle_ok = all(
                    h.get("kind") in ("memberOffset", "ringIndex")
                    for h in hops[1:-1])
                if first != "rootRva" or last != "recordOffset" or not middle_ok:
                    issues.append(
                        f"{rel}: chains['{field_name}'] hop shape must be "
                        f"rootRva -> memberOffset|ringIndex* -> recordOffset")
        write_log(log_path, f"  {path.name}: chains validated ({len(chains)} field(s))")

    return issues


# ── Main ─────────────────────────────────────────────────────────────────────

def main() -> int:
    check_schema_mode = "--check-schema" in sys.argv[1:]

    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    log_path = LOG_DIR / f"offset-check-{timestamp}.log"
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    write_log(log_path, "=" * 60)
    write_log(log_path, "WotB Treader — Offset File Validator")
    write_log(log_path, f"Started: {now_iso()}" + (" (--check-schema mode)" if check_schema_mode else ""))
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

    # --check-schema: pack doc <-> schema.json <-> validator constants.
    # Parse the doc once up front; without it the cross-checks are skipped.
    doc_contract: dict[str, set[str]] = {}
    if check_schema_mode:
        write_log(log_path, "Cross-verifying documented schema (offline/memory-offsets.md):")
        if not DOC_PACK_PATH.is_file():
            missing = f"documentation not found: {DOC_PACK_PATH.relative_to(REPO_ROOT)}"
            all_issues.append(missing)
            write_log(log_path, f"  CROSS-CHECK ISSUE: {missing}")
        else:
            doc_contract = extract_documented_schema(DOC_PACK_PATH)
            cross_issues = check_documented_schema(log_path, doc_contract)
            all_issues.extend(cross_issues)
            for issue in cross_issues:
                write_log(log_path, f"  CROSS-CHECK ISSUE: {issue}")
        write_log(log_path, "")

    for path in offset_files:
        issues = validate_offset_file(log_path, path, schema)
        all_issues.extend(issues)
        if issues:
            for issue in issues:
                write_log(log_path, f"  ISSUE: {issue}")

        if check_schema_mode and doc_contract.get("offset_fields"):
            doc_issues = validate_against_documented_schema(log_path, path, doc_contract)
            all_issues.extend(doc_issues)
            for issue in doc_issues:
                write_log(log_path, f"  DOC-CHECK ISSUE: {issue}")

    write_log(log_path, "")
    if all_issues:
        write_log(log_path, f"FAIL: {len(all_issues)} issue(s) found.")
        return 1
    else:
        write_log(log_path, "PASS: All offset files are valid.")
        return 0


if __name__ == "__main__":
    sys.exit(main())
