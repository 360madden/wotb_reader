# HUD UI runtime state and diagnostics

**Date:** 2026-08-15 (UTC)

**Status:** explicit fail-closed runtime state and visible diagnostics banner implemented; live/visual smoke testing remains open

## Repository state

- Branch: `main`, one commit ahead of `origin/main`.
- HUD UI version: `0.2.0-alpha`.
- Product baseline remains `0.2.0-alpha`; penetration feature remains `v0.3`.
- No shared API contract or project-reference change was made.
- Existing logging, Pen UI, versioning, older handoff, and agent-skill dirty paths were preserved.

## Implemented

- Added `HudRuntimeState` to the overlay view model with explicit states for:
  startup, host discovery, stale/invalid host records, host failure, no
  sessions, no selection, session/frame loading, replay ready/paused/playing,
  replay-unavailable, stale-frame retention, live loading/ready/unavailable,
  and fatal startup failure.
- Added safe presentation properties for state label, detail, severity accent,
  and frame status. The existing free-form `Status` remains available for
  session/host summaries and compatibility.
- Added a visible sidebar diagnostics banner. It shows the current lifecycle
  state, safe explanatory detail, and whether the HUD has no frame, a current
  frame, or a retained last-good frame.
- Failed replay frame requests now enter `FrameStale` when a last-good frame is
  retained, rather than silently presenting old telemetry as current. A first
  failed replay frame enters `ReplayUnavailable`; failed live frames enter
  `LiveUnavailable` and clear the penetration verdict.
- Non-finite replay frame times are rejected before rendering.
- Startup exception handling enters the visible `FatalError` state using only
  the exception type.
- Bumped the independent HUD presentation version from `0.1.0-alpha` to
  `0.2.0-alpha` because the diagnostics banner is an additive visible surface.

## Validation

- Overlay tests: **145 passed**.
- Release solution build: **0 warnings, 0 errors**.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: PASS.
- `git diff --check`: PASS.
- No live game launch, live capture, or visual smoke test was run.

## Remaining

1. Run one supervised Windows visual smoke test to confirm the banner is
   readable over bright/dark game scenes and that tracking/DPI behavior remain
   correct.
2. Keep colored penetration results blocked by BLK-0027 until exact inputs and
   the representative validation corpus are proven.
3. Continue the HUD roadmap with replay readability/performance polish only
   after the visual smoke gate is recorded.
