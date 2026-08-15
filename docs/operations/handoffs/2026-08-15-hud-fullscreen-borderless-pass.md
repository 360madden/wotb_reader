# HUD fullscreen/borderless tracking pass

**Date:** 2026-08-15 (UTC)

**Status:** maximized and borderless geometry verified; exclusive fullscreen and scene contrast remain open

## Serialized test

The existing Wargaming-launched game process was used and was not started or
stopped. Its original placement and window style were captured before the
check. Two overlay probes ran sequentially:

| Mode | Actual game bounds | HUD tracked bounds | DPI | Result |
|---|---:|---:|---:|---|
| Maximized window | 2576 x 1408 | 2576 x 1408 | 96 | PASS |
| Borderless work-area geometry | 2560 x 1392 | 2560 x 1392 | 96 | PASS |

The borderless probe temporarily removed the standard window frame and placed
the game surface over the active work area. The original style and placement
were restored in the `finally` cleanup path.

## Cleanup and privacy

- Original game-window placement/style restored: **PASS**.
- Game process remained alive: **PASS**.
- Overlay processes remaining after cleanup: **0**.
- No game install files, replay bytes, screenshots, raw logs, or private
  captures were written to the repository.

## Result boundary

- Maximized HUD tracking: **verified**.
- Borderless work-area HUD tracking: **verified**.
- Exclusive/game-engine fullscreen mode: **not entered or claimed**.
- Multi-DPI behavior beyond the active 96 DPI: **not verified**.
- Bright/dark scene readability: **not verified**.

A visual scene review remains the final HUD presentation gate. The geometry
probe proves bounds propagation and safe restoration, not contrast or text
legibility over rendered game content.
