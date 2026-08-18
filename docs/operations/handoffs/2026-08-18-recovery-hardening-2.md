# Handoff: RECOVERY hardening pass 2 — fail-closed semantics, link/path gate, Pester suite (2026-08-18)

**Date:** 2026-08-18 (UTC)
**Status:** Complete — triage is fail-closed and deterministically tested; the
RECOVERY docs are now part of the offline gate
**Campaign:** build-drift readiness (follow-up to `2026-08-17-recovery-hardening.md`)

## What was done

1. **Fail-closed exit semantics (`RECOVERY/invoke-build-drift-triage.ps1`).**
   `drifted` (exit 1) now means exactly "tables readable, installed hash
   matches none". Anything that prevents a trustworthy comparison is a
   **failure (exit 3)**, not a verdict: a table that fails to parse
   (`read-error`) or no readable table at all (`no-readable-table`). README
   and playbook exit-code docs updated to match. This closes the earlier
   ambiguity where a corrupt table read like drift.

2. **Strict-mode robustness fix.** `Get-AnchorHops` accessed `$hop.note`
   unconditionally under `Set-StrictMode Latest`, so a hop without a `note`
   property (e.g. a minimal/synthetic table) crashed the whole triage with
   exit 3 before any report. Property reads are now strict-mode-safe
   (missing kind/value/note tolerated); the real `11.19.0.10` table still
   reports 8 fields / 8 anchors and exits 0.

3. **RECOVERY docs are now part of the offline gate.**
   `scripts/python/offline_check.py` gained `check_recovery_paths()`: every
   backticked, path-like token in `RECOVERY/*.md` must resolve (relative to
   the doc, then the repo root). The module's docs use code spans, not
   markdown links, so the existing link checker missed renamed/moved paths.
   Filters skip shell commands, API endpoints (`/discover/...`), placeholders
   (`<version>`, `*`), generated dirs (`.build/`, `.data/`, `.freebuff/`),
   and field names with slashes (`playerPositionX/Y/Z`). Three bare path
   references in the playbook were corrected to full repo paths so the check
   passes.

4. **Deterministic Pester suite for the triage contract.**
   `RECOVERY/invoke-build-drift-triage.Tests.ps1` (7 tests, synthetic — a
   fake exe + fake table in a temp dir, no game install, no CI dependency):
   exit 0 same-build, exit 1 drifted, exit 2 missing exe, exit 3 missing
   offset dir, exit 3 malformed table, anchor-hop extraction + schema.json
   exclusion, and report shape. Wired into `scripts/validate.ps1` via
   `scripts/invoke-build-drift-triage-tests.ps1` (Pester 3.4.0-compatible
   `Should Be` syntax, ASCII-only, PS 5.1).

## Validation

- Pester suite: 7/7 passed.
- Real triage: exit 0, `11.19.0.10` MATCH, 8 fields / 8 anchors.
- `offline_check.py`: exit 0 (links + new RECOVERY path check green).
- Full `scripts/validate.ps1`: PASS (includes the new Pester suite).

## Notes

- The triage script remains read-only and never mutates evidence.
- Exit-code contract is now pinned by tests, so a future refactor cannot
  silently regress it.

## Amendment (same day): tool inventory + Step 0 drills verified

- Every tool the playbook's route table cites exists and resolves:
  `ConfirmHealthFieldStores.java`, `FindVftableViaCol.java`,
  `FindVftableForType.java`, `DumpDescriptorVtables.java`,
  `TraceShellGunProducers.java` (all in `tools/ghidra-scripts/`),
  `scripts/invoke-replay-crosscheck.ps1` (`-GoldenVector` present),
  `tools/compute-exe-hash.ps1`, `scripts/python/offset_check.py`,
  `scripts/python/offline_check.py`, `scripts/validate.ps1`, and the CLI
  verbs `hp-diff` / `yaw-diff` (registered in
  `src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs`). No dead citations.
- Synthetic Step 0 drill (read-only, temp dirs, real tables untouched):
  a fake same-hash table -> exit 0 same-build with anchors extracted; a
  hypothetical `11.20.0.0` executable against the real `memory-offsets/`
  -> all tables DRIFT, exit 1, playbook pointer printed. The playbook's
  trigger path works end-to-end without touching evidence.

## Next steps

- Drill the playbook end-to-end when the first real game update lands.
- Otherwise `RECOVERY/` is the standing answer to "the game updated, what
  now?" — triage is now fail-closed in every mode, its contract is
  regression-tested, and Step 0 is verified against both outcomes and the
  real table set.