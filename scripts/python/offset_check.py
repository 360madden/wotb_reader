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

# Canonical 2nd-generation walkable position-chain form (APPLIED 2026-08-10,
# OD-RECOVERY-084 — the published chains in memory-offsets/11.19.0.10.json now
# ARE this form): single source of truth for the walkable chains. The C# test
# (WalkablePositionChainTests) loads this file through OffsetTableReader; the
# validator checks it with the same rules as the published tables; the
# operator-facing JSON block in g0-offset-table-draft.md §7.4 must match it;
# and the walkable form must match the published chains
# (memory-offsets/11.19.0.10.json) — identical since OD-RECOVERY-084, or the
# re-expression of the pre-publication 16-hop evidence form offset for offset.
WALKABLE_DRAFT_PATH = REPO_ROOT / "docs" / "operations" / "g0-walkable-position-chains.draft.json"
WALKABLE_DRAFT_DOC_PATH = REPO_ROOT / "docs" / "operations" / "g0-offset-table-draft.md"
PUBLISHED_POSITION_TABLE = OFFSET_DIR / "11.19.0.10.json"

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

# The only chain hop kinds the schema, the pack doc, the validator, and
# OffsetChainHopKind (Core/OffsetModels.cs) agree on. Must match
# schema.json's chains.items.properties.kind enum exactly (the cross-check
# below enforces this).
#   rootRva       deref the root slot (moduleBase + value)
#   memberOffset  deref a pointer at (object + value)
#   inlineOffset  add value WITHOUT dereferencing (inline member)
#   ringIndex     INLINE ring entry at (object + value + index*stride)
#   entityLookup  entity-map lookup (cache fast path + tree roots); its
#                 descriptor lives on the hop, and the target entity id is
#                 supplied per walk, never carried by the chain
#   recordOffset  final: add value without dereferencing
CHAIN_KINDS = {
    "rootRva", "memberOffset", "inlineOffset", "recordOffset",
    "ringIndex", "entityLookup",
}

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

    # Chain hop kinds from the Chains section's hop template:
    # `{ "kind": "rootRva" | "memberOffset" | ... , "value": ... }`.
    chain_kinds: set[str] = set()
    kind_alternation = re.search(
        r'"kind":\s*((?:"[A-Za-z]+"\s*\|\s*)+"[A-Za-z]+")', text)
    if kind_alternation:
        chain_kinds |= set(re.findall(r'"([A-Za-z]+)"', kind_alternation.group(1)))

    return {
        "offset_fields": offset_fields,
        "confidence": confidence_levels,
        "required": required_fields,
        "chain_kinds": chain_kinds,
    }


def extract_walkable_draft_block(doc_path: Path):
    """Parse the §7.4 walkable-chain JSON block out of g0-offset-table-draft.md.

    The operator-facing block is the playerPositionX hop ARRAY (the last
    ```json block in the doc that parses to a list of hops). Returns the
    parsed array, or None if it cannot be extracted.
    """
    text = doc_path.read_text(encoding="utf-8")
    for block in reversed(re.findall(r"```json\s*\n(.*?)```", text, re.DOTALL)):
        try:
            data = json.loads(block)
        except json.JSONDecodeError:
            continue
        if isinstance(data, list) and data and isinstance(data[0], dict):
            return data
    return None


def _hop_signature(h: dict) -> tuple:
    """Semantic signature of one hop: kind + all numeric fields. Notes are
    prose and never compared. EntityLookup and ringIndex carry descriptor
    fields beyond kind/value."""
    kind = h.get("kind")
    if kind == "entityLookup":
        return (
            kind, h.get("value"), h.get("cachedEntityOffset"),
            h.get("entityIdOffset"), tuple(h.get("treeRootOffsets") or ()),
            h.get("treeNodeSize"), h.get("treeNodeNilOffset"),
            h.get("treeNodeKeyOffset"), h.get("treeNodeValueOffset"),
            h.get("treeNodeChildLessOffset"), h.get("treeNodeChildGreaterOffset"),
            h.get("treeSentinelFirstNodeOffset"), h.get("maxTreeNodes"),
        )
    if kind == "ringIndex":
        return (kind, h.get("value"), h.get("indexOffset"), h.get("stride"))
    return (kind, h.get("value"))


def walkable_fidelity_issues(field: str, pub: list, dr: list) -> list[str]:
    """Verify ONE walkable draft chain against the published chain for the
    same field. Two generations of the published form are supported:

    - OLD form (16 memberOffset-spelled hops, pre OD-RECOVERY-084): the
      walkable form re-expresses it — the check maps OFFSETS, not hop kinds
      (same root RVA, controller spine, entities map, cache/tree roots,
      filter/helper/ring/index offsets, record offset). The re-expression is
      the point: the draft must be the SAME walk the live evidence verified.
    - WALKABLE form (12 hops, published since OD-RECOVERY-084): the published
      chains must be IDENTICAL to the canonical draft (semantic signature per
      hop) — the invariant that keeps the published table from drifting from
      the canonical artifact. The re-expression-vs-original-evidence proof is
      preserved in git history (commit 0e6bdba) and the ledger."""
    issues: list[str] = []
    tag = f"fidelity[{field}]"

    def value(h):
        return h.get("value")

    if field == "playerHP":
        # Entity-base chain (HP, G1 pre-stage 2026-08-11): the module-rooted
        # walk through the entity lookup ONLY — the health field lives on the
        # ENTITY BASE record itself ([entity+0xB8], OD-RECOVERY-087/091), not
        # on the movement-filter/ring path, so the walkable form is 9 hops
        # (no filter/helper/ring hops). Published must be IDENTICAL to the
        # canonical draft (signature per hop).
        expected_kinds = [
            "rootRva", "memberOffset", "memberOffset", "memberOffset",
            "memberOffset", "memberOffset", "inlineOffset", "entityLookup",
            "recordOffset",
        ]
        actual_kinds = [h.get("kind") for h in dr]
        if actual_kinds != expected_kinds:
            issues.append(f"{tag}: unexpected entity-base chain shape "
                          f"{actual_kinds} (expected {expected_kinds})")
            return issues
        pub_kinds = [h.get("kind") for h in pub]
        if pub_kinds != expected_kinds:
            issues.append(f"{tag}: published chain has unrecognized shape "
                          f"({len(pub)} hops, kinds {pub_kinds})")
            return issues
        if [_hop_signature(h) for h in pub] != [_hop_signature(h) for h in dr]:
            issues.append(f"{tag}: published entity-base chain differs from the "
                          f"canonical draft (signature per hop)")
        return issues

    # The walkable form's shape is pinned by schema.json; guard before
    # indexing (a shape change must fail, not index-panic).
    expected_kinds = [
        "rootRva", "memberOffset", "memberOffset", "memberOffset",
        "memberOffset", "memberOffset", "inlineOffset", "entityLookup",
        "memberOffset", "memberOffset", "ringIndex", "recordOffset",
    ]
    actual_kinds = [h.get("kind") for h in dr]
    if actual_kinds != expected_kinds:
        issues.append(f"{tag}: unexpected walkable chain shape {actual_kinds} "
                      f"(expected {expected_kinds})")
        return issues

    pub_kinds = [h.get("kind") for h in pub]
    if pub_kinds == expected_kinds:
        # Published is already the walkable form (OD-RECOVERY-084+): identity.
        if [_hop_signature(h) for h in pub] != [_hop_signature(h) for h in dr]:
            issues.append(f"{tag}: published walkable chain differs from the "
                          f"canonical draft (signature per hop)")
        return issues

    # Old memberOffset-spelled form (16 hops): re-expression mapping.
    if len(pub) != 16:
        issues.append(f"{tag}: published chain has unrecognized shape "
                      f"({len(pub)} hops, kinds {pub_kinds})")
        return issues

    if pub[0].get("kind") != "rootRva" or value(pub[0]) != value(dr[0]):
        issues.append(f"{tag}: root RVA differs — published={value(pub[0])} "
                      f"draft={value(dr[0])}")

    # Controller spine + entities map. The published memberOffset 0x04 is the
    # BWEntities map; the walkable form corrects it to inlineOffset (the
    # resolver treats the map as INLINE) — same offset, kind differs by design.
    pub_spine = [value(h) for h in pub[1:7]]
    dr_spine = [value(h) for h in dr[1:7]]
    if pub_spine != dr_spine:
        issues.append(f"{tag}: controller spine differs — published={pub_spine} "
                      f"draft={dr_spine}")

    # Cached fast path + ALTERNATIVE tree roots (order-sensitive).
    if value(dr[7]) != 0:
        issues.append(f"{tag}: entityLookup hop value must be 0 (the descriptor "
                      f"carries the offsets)")
    if dr[7].get("cachedEntityOffset") != value(pub[7]):
        issues.append(f"{tag}: cache offset differs — published={value(pub[7])} "
                      f"draft={dr[7].get('cachedEntityOffset')}")
    pub_trees = [value(h) for h in pub[8:11]]
    if dr[7].get("treeRootOffsets") != pub_trees:
        issues.append(f"{tag}: tree roots differ — published={pub_trees} "
                      f"draft={dr[7].get('treeRootOffsets')}")

    # Movement filter + avatar helper.
    if value(dr[8]) != value(pub[11]) or value(dr[9]) != value(pub[12]):
        issues.append(f"{tag}: filter/helper offsets differ — published="
                      f"{value(pub[11])}/{value(pub[12])} draft="
                      f"{value(dr[8])}/{value(dr[9])}")

    # Ring: base + index offset + stride (stride from the published note hex).
    if value(dr[10]) != value(pub[13]):
        issues.append(f"{tag}: ring base differs — published={value(pub[13])} "
                      f"draft={value(dr[10])}")
    if dr[10].get("indexOffset") != value(pub[14]):
        issues.append(f"{tag}: ring index offset differs — published={value(pub[14])} "
                      f"draft={dr[10].get('indexOffset')}")
    m = re.search(r"stride 0x([0-9A-Fa-f]+)", pub[13].get("note", ""))
    if m and dr[10].get("stride") != int(m.group(1), 16):
        issues.append(f"{tag}: ring stride differs — published note 0x{m.group(1)} "
                      f"draft={dr[10].get('stride')}")

    # Record offset.
    if value(dr[11]) != value(pub[15]):
        issues.append(f"{tag}: record offset differs — published={value(pub[15])} "
                      f"draft={value(dr[11])}")

    return issues


def check_walkable_fidelity(log_path: Path) -> list[str]:
    """Verify the canonical walkable draft re-expresses the PUBLISHED
    evidence chains (memory-offsets/11.19.0.10.json) for every position field
    present in both: the walkable form must be the same walk the live evidence
    (OD-RECOVERY-083) verified, offset for offset."""
    issues: list[str] = []

    try:
        published = json.loads(PUBLISHED_POSITION_TABLE.read_text(encoding="utf-8"))
        draft = json.loads(WALKABLE_DRAFT_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        return [f"fidelity check: invalid JSON — {e}"]

    pub_chains = published.get("chains", {})
    draft_chains = draft.get("chains", {})
    checked = 0
    # playerYaw joined the walkable family 2026-08-11 (G1 pre-stage): the
    # SAME position prefix with recordOffset 48 (+0x30), OD-RECOVERY-088/089
    # live-verified. playerHP joined the same day (G1 pre-stage): the
    # entity-base chain (entityLookup prefix + recordOffset 184 [+0xB8]),
    # OD-RECOVERY-087/091 live-verified. Fields present in only ONE side are
    # skipped (each fidelity check goes active the moment the published
    # table gains its chain).
    for field in ("playerPositionX", "playerPositionY", "playerPositionZ",
                  "playerYaw", "playerHP"):
        pub = pub_chains.get(field)
        dr = draft_chains.get(field)
        if pub is None or dr is None:
            continue
        checked += 1
        issues.extend(walkable_fidelity_issues(field, pub, dr))

    write_log(log_path,
              f"  fidelity: walkable draft matches the published "
              f"chains ({checked} field(s))")
    return issues


def check_walkable_draft(log_path: Path) -> list[str]:
    """Validate the canonical walkable draft file (docs/operations/
    g0-walkable-position-chains.draft.json) with the same chain rules as the
    published tables, and cross-check the operator-facing §7.4 JSON block in
    g0-offset-table-draft.md against the file's playerPositionX chain — the
    file is authoritative; any doc/file drift fails the gate."""
    issues: list[str] = []

    if not WALKABLE_DRAFT_PATH.is_file():
        issues.append(
            f"walkable draft not found: "
            f"{WALKABLE_DRAFT_PATH.relative_to(REPO_ROOT)}")
        return issues

    try:
        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        return [f"schema.json is invalid JSON — {e}"]

    issues.extend(validate_offset_file(log_path, WALKABLE_DRAFT_PATH, schema))
    issues.extend(check_walkable_fidelity(log_path))

    if WALKABLE_DRAFT_DOC_PATH.is_file():
        block = extract_walkable_draft_block(WALKABLE_DRAFT_DOC_PATH)
        if block is None:
            issues.append(
                "g0-offset-table-draft.md §7.4 walkable JSON block not found")
        else:
            try:
                data = json.loads(WALKABLE_DRAFT_PATH.read_text(encoding="utf-8"))
                x = data.get("chains", {}).get("playerPositionX")
                if x != block:
                    issues.append(
                        "walkable draft drift: g0-offset-table-draft.md §7.4 block "
                        "does not match the canonical file's playerPositionX chain "
                        "(file is authoritative)")
            except json.JSONDecodeError as e:
                issues.append(f"walkable draft file is invalid JSON — {e}")
    return issues


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
    chain_items = (
        schema.get("properties", {})
        .get("chains", {})
        .get("additionalProperties", {})
        .get("items", {})
    )
    schema_chain_kinds = set(
        (chain_items.get("properties", {}) or {}).get("kind", {}).get("enum", []))

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

    # chain hop kinds across all three sources
    if doc["chain_kinds"] and schema_chain_kinds and doc["chain_kinds"] != schema_chain_kinds:
        issues.append(
            "chain-kinds drift: pack doc=" + ",".join(sorted(doc["chain_kinds"]))
            + " schema.json=" + ",".join(sorted(schema_chain_kinds)))
    if doc["chain_kinds"] and CHAIN_KINDS != doc["chain_kinds"]:
        issues.append(
            "CHAIN_KINDS drift vs pack doc: validator=" + ",".join(sorted(CHAIN_KINDS))
            + " doc=" + ",".join(sorted(doc["chain_kinds"])))
    if schema_chain_kinds and CHAIN_KINDS != schema_chain_kinds:
        issues.append(
            "CHAIN_KINDS drift vs schema.json: validator=" + ",".join(sorted(CHAIN_KINDS))
            + " schema=" + ",".join(sorted(schema_chain_kinds)))

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
            chain_kinds = CHAIN_KINDS
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
                    if hop.get("kind") == "entityLookup":
                        if value != 0:
                            issues.append(
                                f"{rel}: chains['{field_name}'] entityLookup hop value "
                                f"must be 0 (the hop operates on the current object)")
                        for key in (
                            "cachedEntityOffset", "entityIdOffset", "treeNodeSize",
                            "treeNodeNilOffset", "treeNodeKeyOffset",
                            "treeNodeValueOffset", "treeNodeChildLessOffset",
                            "treeNodeChildGreaterOffset", "treeSentinelFirstNodeOffset",
                            "maxTreeNodes",
                        ):
                            extra = hop.get(key)
                            if not isinstance(extra, int) or isinstance(extra, bool) or extra < 0:
                                issues.append(
                                    f"{rel}: chains['{field_name}'] entityLookup hop must "
                                    f"have a non-negative integer '{key}'")
                        roots = hop.get("treeRootOffsets")
                        if not isinstance(roots, list) or not roots or not all(
                            isinstance(r, int) and not isinstance(r, bool) and r >= 0
                            for r in roots
                        ):
                            issues.append(
                                f"{rel}: chains['{field_name}'] entityLookup hop must "
                                f"have a non-empty treeRootOffsets list of "
                                f"non-negative integers")
                        node_size = hop.get("treeNodeSize")
                        if not isinstance(node_size, int) or isinstance(node_size, bool) or node_size < 1:
                            issues.append(
                                f"{rel}: chains['{field_name}'] entityLookup hop must "
                                f"have treeNodeSize >= 1")
                        max_nodes = hop.get("maxTreeNodes")
                        if not isinstance(max_nodes, int) or isinstance(max_nodes, bool) or max_nodes < 1:
                            issues.append(
                                f"{rel}: chains['{field_name}'] entityLookup hop must "
                                f"have maxTreeNodes >= 1")
                    # Note cross-check: the FIRST hex literal in the note is the
                    # canonical form of this hop's value (later hexes are
                    # vtable RVAs / strides). Catches hex<->decimal transcription
                    # drift (e.g. the G0 grill: 0x04095C88 written as 67518856).
                    # entityLookup hops are exempt: their note describes the
                    # descriptor offsets (e.g. cached 0x48, roots 0x1C...), not
                    # the hop's value (always 0).
                    note = hop.get("note")
                    if isinstance(note, str) and hop.get("kind") != "entityLookup":
                        m = re.search(r"0x([0-9A-Fa-f]+)", note)
                        if m and int(m.group(1), 16) != value:
                            issues.append(
                                f"{rel}: chains['{field_name}'] hop value "
                                f"{value} disagrees with its note hex "
                                f"0x{m.group(1)} (expected "
                                f"{int(m.group(1), 16)})")
                # Shape: exactly one rootRva first, exactly one recordOffset
                # last, and only memberOffset/inlineOffset/ringIndex/entityLookup
                # in between.
                first = hops[0].get("kind")
                last = hops[-1].get("kind")
                middle_ok = all(
                    h.get("kind") in (
                        "memberOffset", "inlineOffset", "ringIndex", "entityLookup",
                    )
                    for h in hops[1:-1])
                if first != "rootRva" or last != "recordOffset" or not middle_ok:
                    issues.append(
                        f"{rel}: chains['{field_name}'] hop shape must be "
                        f"rootRva -> memberOffset|inlineOffset|ringIndex|entityLookup* "
                        f"-> recordOffset")
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

    # Walkable draft (canonical 2nd-generation chain form, G0 draft §7).
    write_log(log_path, "")
    write_log(log_path,
              "Validating walkable draft (docs/operations/g0-walkable-position-chains.draft.json):")
    draft_issues = check_walkable_draft(log_path)
    all_issues.extend(draft_issues)
    for issue in draft_issues:
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
