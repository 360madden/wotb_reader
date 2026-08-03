# Handoff: OD-030 docs propagation + campaign commit (2026-08-03)

**Date:** 2026-08-03
**Status:** Complete — documentation propagated to live docs; full OD-018..030 campaign committed and pushed (`92321bb`)
**Campaign:** WoT Blitz PC offset discovery (`playerYaw` hypothesis quarantined; offset remains 0)

## Repository state

- **Branch:** `main`
- **Head:** `92321bb` — `docs(ops): record OD-RECOVERY-018..030; harden rolling driver with 401 refresh and probe-fold` (23 files, +2585/−77)
- **Remote:** `origin` (https://github.com/360madden/wotb_reader.git); `main` pushed, in sync (`9a545dd..92321bb`)
- **Working tree:** one phantom modification remains — `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md` shows as modified but its blob hash equals the index (`ec5f0952…`); it is a CRLF/LF line-ending artifact with no content change and was intentionally left uncommitted.

## What was done

1. **OD-030 facts propagated to all live (non-append-only) docs:**
   - `docs/operations/offset-discovery-workflow.md` — header `Last updated` → OD-030; canonical rolling recipe now documents the folded steady-state gate (round-1 `previousCount` == snapshot count, no separate 66M-walk probe) and the 401 capability-rotation refresh + retry.
   - `docs/operations/offset-discovery-guide.md` — `Last updated` → 2026-08-03 with campaign-status pointer.
   - `docs/ROADMAP.md`, `research/README.md`, `research/complete-reference.md` — fixed stale "`playerYaw` is a static-analysis `Candidate`" wording → "has static-analysis provenance but is quarantined/Stale".
   - `docs/operations/offset-discovery-ledger.md` — already carried the full OD-030 record (header, index row, YAML, decision register → OD-031); verified consistent.
2. **Committed + pushed the campaign trail:** handoffs 018–030, ledger records, workflow next-session protocol → OD-031, session driver scripts (`od-018-session.ps1`, `od-query-state.ps1`), CE autorun write-BP script (`od-autorun-writebp.lua`), tmpwotb-e2e helpers, and the two rolling-driver fixes (401 refresh + probe-fold).
3. Historical handoffs (018–029) and old YAML records left untouched (append-only per repo rules).

## Validation commands and results

```powershell
python scripts/python/offline_check.py
# Checked 21 files, 85 links, 0 broken.
# Blocker numbering OK: BLK-0001..BLK-0025 contiguous across 3 record file(s).
# Ledger consistency OK: 17 result section(s), 31 index row(s).

powershell -NoProfile -Command '...ParseFile("scripts/roll-replay-time-increased.ps1"...) '
# PARSE_OK (both driver edits, incl. reviewer refinements)
```

## Assumptions and unknowns

- The 014-partial.md phantom modification is line-ending noise only (hash identical to index); no content change was lost.
- Offset remains 0; no field promoted; `independentReplays` still 0 (BLK-0019 open).
- No operator Find-what-writes result yet (operator-owned step; OD-031 targets the staged-survivor window).

## Integration risks / operational notes

- Next session runs the 401-refresh + folded-gate driver (commit `92321bb`). The 120s lease wall after round-1's 66M walk remains the binding constraint on this machine (OD-030: closest 391 survivors; OD-026..029 converged at 8 rounds under lighter load).
- Launcher-green + immediate `Denied`/`evidence.monitor_unhealthy` on ~2/6 launches is a game-side assert crash at replay start (`AccountController.cpp:386`); relaunch rather than re-clicking.

## Recommended next steps

1. **OD-RECOVERY-031 (120s research-lease timebox):** run the proven pipeline with the 401-refresh + folded-gate driver; roll to ≤10, stage CE addresses, and hold the lease-bound green window for operator Find-what-writes on the staged survivors.
2. If the round-1 66M walk keeps eating the lease, consider a bounded `MaxBytes` snapshot budget or running when the machine is quiescent.
3. Import a content-distinct second `.wotbreplay` to close BLK-0019 (`independentReplays`).
