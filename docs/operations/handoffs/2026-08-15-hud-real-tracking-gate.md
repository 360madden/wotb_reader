# HUD UI real game-window tracking gate

**Date:** 2026-08-15 (UTC)

**Status:** blocked by game startup; HUD tracking code remains offline-verified

## Serialized attempt

This was the final single-process live attempt after all offline HUD work and
post-change overlay smoke checks had passed. No parallel HUD/game work was
started.

- The known local `wotblitz.exe` installation was present.
- One normal process start was attempted with no replay or memory arguments.
- The process exited before presenting a window titled `World of Tanks Blitz`.
- A bounded wait found no target window and no remaining game-related process.
- No relevant Windows Application Error record was available in the bounded
  check.
- No host, overlay, scanner, or memory session was started for this failed
  launch attempt, so no live evidence was claimed.

## Result

**Real game-window alignment: NOT VERIFIED.** The blocker is now game startup,
not the HUD tracker: the target executable must remain alive and expose its
window before `FindWindowW` can reach the production `Tracking` state.

The post-change no-game overlay smoke already verified the safe fallback state,
rate-limited `hud.game_window.not_found` logging, frame/render health display,
and diagnostics-export action. Those results remain valid and are recorded in
`2026-08-15-hud-diagnostics-tooling-validation.md`.

## Required owner action

Start World of Tanks Blitz through the user's normal launcher/platform flow,
leave the game window visible, and then run one serialized HUD check. Do not
promote the HUD alignment gate from this attempt; the executable exit is an
honest external blocker.
