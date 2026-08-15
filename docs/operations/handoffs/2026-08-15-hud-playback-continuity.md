# HUD UI replay playback continuity

**Date:** 2026-08-15 (UTC)

**Status:** unnecessary replay-detail polling removed; playback continuity hardening validated offline

## Problem

The HUD's two-second refresh timer called `RefreshSelectedAsync` for the
selected replay. That detail load reset the timeline to the session duration
and set `IsPlaying=false`, so normal playback could be interrupted every two
seconds while also repeating immutable HTTP/SQLite detail work.

## Change

- The timer now refreshes only render-health bindings such as frame age and
  rendered collection counts.
- Decoded replay details are no longer polled in the background because they
  are immutable after import.
- Manual Refresh and SignalR session-list updates remain the explicit refresh
  paths and still reconcile a changed or removed selected session.
- Added a regression test proving the render-health refresh does not mutate
  runtime state, timeline position, or playback state.

No API contract, storage contract, or live-memory path changed.

## Validation

- Overlay tests: **157 passed**.
- Release build and full validation are the remaining gate after this handoff
  and offline-index refresh.
- No live game process was launched.

## Expected result

Once a replay is selected, playback can advance continuously without the
background diagnostics timer resetting it to the end. Frame requests continue
at the playback tick, while session details are loaded only when explicitly
needed.
