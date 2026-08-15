# HUD UI logging

**Date:** 2026-08-15 (UTC)

**Status:** structured local logging implemented; visual smoke test remains open

## Repository state

- Branch: `main`, one commit ahead of `origin/main`.
- Product baseline: `0.2.0-alpha`.
- HUD UI baseline: `0.1.0-alpha`.
- No API contract or project-reference change was made.
- Existing unrelated dirty paths remain untouched: the older modified handoff
  and untracked `.agents/skills/autorun/` directory.

## Implemented

- Added a standalone overlay-owned `IHudLogger` seam and JSON-lines file sink.
- Production startup writes to the per-user WotB Treader logs directory using
  `hud-YYYYMMDD.jsonl`, a 20 MiB rolling limit, and 14 retained files.
- Logged process/window lifecycle, host/rendezvous state, session/detail loads,
  frame outcomes, SignalR connection lifecycle, memory-observation liveness,
  minimap failures, dashboard actions, and unhandled exceptions.
- Replaced overlay `Debug.WriteLine` diagnostics with the structured logger.
- High-volume frame, memory, stream, and polling events are rate-limited to
  one record per five seconds per event name; suppressed records are reported
  in the next admitted record.
- Exception messages/stacks, tokens, paths, URLs, session/account/artifact
  identifiers, raw telemetry values, and arbitrary object graphs are excluded.
  Sensitive property names are redacted defensively.
- Logging is fail-open: an unavailable log directory disables the sink without
  preventing the HUD from starting or rendering.

## Validation

- WPF overlay tests: **142 passed**.
- Release solution build: **0 warnings, 0 errors**.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.

## Remaining

1. Run a supervised Windows visual smoke test and confirm the HUD writes a
   startup record while it tracks the game window.
2. Keep the logger outside `ApiContracts` and `Bootstrap`; it is a local HUD
   diagnostic surface, not a wire contract.
3. Keep colored penetration results blocked by BLK-0027 until exact inputs and
   the representative validation corpus are proven.
