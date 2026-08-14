# Launcher completion-marker pre-flight ordering — already implemented; now pinned

**Date:** 2026-08-14 (UTC)
**Roadmap:** OD-099 replay-lifecycle hardening
**Base commit:** `ceced00` (`fix(overlay): resolve numeric arena minimap textures`)

## Result

The top-10 launcher pre-flight reorder was a stale action, not missing code.
Git blame and the current source show that commit `f633cdf0` placed
`Test-OdReplayCompleted` before CLI discovery and the replay-version probe on
2026-08-12. A matching completion marker therefore exits with
`FAILED_replay_already_completed` before any install/version probe or game
launch.

No production launcher behavior changed in this unit. The existing order is
now pinned in the completion-marker Pester suite so a future edit cannot move
the native CLI probe ahead of the persisted marker unnoticed.

## Validation

- Completion-marker Pester suite: 8/8 passed under Windows PowerShell 5.1.
- Full `scripts/validate.ps1`: 1,205 tests passed with 7 opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer, Pester
  8/8 + 16/16, offline pack, blocker/ledger consistency, and 8-chain schema
  validation passed on the final staged state.

## Next

No independent offline top-10 item remains. Items 1–4 share one approved live
launch; item 5 and the PN ship review are owner-gated; the remaining discovery
items require a live launch. Minimap terrain alignment additionally needs a
decoded replay for a texture-bearing map because the two current ground-truth
maps ship without textures in this exact install.
