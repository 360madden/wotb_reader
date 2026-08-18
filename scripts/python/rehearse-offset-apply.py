#!/usr/bin/env python3
"""Rehearse an offset-publication apply on SCRATCH copies before the
operator approves it.

Why: every publication apply (G0 position, G1 HP/yaw, G1 pitch/roll, G2
damage-dealt) touches the published table + validator + schema + pack doc,
and each apply has surprised us with at least one extra artifact the draft
did not enumerate (G2's rehearsal caught an incomplete checker spec; the
real gates then surfaced report-offset-evidence.ps1 and
OffsetTableReader.KnownFieldNames). Rehearsing the apply against scratch
copies BEFORE the operator commits turns approval into low-risk mechanical
execution.

Usage:
    python scripts/python/rehearse-offset-apply.py --package g2-damage-dealt
    python scripts/python/rehearse-offset-apply.py --package g1-pitch-roll
    python scripts/python/rehearse-offset-apply.py --all

The tool copies the real schema/table/draft/doc to .build/rehearse-<pkg>/,
applies the package's manifest edits (the SAME edits the proven rehearsal
harnesses applied), builds a scratch validator (the manifest's checker
patches + scratch paths; the canonical draft + pack doc cross-checks stay
REAL, because the apply's fidelity is against the REAL canonical draft),
and runs --check-schema. Exit 0 = the apply as specified PASSES the gates.

Register a new publication: add a manifest to MANIFESTS with the file
edits + checker patches, mirroring the two proven packages. The manifest
IS the rehearsal spec; keep it in lockstep with the publication draft's
apply steps.
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
BUILD = ROOT / ".build"

# The G2 damage-dealt chain (vftableScan hop + recordOffset 280) — the §4
# copy-verbatim JSON from g2-damage-dealt-publication-draft.md.
G2_CHAIN = [
    {
        "kind": "vftableScan",
        "value": 52908708,
        "note": "gated AOB scan for the entity-factory Avatar vftable dword == moduleBase + 0x032752a4 (0x128-byte object); identity re-gated; max 4 candidates, alignment 4; own counter discriminated by increment correlation (OD-RECOVERY-095/096)",
    },
    {
        "kind": "recordOffset",
        "value": 280,
        "note": "uint32 battle-stats quad base [avatar+0x118]; dword0 = cumulative own damageDealt; quad = [damageDealt, damageBlocked, damageAssisted1, damageAssisted2] (indices 0xA-0xD); OD-RECOVERY-095/096 live-verified (finals 752 / 1598 = decoded damageDealt)",
    },
]

G2_FV = {
    "status": "Verified",
    "evidence": [
        {
            "provenanceKind": "DynamicScan",
            "sourceTool": "GameHarness loopback hp-diff live correlator (increment + bounded lag window)",
            "notes": "OD-RECOVERY-095 (2026-08-12, savanna, session 019ff5f1, 20 region dumps): avatar-stats quad dword0 increments 1:1 with the decoded own-attacker events - re-verdict with the bounded lag path (--lag-tolerance): offset 0x0, score 1.0, matched 5/5 damage windows with EXACT sums (152/144/151/170/1), flatness 1.0, Strict 5/5 -> HIT; d0 final 752 = decoded damageDealt 752; the at-session lag-0 honest-negative was the OD-087 memory-apply lag class (+2.3-4.1 s).",
        },
        {
            "provenanceKind": "DynamicScan",
            "sourceTool": "GameHarness loopback hp-diff live correlator (increment + bounded lag window)",
            "notes": "OD-RECOVERY-096 (2026-08-12, medvedkovo, session 019ff6f0, 38 dumps, clock labels 158.0-276.9 s): offset 0x0, score 1.0, matched 9/9 windows with EXACT sums (146/162/145/162/140/178/181/171/168), flatness 1.0, Strict >= 2 -> HIT; d0 final 1598 = decoded damageDealt 1598 (all 10 decoded own-attacker events map 1:1; the first 145 at 154.5 s predates the earliest dump label); offsets agree with savanna (0x0) -> twoReplayRepeatability = true.",
        },
    ],
    "independentProcessLaunches": 2,
    "independentReplays": 2,
    "harnessInvariantsPassed": True,
    "leadApproved": True,
    "decoderAuditorApproved": True,
}

# Checker patches applied to the scratch offset_check.py copy. (old, new)
# pairs; each must match exactly once. The G2 set is the CORRECTED step-2
# spec the first rehearsal proved (the draft's bare wording missed
# FIELD_DEFS / OPTIONAL_FIELDS / the shape check / the fidelity branch).
G2_CHECKER_PATCHES = [
    (
        'CHAIN_KINDS = {\n    "rootRva", "memberOffset", "inlineOffset", "recordOffset",\n    "ringIndex", "entityLookup",\n}',
        'CHAIN_KINDS = {\n    "rootRva", "memberOffset", "inlineOffset", "recordOffset",\n    "ringIndex", "entityLookup", "vftableScan",\n}',
    ),
    (
        'for field in ("playerPositionX", "playerPositionY", "playerPositionZ",\n'
        '                  "playerYaw", "playerPitch", "playerRoll", "playerHP"):',
        'for field in ("playerPositionX", "playerPositionY", "playerPositionZ",\n'
        '                  "playerYaw", "playerPitch", "playerRoll", "playerHP",\n'
        '                  "damageDealt"):',
    ),
    (
        '    ("cameraPitch", "float", "Camera pitch in radians"),\n    ("aliveTankCount", "int32", "Number of tanks alive"),\n]',
        '    ("cameraPitch", "float", "Camera pitch in radians"),\n    ("aliveTankCount", "int32", "Number of tanks alive"),\n    ("damageDealt", "uint32", "Cumulative own damage dealt (avatar-stats quad dword0)"),\n]',
    ),
    (
        'OPTIONAL_FIELDS = {"playerPitch", "playerRoll"}',
        'OPTIONAL_FIELDS = {"playerPitch", "playerRoll", "damageDealt"}',
    ),
    (
        '                if first != "rootRva" or last != "recordOffset" or not middle_ok:',
        '                if first not in ("rootRva", "vftableScan") or last != "recordOffset" or not middle_ok:',
    ),
    (
        '    if field == "playerHP":\n        # Entity-base chain (HP, G1 pre-stage 2026-08-11):',
        '    if field == "damageDealt":\n'
        '        # Avatar-stats scan chain (G2, 2026-08-12): the vftableScan anchor is\n'
        '        # the gated AOB scan for the entity-factory Avatar (moduleBase + RVA\n'
        '        # 0x032752a4), recordOffset 280 = the uint32 quad base dword0.\n'
        '        # Published must be IDENTICAL to the canonical draft (signature per\n'
        '        # hop) - OD-RECOVERY-095/096 live-verified.\n'
        '        expected_kinds = ["vftableScan", "recordOffset"]\n'
        '        actual_kinds = [h.get("kind") for h in dr]\n'
        '        if actual_kinds != expected_kinds:\n'
        '            issues.append(f"{tag}: unexpected avatar-stats chain shape "\n'
        '                          f"{actual_kinds} (expected {expected_kinds})")\n'
        '            return issues\n'
        '        pub_kinds = [h.get("kind") for h in pub]\n'
        '        if pub_kinds != expected_kinds:\n'
        '            issues.append(f"{tag}: published chain has unrecognized shape "\n'
        '                          f"({len(pub)} hops, kinds {pub_kinds})")\n'
        '            return issues\n'
        '        if [_hop_signature(h) for h in pub] != [_hop_signature(h) for h in dr]:\n'
        '            issues.append(f"{tag}: published avatar-stats chain differs from the "\n'
        '                          f"canonical draft (signature per hop)")\n'
        '        return issues\n'
        '\n'
        '    if field == "playerHP":\n'
        '        # Entity-base chain (HP, G1 pre-stage 2026-08-11):',
    ),
]

# Per-package manifests. Each: which real files to copy, the JSON edit
# functions, the checker patches (empty for table-only packages), and the
# expected validator log lines. The manifest mirrors the publication
# draft's apply steps exactly.
MANIFESTS = {
    "g2-damage-dealt": {
        "files": ("schema.json", "11.19.0.10.json",
                  "g0-walkable-position-chains.draft.json", "memory-offsets.md"),
        "checker_patches": G2_CHECKER_PATCHES,
        "apply": lambda scratch: (
            edit_g2_schema(scratch / "schema.json"),
            edit_g2_draft(scratch / "g0-walkable-position-chains.draft.json"),
            edit_g2_table(scratch / "11.19.0.10.json"),
            edit_g2_doc(scratch / "memory-offsets.md"),
        ),
        "expect": ("chains validated (6 field(s))", "fidelity"),
    },
    "g1-pitch-roll": {
        "files": ("schema.json", "11.19.0.10.json", "memory-offsets.md"),
        "checker_patches": [],
        "apply": lambda scratch: (
            edit_pr_table(scratch / "11.19.0.10.json"),
            edit_pr_doc(scratch / "memory-offsets.md"),
        ),
        "expect": ("chains validated (8 field(s))", "fidelity"),
    },
}

REAL = {
    "schema": ROOT / "memory-offsets" / "schema.json",
    "table": ROOT / "memory-offsets" / "11.19.0.10.json",
    "draft": ROOT / "docs" / "operations" / "g0-walkable-position-chains.draft.json",
    "doc": ROOT / "offline" / "memory-offsets.md",
    "checker": ROOT / "scripts" / "python" / "offset_check.py",
}


def load(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def dump(p: Path, data):
    p.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def edit_g2_schema(p: Path):
    s = load(p)
    for section in ("chains", "fieldValidation"):
        enum = s["properties"][section]["propertyNames"]["enum"]
        if "damageDealt" not in enum:
            enum.append("damageDealt")
    kind_enum = s["properties"]["chains"]["additionalProperties"]["items"]["properties"]["kind"]["enum"]
    if "vftableScan" not in kind_enum:
        kind_enum.append("vftableScan")
    s["properties"]["offsets"]["properties"]["damageDealt"] = {
        "type": "integer",
        "description": "Chained field: see `chains`. The integer stays 0 (the runtime computes moduleBase + offset and the Avatar object is battle-scoped heap). uint32 cumulative own damage dealt at the avatar-stats quad dword0 [avatar+0x118].",
    }
    dump(p, s)


def edit_g2_draft(p: Path):
    d = load(p)
    d["chains"]["damageDealt"] = G2_CHAIN
    dump(p, d)


def edit_g2_table(p: Path):
    t = load(p)
    t["chains"]["damageDealt"] = G2_CHAIN
    t["offsets"]["damageDealt"] = 0
    t["fieldValidation"]["damageDealt"] = G2_FV
    t["notes"] = t.get("notes", "") + "\n\nG2 rehearsal manifest applied (damageDealt via vftableScan chain)."
    dump(p, t)


def edit_g2_doc(p: Path):
    text = p.read_text(encoding="utf-8")
    # Idempotent: the example block may already carry the field when the
    # apply is already landed (rehearsing the applied state is a no-op pass).
    if '"damageDealt": 0' not in text:
        old = '"cameraPitch": 0, "aliveTankCount": 0 }'
        new = '"cameraPitch": 0, "aliveTankCount": 0, "damageDealt": 0 }'
        assert text.count(old) >= 1, "example offsets line not found"
        text = text.replace(old, new)
    if '"vftableScan"' not in text:
        old_kind = ('"ringIndex" | "entityLookup", "value"')
        new_kind = ('"ringIndex" | "entityLookup" | "vftableScan", "value"')
        assert text.count(old_kind) == 1, text.count(old_kind)
        text = text.replace(old_kind, new_kind)
    p.write_text(text, encoding="utf-8")


def edit_pr_table(p: Path):
    t = load(p)
    draft = load(REAL["draft"])
    t["chains"]["playerPitch"] = draft["chains"]["playerPitch"]
    t["chains"]["playerRoll"] = draft["chains"]["playerRoll"]
    t["offsets"]["playerPitch"] = 0
    t["offsets"]["playerRoll"] = 0
    t["fieldValidation"]["playerPitch"] = {"status": "Verified", "evidence": []}
    t["fieldValidation"]["playerRoll"] = {"status": "Verified", "evidence": []}
    dump(p, t)


def edit_pr_doc(p: Path):
    text = p.read_text(encoding="utf-8")
    # Idempotent: skip when the fields are already documented (the apply
    # landed).
    if '"playerPitch": 0' not in text:
        old = '"cameraPitch": 0, "aliveTankCount": 0 }'
        new = '"cameraPitch": 0, "aliveTankCount": 0, "playerPitch": 0, "playerRoll": 0 }'
        assert text.count(old) >= 1, "example offsets line not found"
        text = text.replace(old, new)
    p.write_text(text, encoding="utf-8")


def build_checker(scratch: Path, patches) -> Path:
    src = REAL["checker"].read_text(encoding="utf-8")
    for old, new in patches:
        if new in src:
            continue  # already applied (rehearsing the applied state)
        assert src.count(old) == 1, f"checker patch target not found: {old[:60]!r}"
        src = src.replace(old, new)
    src = src.replace('OFFSET_DIR = REPO_ROOT / "memory-offsets"',
                      'OFFSET_DIR = Path(__file__).resolve().parent')
    src = src.replace('LOG_DIR = REPO_ROOT / ".build"',
                      'LOG_DIR = Path(__file__).resolve().parent')
    # The walkable-draft + pack-doc cross-checks stay REAL: the apply's
    # fidelity is against the REAL canonical draft.
    checker = scratch / "offset_check.py"
    checker.write_text(src, encoding="utf-8")
    return checker


def rehearse(package: str) -> int:
    manifest = MANIFESTS[package]
    scratch = BUILD / f"rehearse-{package}"
    scratch.mkdir(parents=True, exist_ok=True)
    for key in ("schema", "table", "draft", "doc"):
        if REAL[key].name in manifest["files"]:
            shutil.copy2(REAL[key], scratch / REAL[key].name)
    for edit in manifest["apply"](scratch):
        pass  # the edits are applied for their side effects
    checker = build_checker(scratch, manifest["checker_patches"])
    print(f"\nRehearsing {package} on scratch copies ({scratch}) ...\n")
    result = subprocess.run(
        [sys.executable, str(checker), "--check-schema"],
        cwd=scratch, capture_output=True, text=True)
    out = result.stdout + result.stderr
    shown = 0
    for line in out.splitlines():
        if any(k in line for k in ("chains validated", "PASS", "FAIL", "ISSUE", "fidelity", "drift", "ERROR")):
            print("  " + line.strip())
            shown += 1
        if shown > 25:
            break
    print(f"\nexit code: {result.returncode}")
    return result.returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", choices=sorted(MANIFESTS))
    parser.add_argument("--all", action="store_true")
    args = parser.parse_args()
    if args.all:
        code = 0
        for package in MANIFESTS:
            code = max(code, rehearse(package))
        return code
    if not args.package:
        parser.error("pass --package <name> or --all")
    return rehearse(args.package)


if __name__ == "__main__":
    sys.exit(main())
