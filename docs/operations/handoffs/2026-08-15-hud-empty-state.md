# HUD UI replay-session empty state

**Date:** 2026-08-15 (UTC)

**Status:** replay-session empty and filter-miss states implemented; live/visual smoke testing remains open

## Repository state

- Branch: `main`, one commit ahead of `origin/main`.
- HUD UI version: `0.3.0-alpha`.
- Product baseline remains `0.2.0-alpha`; penetration feature remains `v0.3`.
- No shared API contract or project-reference change was made.
- Existing dirty and staged logging/runtime-state paths were preserved.

## Implemented

- Added view-model properties for whether the visible session list is empty,
  the empty-state title, and the next safe user action.
- Added a visible sidebar placeholder that distinguishes:
  - no decoded replay sessions: import a replay and refresh;
  - no rows matching the current map filter: clear the filter; and
  - an in-progress session refresh: wait for the local host.
- Empty-state properties raise notifications with session refreshes, filter
  changes, and runtime-state transitions, so the copy cannot become stale.
- Bumped the independent HUD presentation version from `0.2.0-alpha` to
  `0.3.0-alpha` for the additive visible surface.

## Validation

- Focused overlay tests and the full validation gate are pending for this
  handoff at creation time.
- No live game launch, live capture, or visual smoke test was run.

## Remaining

1. Run the focused and full offline validation gates.
2. Run one supervised Windows visual smoke test to confirm the placeholder and
   diagnostics banner remain readable over the game window.
3. Continue replay readability/performance work only after the visual gate.
