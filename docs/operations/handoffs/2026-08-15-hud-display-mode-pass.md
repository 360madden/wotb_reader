# HUD display-mode pass

**Date:** 2026-08-15 (UTC)

**Status:** geometry and DPI tracking pass; scene-contrast review remains open

## Serialized test

The existing Wargaming-launched game process was used and was not started or
stopped. One overlay was started and cleaned up sequentially for each geometry
mode. The game window was restored to its original state after the pass.

The active display work area reported 2560 x 1392 at 96 DPI. The HUD emitted a
successful `hud.game_window.tracking_started` event in every mode:

| Mode | Actual game bounds | HUD tracked bounds | DPI | Result |
|---|---:|---:|---:|---|
| Normal | 640 x 360 | 640 x 360 | 96 | PASS |
| Large | 1280 x 720 | 1280 x 720 | 96 | PASS |
| Work-area geometry | 2560 x 1392 | 2560 x 1392 | 96 | PASS |

The work-area case verifies large/borderless-style geometry handling; it does
not claim that the game's own fullscreen rendering mode was entered.

## Cleanup and privacy

- Original game-window state restored: **PASS**.
- Game process remained alive: **PASS**.
- Overlay processes remaining after cleanup: **0**.
- No game install files, replay bytes, screenshots, raw logs, or private
  captures were written to the repository.

## Remaining visual gate

Geometry and active-DPI tracking are now live-verified. A separate owner-
supervised visual review is still needed for actual fullscreen mode, a second
DPI scale/display if available, and readability over bright and dark game
scenes. The HUD's diagnostics and fail-closed states remain covered by the
existing offline and runtime smoke evidence.
