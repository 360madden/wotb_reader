# HUD UI game-window alignment diagnostics

**Date:** 2026-08-15 (UTC)

**Status:** explicit alignment state and privacy-safe missing-window diagnostics implemented; real game-window tracking still requires one serialized owner-supervised run

## Repository state

- Branch: `main`, one commit ahead of `origin/main`.
- HUD UI version: `0.4.0-alpha`.
- Product baseline remains `0.2.0-alpha`; penetration feature remains `v0.3`.
- No shared API contract, storage contract, or project-reference change was made.
- Existing dirty and staged paths were preserved.

## Implemented

- Added the UI-only `HudGameWindowState` state set:
  - `NotFound`;
  - `Tracking`;
  - `BoundsUnavailable`;
  - `BoundsInvalid`; and
  - `RepositionFailed`.
- Added safe view-model properties for the current alignment state, boolean
  tracking status, user-facing label, and severity accent.
- Added a second diagnostics line to the sidebar banner. It explicitly tells
  the user whether the HUD is waiting for the target window, aligned, or unable
  to obtain/ apply valid bounds.
- Changed the window tracker to report `Tracking` only after both
  `GetWindowRect` and `SetWindowPos` succeed. A visible overlay is therefore
  not presented as aligned when positioning failed.
- Added rate-limited `hud.game_window.not_found` logging. Bounds and
  reposition failures are also covered by the HUD logger's game-window event
  rate limit, without recording window handles, titles, paths, or coordinates.
- Bumped the independent presentation version from `0.3.0-alpha` to
  `0.4.0-alpha` for the additive visible diagnostic surface.

## Validation

- Overlay tests: **148 passed**.
- Release solution build: **0 warnings, 0 errors**.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: PASS.
- Full repository validation after this handoff is the remaining gate for this
  work item.
- No live game process or parallel live test was used.

## Remaining unknown

The prior serialized smoke test confirmed the overlay and replay/empty states,
but the target `World of Tanks Blitz` window was absent. This change makes that
condition explicit; it does not claim real DPI, fullscreen, borderless, or
multi-monitor alignment. One owner-supervised serialized run with the game
window present remains required.

## Next step

Run the single game-window tracking smoke check, then continue with replay
readability/performance polish only after the alignment state is observed as
`Tracking` and the banner remains readable over real scenes.
