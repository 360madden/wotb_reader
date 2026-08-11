# Handoff — 2026-08-10: projection cache (frame latency 250 ms → 10 ms)

**Status:** done, committed, pushed. Gate green.

## What and why

The `/sessions/{id}/frame` endpoint reloaded the entire decode projection
(~580k position samples, events, raw records) from SQLite **on every frame
request** — measured ~250 ms/frame, far too slow for playback-speed HUD
rendering. The projection is immutable per session (every decode run creates a
fresh session id), so the reload was pure waste.

## The change

- `src/WotBTreader.Application/Replay/ProjectionCache.cs`: `IProjectionCache`
  + `ProjectionCache` — a bounded (default capacity 4) concurrent cache keyed
  by `BattleSessionId`, evicting least-recently-stored entries past capacity.
  Never inspects projection contents; fail-safe by construction.
- `ReplayFrameSource` consults the cache at the projection boundary
  (`TryGet` before loading, `Store` after a successful load). Registered in DI
  as a singleton; scoped `IOverlayFrameSource` instances share it.

## Verification

- Unit tests (`ProjectionCacheTests`): miss→store→hit, same-session refresh,
  over-capacity eviction (2 cap and 4-cap orderings). 57/57 Application tests.
- Full suite: 12 test projects, ~860 tests, 0 warnings, green.
- Real-data benchmark (web host on the `.data` DB, Oasis Palms
  `019fee20-9315-70b7-a92c-379f41f69532`): cold first frame 367 ms (loads the
  cache), warm frames **8–34 ms (avg ~10 ms)** — a ~25× improvement. Content
  intact: 14 tanks, 8 kills at t=250, death pip at t=190, world coords +
  `alive` flags all present.
- `scripts/python/offline_check.py`: all links resolve, blocker numbering
  contiguous, ledger consistent.

## Playback-speed rehearsal (2026-08-10, appended)

Full-battle walk of the Oasis Palms session against the live endpoint at the
HUD's real 20 fps tick (t step 0.05 s, 252 s battle → 5040 frames):

- **5040/5040 frames resolved, 0 failures.**
- Latency: p50 7 ms, p95 31 ms, p99 34 ms, max 39 ms.
- **0/5040 frames exceeded the 50 ms tick budget** — the HUD's
  fire-and-forget refresh now keeps up with playback by a wide margin.

This closes the loop on F5's "playback-speed HUD" unlock: the data path is
no longer the bottleneck.

## Notes for next

- The cold first frame per session (~370 ms) could be hidden by warming the
  cache for the most recent session at host startup, or by prefetching during
  the CLI `decode` command. Not done — not needed for the current UX.
- Capacity 4 comfortably covers the HUD's one-session-at-a-time pattern; bump
  only if the overlay ever scrubs multiple sessions concurrently.
