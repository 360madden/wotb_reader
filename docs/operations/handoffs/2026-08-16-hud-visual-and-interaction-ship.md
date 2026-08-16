# HUD visual and interaction ship (v0.7.0-alpha)

**Date:** 2026-08-16 (UTC)
**Status:** code-complete and merged; live owner-supervised visual ship review remains
**HEAD:** `ab6c493 feat(overlay): pulse low-HP nameplates as killable-target cues`
**Blocker:** none new (penetration `BLK-0027` unaffected)

## Project-management decision

The HUD reached the "beautiful in form and function" milestone through one
design language instead of isolated widgets: a frozen glass palette plus team
accents shared by the in-game overlay and the sidebar, and a single
frame-driven animation model that survives the clear-and-rebuild render path.
Interaction was added surgically — one hit-testable scrub band — so the
overlay's drag-to-move and click-through behavior is unchanged.

## Implemented

- One visual language across `W2sHudView` and `MainWindow`: glass nameplates
  with team accent strips and gradient HP fills, rounded minimap with vignette
  and camera cone, framed kill feed/scoreboard/pen badge/playback bar, shared
  dark slider/ComboBox/toolbar styles, and matching team colors in the
  converter.
- Frame-driven animation system (no timers, no WPF animation objects):
  combat-pip rise-and-fade, HP damage-ghost trail, kill-feed slide-in,
  pen-badge verdict pulse, and low-HP killable-target edge pulse. All curves
  are pure, finite-safe, and unit-tested.
- In-HUD scrubbable playback bar: a persistent hit band over the track maps
  pointer position to a clamped timeline fraction via `ScrubToFraction`; the
  per-frame canvas remains non-hit-testable so clicks elsewhere still reach
  the window.
- 20-round hardening pass: NaN/Infinity fail-closed sanitation for
  projections, HP, distance, depth, minimap bounds and camera; HP-bar fill
  geometry fix; frame-request timeout that distinguishes timeout from
  supersede; render-storm coalescing; critical-HP pulse activation; collapse
  animation generation guard; plot decimation ceiling; null-safe stats parse.
- Six-track follow-up: sanitization regression tests, pure marker-color
  contract, frame-staleness watchdog (paused-replay exempt), label edge cases,
  privacy audit (verified already covered, not duplicated), full gate.
- HUD UI version bumped to `0.7.0-alpha`.

## Deliberate non-actions

- No change to the overlay's window style, topmost behavior, or drag-to-move
  contract beyond the single scrub band.
- No timers, storyboards, or per-frame WPF animation objects were introduced;
  animation stays in pure, testable per-frame math.
- No game install, replay bytes, private paths, tokens, or raw memory data
  were touched or committed.

## Validation

- Overlay suite: **191 passed**, 0 failed.
- Full solution test run: **1,328 passed**, **8 expected opt-in skips**,
  0 failed. (One pre-existing SQLitePCL disposal flake in `Host.Cli.Tests`
  appeared once under parallel test execution and passed on re-run; it is
  unrelated to the Overlay-only changes.)
- Release build: **0 warnings, 0 errors**.
- Full `scripts/validate.ps1` gate: locked restore, formatting, build, tests,
  repository/privacy scan, Codex agent-policy, PowerShell hygiene, offline
  links, ledger/blocker, and offset schema/chains checks all passed.

## Next decision

The code ship is done; what remains is the owner-supervised live visual pass
already scheduled as next-10 action 10 — exclusive fullscreen, a second DPI
scale, scene contrast, and penetration fidelity across real scenes. That pass
is offline-blocked and unchanged by this workstream; the penetration release
still gates on items 1-6.
