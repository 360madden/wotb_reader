# HUD/game startup recovery validation

**Date:** 2026-08-15 (UTC)

**Supersedes:** the pending-validation section of
`2026-08-15-hud-startup-failure-resolution.md`.

## Validation

- Overlay tests: **158 passed**, 0 failed, 0 skipped.
- GameIntegration tests: **352 passed**, **6 expected local-game skips**, 0 failed.
- Full `scripts/validate.ps1`: **passed**.
- Release solution build: **0 warnings, 0 errors**.
- Full suite: **1,280 passed**, **8 expected opt-in skips**, 0 failed.
- Formatting, architecture, repository/privacy scan, Codex policy checks,
  PowerShell hygiene, offline links/file-tree freshness, blocker/ledger
  consistency, and offset schema/chain validation: PASS.

## Serialized runtime evidence

- One overlay process was started after the tracking change.
- The pre-existing game process was not started, stopped, or modified by this
  check.
- The overlay emitted `hud.game_window.bounds_invalid` for the observed 160 x
  28 splash-sized candidate and was cleaned up successfully.
- No `hud.game_window.tracking_started` claim was made because a usable game
  surface was not present.
- The game process remained present after overlay cleanup.

No game install files, replay bytes, screenshots, raw game logs, or private
captures were written to the repository.
