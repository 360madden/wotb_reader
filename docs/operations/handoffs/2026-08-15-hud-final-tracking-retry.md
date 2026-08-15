# HUD final tracking retry

**Date:** 2026-08-15 (UTC)

**Status:** tracking fix verified; usable game-window gate remains blocked

## Serialized retry

The current owner game process was inspected before the retry and remained
untouched. It was alive and responsive with the installed client's actual
caption `WoT Blitz`; no overlay process was running beforehand.

One Release overlay process was started for eight seconds. It emitted two
privacy-safe `hud.game_window.bounds_invalid` records and no
`hud.game_window.tracking_started` record. The overlay was stopped by the
smoke-test cleanup, and the game process remained alive afterward.

The new process-identity probe therefore works past the old exact-caption
failure, while the 160 x 28 splash-sized/minimized surface is correctly
rejected by the 320 x 200 readiness guard.

## Result

- Caption-independent game discovery: **verified**.
- Fail-closed invalid-surface handling: **verified**.
- Real usable-window alignment: **not verified**.
- DPI/fullscreen/borderless placement and bright/dark readability:
  **not verified**.

The owner must leave the game at a normal visible client surface through the
normal launcher flow before the final `hud.game_window.tracking_started` gate
can be claimed. No game install files, replay data, screenshots, or raw logs
were written to the repository.
