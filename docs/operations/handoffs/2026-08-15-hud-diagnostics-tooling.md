# HUD UI diagnostics tooling

**Date:** 2026-08-15 (UTC)

**Status:** ordered offline follow-ups implemented; real game-window smoke remains the final serialized gate

## Repository state

- Branch: `main`, one commit ahead of `origin/main`.
- HUD UI version: `0.5.0-alpha`.
- Product baseline remains `0.2.0-alpha`; penetration feature remains `v0.3`.
- No shared API contract, storage contract, or project-reference change was made.
- Existing dirty and staged paths were preserved.

## Ordered work completed

### 1. Deterministic tracking seam

- Extracted Win32 discovery and `SetWindowPos` into an injectable
  `IGameWindowTracker` implementation.
- Added a pure `GameWindowTrackingCoordinator` that maps probe, bounds, and
  alignment outcomes to the HUD's fail-closed tracking states.
- Added tests for not-found, unavailable/invalid bounds, successful alignment,
  repeat tracking, and reposition failure without launching the game.

### 2. Render-health telemetry

- Added wall-clock frame age using an injectable `TimeProvider`.
- Added last frame-request latency measured with `Stopwatch`.
- Added rendered nameplate, minimap-dot, and beacon counts.
- Displayed these values in the existing diagnostics banner and included only
  bounded latency/count fields in privacy-safe frame logs.

### 3. Privacy-safe diagnostics export

- Added an `ⓘ` toolbar action that writes a JSON diagnostics document through a
  save dialog.
- The export contains HUD state, session count, mode, frame/render health, and
  aggregated event names/counts/timestamps from at most 14 local HUD log files.
- It never copies raw log properties, replay data, paths, URLs, identifiers,
  credentials, or exception text.
- Added tests for sensitive-property exclusion and successful export when logs
  are missing.

## Validation at handoff creation

- Overlay tests: **156 passed**.
- Release solution build: **0 warnings, 0 errors**.
- Formatting verification: PASS.
- Full repository validation is the remaining gate after this documentation and
  offline-index refresh.
- No live game process or parallel live test was used.

## Remaining gate

Run one owner-supervised, serialized check with `World of Tanks Blitz` already
running. Confirm that the new injectable production tracker reaches `Tracking`,
the HUD bounds match the game under the active DPI/display mode, the render
health line remains readable, and the export action writes only the bounded
summary. Do not include the exported diagnostics file in the repository.
