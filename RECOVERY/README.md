# RECOVERY — build-drift module

**Purpose:** the single place the repo goes when the installed game build
changes. Every published offset chain and every live/discovery gate is
hash-bound to one executable (`11.19.0.10` = `1cda5c31...`, recorded in
`memory-offsets/11.19.0.10.json`). The coordinator and launcher fail closed
on a build mismatch by design — safe, but it leaves no recovery path unless
one exists beforehand. This module is that path.

**Trigger:** any of

- the game updates (new version / new executable),
- a managed launch fails with `capture.decode_build_mismatch` (the
  `/discover/pen-capture` endpoint answers it with a `reason` pointing
  here),
- a live session refuses reads although the game is running.

**The one rule:** evidence-first. Nothing is migrated, copied, or estimated
across builds. Every field is re-verified against the new executable, and the
old table stays frozen as history (`memory-offsets/11.18.0.7.json` is the
precedent for per-version tables).

## What is here

| File | Purpose |
|---|---|
| `invoke-build-drift-triage.ps1` | Read-only triage: compares the installed `wotblitz.exe` against every versioned offset table and reports drift, published fields, and the module-relative anchor RVAs that must be re-derived. Never mutates evidence. Exit codes: 0 same build, 1 drifted (tables readable, no hash match), 2 exe not found, 3 failure (including unreadable or missing offset tables — fail-closed, not a verdict). |
| `invoke-build-drift-triage.Tests.ps1` | Deterministic Pester suite (7 tests, synthetic) pinning the exit-code contract, anchor extraction, and report shape; wired into `scripts/validate.ps1`. |
| `build-drift-recovery.md` | The playbook: freeze/ratchet, replay-format check, ordered per-field re-verification, re-publication. |

Reports land in `.build/reports/` (git-ignored), never in the committed
tree.

## Quickstart

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File RECOVERY\invoke-build-drift-triage.ps1
```

- Exit 0 → the installed build still matches; copy the report path if you
  need evidence.
- Exit 1 → build drifted; open `RECOVERY/build-drift-recovery.md` and follow
  it from Step 1. No live session or managed-launch evidence work before the
  playbook's re-verification is done for the fields you consume.
- Exit 2 → pass `-GameExePath` (same install discovery as
  `tools/compute-exe-hash.ps1`).
- Exit 3 → the comparison could not be made (unreadable/missing offset
  tables, or any other failure); treat it as "cannot assess", never as
  same-build.

## How it fits

- Reads `memory-offsets/*.json` (the versioned publication tables).
- Reuses `tools/compute-exe-hash.ps1` discovery and the same-bound Ghidra
  tooling under `tools/ghidra-scripts/` for per-field re-derivation.
- Re-verification follows the existing publication workflow
  (`docs/operations/offset-discovery-workflow.md` Phase 5) and the record
  conventions in `docs/operations/` (immutable history, dated amendments).
- The HUD needs no recovery itself: it renders what the host serves, and
  the host's gates are what fail closed.

## Deliberately not here

- No automatic offset migration or RVA-delta heuristics. That is the
  documented evidence anti-pattern; the first real update tells us how much
  actually moves before a heavier lane earns its cost.
- No bulk re-discovery. The playbook re-runs the existing hash-bound scripts
  per field and gates the live half on the usual owner-approved launches.