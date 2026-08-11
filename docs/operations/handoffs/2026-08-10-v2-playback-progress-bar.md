# Handoff — 2026-08-10: playback progress bar on the W2S overlay

**Status:** done, committed, pushed. Gate green.

## What and why

The session panel already had a full timeline scrubber (slider + play/pause +
speed), but the game overlay itself gave no indication of where the replay
was in the battle. A thin progress bar on the overlay closes that: during
playback you can see battle progress and current time without looking at the
control panel.

## The change

- `W2sHudView.Render` gained `playbackProgress` (0..1, null = no session/
  unknown duration) and `playbackLabel` ("m:ss / m:ss"); `BuildPlaybackBar`
  draws a bottom-centre track (320 px, clamped to the viewport), a white fill
  scaled by progress, and the time label above the left end.
- `MainWindow.RenderW2sHud` computes `progress = clamp(CurrentTimeSeconds /
  Duration)` and the label from the view model's existing playback state.
- New pure helpers: `PlaybackFillWidth(trackWidth, progress)` (clamped) and
  `FormatPlaybackLabel(current, total)` (null when duration unknown), plus
  `FormatClock` (m:ss).

## Verification

- New tests: fill scaling + clamping (0/0.5/1/1.5/-0.5), label formatting
  ("0:47 / 4:12", "10:00 / 10:00"), null for unknown/negative/non-finite
  durations. Overlay suite 92/92; full suite 12 projects, ~871 tests,
  0 warnings, green.
- The bar only appears when a session with a known duration is selected
  (progress null otherwise) — fail-closed like every other HUD element.
- `scripts/python/offline_check.py`: links, blocker numbering, ledger OK.

## Notes for next

- The bar is purely visual; seeking still happens via the session panel's
  slider. If in-game scrubbing is wanted later, the slider pattern already
  exists and the frame endpoint already serves arbitrary `timeSeconds`, so
  the overlay just needs pointer input wiring.
- Bar width/height/colors are single constants in `BuildPlaybackBar`; it
  sits between the kill feed (bottom-left) and minimap (bottom-right).
