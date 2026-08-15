# HUD UI serialized Windows smoke test

**Date:** 2026-08-15 (UTC)

**Status:** replay and empty-session smoke paths passed; real game-window tracking remains unverified because the game window was not running

## Scope

This was one serialized Windows runtime check. No parallel HUD or game
processes were used, and no game install files, replay bytes, screenshots, or
private captures were stored.

The check used the Release host and overlay binaries with:

1. an isolated empty application-data root; and
2. the repository's existing offline data root containing decoded sessions.

The host was started on separate loopback ports for the two phases. The
rendezvous record was found by the overlay in both phases.

## Evidence captured

### Startup and host connection

- The WPF overlay process started and remained responsive.
- UI Automation found a visible, non-offscreen overlay window in both phases.
- The observed overlay bounds were 1200 x 700 in both phases.
- Privacy-safe HUD logs recorded `hud.process.starting`,
  `hud.window.created`, `hud.window.loaded`, `hud.host.connected`,
  `hud.process.started`, and `hud.stream.connected`.
- No overlay error or unhandled-exception records were emitted during the
  successful empty or replay phases.

### Empty-session state

The isolated host returned zero sessions. UI Automation observed all of the
following visible states/copy:

- `No sessions`
- `Import a replay to create a session`
- `Import a replay, then refresh this HUD.`
- `0 session(s)`
- `No frame received`
- `PEN - SESSION DATA UNAVAILABLE`
- `HUD UI v0.3.0-alpha`

The log recorded `hud.sessions.refresh_completed` with `sessionCount=0`.
This confirms the empty-state placeholder and diagnostics banner are visible
without a selected replay.

### Replay session and first frame

The repository data host returned 26 sessions. The first selected replay was
then changed to the synthetic-map fixture through the HUD's map filter so the
renderable fixture was exercised without exposing identifiers.

- The filtered list contained three sessions.
- UI Automation observed `synthetic-map`, `pilot-a`, and `unit-b` nameplates.
- The timeline displayed `2:00 / 2:00` and the state displayed `Replay paused`.
- The frame label displayed `Frame @ 120.0s`.
- The sidebar displayed `HUD UI v0.3.0-alpha`.
- Logs recorded `hud.session.selection_changed`, replay detail loading, and
  repeated `hud.frame.loaded` records with `mode=replay`.
- The first settled synthetic frame carried 2 tanks, 0 visible projected
  tanks, 0 beacons, 2 minimap tanks, and no penetration badge. This is
  fail-closed and consistent with the fixture's limited projection evidence.
- A real replay session also loaded successfully with 14 participants and a
  replay frame; its first frame had no visible projected tanks, followed by
  settled frames with one visible projected tank.

The minimap fallback path emitted `hud.minimap.unavailable` warnings because
no minimap texture was available for the exercised data. This did not stop the
HUD or frame rendering.

### Game-window tracking

The actual target window title, `World of Tanks Blitz`, was not present during
the check. A process/window scan returned `GAME_WINDOW_NOT_FOUND`, and the
fresh empty/replay phases therefore produced no new
`hud.game_window.tracking_started` record.

The overlay window's own visibility and bounds were verified, but this is not
proof of alignment to the game window. DPI, fullscreen/borderless positioning,
and topmost tracking remain an open visual gate.

## Cleanup and privacy

Only the host and overlay processes started for this smoke test were stopped.
The game was not launched, no live-memory path was exercised, and no game
install or private replay artifact was modified. The privacy-safe local HUD
log remains available for troubleshooting; no log contents were copied into
the repository.

## Validation

- Empty-session runtime: PASS.
- Replay-session runtime: PASS.
- Overlay visibility/accessibility tree: PASS.
- Host rendezvous and loopback connection: PASS.
- Real game-window tracking: **NOT VERIFIED**; target window absent.
- No source changes were made by the smoke run.

The prior HUD implementation gate remains valid: overlay tests, Release build,
format verification, and the full repository validation gate passed before
this runtime check.

## Next step

Run one owner-supervised, serialized check with World of Tanks Blitz already
running. Confirm the HUD logs `hud.game_window.tracking_started`, matches the
game client bounds under the active DPI/display mode, and remains readable over
both bright and dark scenes. Do not treat the current runtime evidence as a
penetration-accuracy promotion; BLK-0027 remains unchanged.
