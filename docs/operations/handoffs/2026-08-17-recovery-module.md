# Handoff: RECOVERY build-drift module (2026-08-17)

**Date:** 2026-08-17
**Status:** Complete — module shipped at `6b289de`; triage verified against the
installed build (same-build, exit 0)
**Campaign:** build-drift readiness (BLK-0027 unaffected)

## Repository state

- **Branch:** `main`
- **Head:** `6b289de` — `feat(recovery): add build-drift triage script and re-verification playbook`
- **Working tree:** clean except the pre-existing CRLF-only phantom
  (`docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md`).

## What was done

New root module `RECOVERY/` (owner decision: its own folder at repo root,
outside `docs/operations/`):

- `RECOVERY/README.md` — module index, trigger conditions, quickstart,
  explicit non-goals (no auto-migration, no bulk re-discovery).
- `RECOVERY/build-drift-recovery.md` — the playbook: Step 0 triage, Step 1
  freeze/ratchet (immutable records + promotion-checklist staleness), Step 2
  replay-format drift check (golden-vector crosscheck), Step 3 per-field
  re-verification in dependency order with the 8 published fields + camera +
  pen-anchor route table, Step 4 re-publication; evidence-first rule and
  stop conditions throughout.
- `RECOVERY/invoke-build-drift-triage.ps1` — read-only triage: discovers the
  installed `wotblitz.exe`, compares its SHA-256 against every
  `memory-offsets/*.json` table, extracts the module-relative anchor hops
  (`rootRva` / `vftableScan`) from the chains, writes a JSON report to
  `.build/reports/` (git-ignored). Exit codes 0 = same build / 1 = drifted /
  2 = exe not found / 3 = failure.

Integration: root `README.md` documentation table, `offline/repo-map.md`
root layout, `AGENTS.md` authority list item 5 (build-drift entry point),
`offline/file-tree.md` regenerated.

## Validation commands and results

```powershell
powershell -NoProfile -Command "ParseFile('RECOVERY\invoke-build-drift-triage.ps1')"
# PARSE_OK (Windows PowerShell 5.1)

powershell -NoProfile -ExecutionPolicy Bypass -File RECOVERY\invoke-build-drift-triage.ps1
# Found C:\Games\World_of_Tanks_Blitz\wotblitz.exe v11.19.0.10
# SHA-256 1cda5c31... MATCH on the 11.19.0.10 table (8 fields, 8 anchors);
# legacy 11.18.0.7 / 11.8.0.7 tables correctly DRIFT.
# Verdict: same-build (exit 0). Report in .build/reports/.

powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-scriptanalyzer.ps1
# Analyzed 145 tracked .ps1 files with PSScriptAnalyzer 1.25.0
# SCRIPT HYGIENE GATE PASSED (exit 0)

python scripts/python/offline_check.py --refresh
python scripts/python/offline_check.py --check-fresh
# 0 broken links; BLK-0001..BLK-0027 contiguous; ledger consistency OK; file-tree fresh
```

## Assumptions and unknowns

- Old tables (`11.18.0.7`, `11.8.0.7`) showing DRIFT is expected: they record
  other builds' hashes; the verdict keys off the newest readable table.
- The playbook's per-field route table references the known hash-bound tools;
  re-derivation for a real future build may need new tooling if a surface
  moves in an unexpected way — that is a per-update cost, not a design gap.
- The coordinator diagnostic polish (surface a "build drift — see RECOVERY"
  reason instead of the bare mismatch code) is deliberately NOT in this
  commit; the gates already fail closed, so it is a UX improvement only.

## Recommended next steps

1. Drill the playbook once end-to-end when the first real game update lands;
   record how much actually moved (this decides whether the heavier
   auto-recovery lane is worth its cost).
2. Optional polish: map the coordinator's `decode_build_mismatch` failure to
   a "build drift" reason that points at `RECOVERY/README.md`.
3. Until then, treat `RECOVERY/` as the standing answer to "the game updated,
   what now?".