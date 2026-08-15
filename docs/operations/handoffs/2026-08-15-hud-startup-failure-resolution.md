# HUD/game startup diagnosis and recovery

**Date:** 2026-08-15 (UTC)

**Status:** title/working-directory failure modes corrected; usable live game
surface remains owner-gated

## Diagnosis

The earlier HUD tracking gate used an exact window caption,
`World of Tanks Blitz`. The installed client that was observed during this
session exposed the caption `WoT Blitz` instead. A live `wotblitz.exe` process
therefore existed while the exact-caption probe reported no game window.

A bounded native-log check also observed one earlier startup path ending in the
allowlisted `wgcBridge` preparation failure (`80ee0005`). No raw log, path,
account data, or private capture was retained. The installed executable itself
was present and reported product version `11.19.0.10`.

The currently observed game process was owned by the normal Wargaming launcher,
but its visible candidate bounds were only 160 x 28. That is a splash/invalid
surface, not evidence that a usable game client is ready for HUD alignment.

## Recovery changes

- `Win32GameWindowTracker` now discovers the visible top-level window through
  the `wotblitz.exe` process identity rather than a localized caption.
- Zero or multiple eligible game windows fail closed; the tracker never picks
  an arbitrary window.
- Minimized or splash-sized bounds below 320 x 200 are rejected until a usable
  game surface exists.
- The plain `IGameProcessLauncher` sets the executable directory as the process
  working directory. This allows the installed client's relative WGC/native
  dependencies to resolve when the host was started from another directory.
- HUD presentation version is now `0.6.0-alpha`.

## Serialized verification

One overlay was started after the code change while the pre-existing game
process was left untouched. The overlay remained alive and emitted the safe
`hud.game_window.bounds_invalid` event for the 160 x 28 candidate. The overlay
was then stopped by its own test cleanup; the game process remained running.
This proves the caption-independent process discovery and the new fail-closed
small-window guard, but it does not claim real alignment.

## Validation

- Overlay tests: 158 passed.
- GameIntegration tests: 352 passed, 6 expected local-game skips.
- Release solution build: 0 warnings, 0 errors.
- `dotnet format --verify-no-changes`: passed.
- No game install files were modified.

## Remaining gate

The owner must leave World of Tanks Blitz at a normal usable game surface
through the normal launcher flow, then run one serialized HUD check. The HUD
should reach `Game window: aligned` and record `hud.game_window.tracking_started`
with real client dimensions. Fullscreen/borderless placement, DPI behavior,
and bright/dark scene readability remain unverified until that gate.
