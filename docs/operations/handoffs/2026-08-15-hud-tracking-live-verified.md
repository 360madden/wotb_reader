# HUD tracking live verification

**Date:** 2026-08-15 (UTC)

**Status:** serialized tracking alignment verified at a usable game-window size

## Test

The existing Wargaming-launched `wotblitz.exe` process was inspected and was
not started or stopped. Its actual caption was `WoT Blitz`, and its initial
surface was minimized/splash-sized at 160 x 28.

For this owner-requested live check, the existing window was temporarily
restored and resized to 640 x 360 without changing game files or starting a
battle. One Release HUD overlay was then started for the tracking interval.

The overlay recorded:

```text
hud.game_window.tracking_started width=640 height=360
```

The overlay was stopped by test cleanup. The original game-window bounds were
restored and the game process remained alive afterward.

## Result

- Caption-independent process discovery: **PASS**.
- Restore from minimized/splash surface: **PASS**.
- HUD geometry alignment at 640 x 360: **PASS**.
- Overlay cleanup without stopping the game: **PASS**.
- Fullscreen/borderless placement across monitors: **not verified**.
- Multi-DPI and bright/dark-scene readability: **not verified**.

No game install files, replay bytes, screenshots, raw logs, or private captures
were written to the repository.
