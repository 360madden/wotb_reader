# Handoff: full-repo documentation staleness sweep (2026-08-17)

**Date:** 2026-08-17
**Status:** Complete — 2026-08-17 penetration findings propagated to the
immutable/authoritative records; stale index ranges corrected; committed
`ac81328`
**Campaign:** penetration v0.3 (BLK-0027) + operations documentation hygiene

## Repository state

- **Branch:** `main`
- **Head:** `ac81328` — `docs(ops): propagate 2026-08-17 penetration findings and fix stale indexes`
- **Working tree:** clean except the pre-existing phantom modification —
  `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md` (CRLF/LF
  line-ending artifact, no content change) — intentionally left uncommitted.

## What was done

Audited the documentation surface (tracking docs, index/READMEs, roadmap
family, ledger, blocker log, promotion checklist) for staleness and
cross-checked it against git history. Two classes of fix landed:

1. **2026-08-17 penetration findings propagated to the authoritative records** (they
   had only reached the proof-protocol, next-10-actions, and handoffs):
   - `docs/operations/blocker-log.md` — appended three BLK-0027 entries: the
     `VehicleGun +0x38/+0x3C/+0x40` field-trace refutation, the gun-descriptor
     producer trace (no equip-time write; config lives in `Gun`/`Shell`/
     `VehicleDescr`), and the Gun/Shell descriptor field layout (including the
     honest "`piercingPower` destination offset UNCONFIRMED" note).
   - `docs/operations/offset-discovery-ledger.md` — prepended the 2026-08-17
     static-pass results to the *Next planned session* register and advanced
     NEXT to the runtime shell-index link + `piercingPower` destination.
   - `docs/operations/penetration-v0.3-plan.md` — appended the H1 live-validation
     result (2026-08-16) and the shell/gun semantic-field static derivation
     (2026-08-17) to Execution status, which had still said the live protocol
     "remains to be run."

2. **Stale index facts corrected:**
   - `README.md` — blocker register range `BLK-0001 … BLK-0026` → `… BLK-0027`.
   - `docs/operations/README.md` — two ranges `BLK-0001…0025` /
     `BLK-0012…0025` → `0027`.
   - `docs/operations/offset-discovery-roadmap.md` — added a superseding STATUS
     note (2026-08-17): `playerHP`/`playerYaw`/pitch-roll/`damageDealt` are now
     published; only `replayTime`/velocity/`cameraPitch`/`aliveTankCount` remain
     unpublished.

## Validation commands and results

```powershell
python scripts/python/offline_check.py
# Checked 71 files, 120 links, 0 broken.
# Blocker numbering OK: BLK-0001..BLK-0027 contiguous across 3 record file(s).
# Ledger consistency OK: 69 result section(s), 96 index row(s).
# exit 0
```

## Assumptions and unknowns

- Immutable history (handoffs, older ledger result sections) left untouched per
  append-only rules; the 2026-08-17 facts are appended, not rewritten.
- Nothing was promoted: the Gun/Shell field layout is producer-side static
  evidence only; the runtime shell-index link and the `piercingPower`
  destination offset remain open (BLK-0027).
- `offset-promotion-checklist.md` and `knowledge.md` were checked and are
  already accurate (8 fields `Verified`; the unpublished list is correct) — no
  change.

## Integration risks / operational notes

- `AGENTS.md` "Last verified" (2026-08-14, 1,206 tests) was deliberately NOT
  refreshed: it is a full-gate snapshot and re-running `scripts/validate.ps1`
  was out of scope. It will drift until the next full milestone gate.
- `offset-discovery-roadmap.md` is the legacy research-track doc superseded by
  `product-roadmap.md`; its STATUS note is now only a pointer, not a parallel
  plan.

## Recommended next steps

1. Resolve the runtime shell-index link (which `Shell`/`Shot` loads at fire
   time) and the `piercingPower` destination offset — shot-path consumer trace
   or the live controlled shell-swap.
2. Run the full milestone gate and refresh the `AGENTS.md` last-verified
   snapshot (test count + date).
