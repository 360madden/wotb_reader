# Handoff — batch rehearsal bug-hunt: verdict + array-collapse fixes (2026-08-11)

## Summary

Adversarial review pass over the shipped rehearsal tooling found and fixed
two real defects, both in the fail-closed path:

1. **Cross-check verdict silently passed on unreadable dumps.** A `Resolved`
   entity whose region dump was too short to decode printed a `FAIL` line
   mid-table but was counted as *skipped*, not as a miss — so with every
   other pair matching, the verdict returned PASS with a contradictory FAIL
   line. Fixed: a `Resolved` entity WITH decoded ground truth always counts
   against the verdict, and an unreadable dump is an automatic MISS
   (verified: clean 4/4 PASS, truncated 3/4 + `MISS … unreadable dump`,
   exit 1).
2. **PS 5.1 single-element array collapse in the driver.** `Invoke-RestMethod`
   (via 5.1 `ConvertFrom-Json`) collapses single-element JSON arrays to
   scalars; a 1-entity roster would have serialized `entities` back as an
   OBJECT, and the python cross-check would have crashed iterating a dict.
   Fixed with `@($response.regions)` (the repo's documented PS 5.1 gotcha
   class). Also added a fail-closed guard: a missing replay-time label
   despite the clock attestation now throws instead of silently casting to
   0.0.

## Regression pins

- `--self-test` now includes a verdict-level check on an in-memory SQLite DB:
  matching dumps → exit 0; a truncated dump with ground truth → exit 1.
  Self-test PASS.
- Single-entity dumps file → python compare exit 0 (array-forcing verified).
- Driver parse OK; exit paths 0/1/3 re-verified; PSScriptAnalyzer 0 findings
  on the driver.

## Files touched

- `scripts/python/batch-rehearsal-crosscheck.py` (verdict counting +
  self-test compare mode)
- `scripts/invoke-batch-rehearsal.ps1` (array forcing + null-label guard)

## Verification

- Self-test PASS; clean/truncated/single-entity real-data runs verified;
  full `scripts/validate.ps1` gate green (936 passed, 3 local opt-in skips,
  0 warnings, 0 errors).

## Remaining

- Unchanged: the OD-RECOVERY-086 batch rehearsal session (one approved live
  launch) is the next gate; the rehearsal tooling now fails closed exactly
  where it should.
