# Handoff: V4 — god-view replay minimap

**Date:** 2026-08-10 · **Status:** Complete (offline) · **Scope:** overlay
HUD + frame API; no live memory work

## What shipped

A replay-mode minimap panel for the W2S HUD. Pure god-view: every roster
tank with a position sample appears — dead or alive, in or out of the
camera's viewport — which is the replay default V3 established (no
spotting data exists in replays, so the whole battle is visible).

## Plumbing

- **API**: `ProjectedTank` and `OverlayTankResponse` now carry `WorldX` /
  `WorldZ` (the tank's nearest-sample replay-raw position, camera
  independent). CLI `overlay-frame` output includes them.
- **View model**: `MinimapMath.Normalize(worldX, worldZ, minX, maxX,
  minZ, maxZ)` maps a world position to normalized 0..1 panel coordinates
  (fail-closed on degenerate boundaries). `MainViewModel.BuildMinimap`
  rebuilds `MinimapItems` each frame from the frame's tanks against the
  session's map boundary (`WorldMinX/…`, already loaded per session).
  `MinimapCameraX/Z` carry the viewpoint marker position.
- **HUD**: `W2sHudView.Render` gained a minimap argument; `BuildMinimap`
  draws a 150px panel pinned bottom-right — team-colored dots (blue/red,
  grey when destroyed) + a white ring at the camera. Panel is skipped when
  no tank normalizes (no boundary → fail-closed).

## Tests (all green)

- `MinimapMathTests` (3): extent→0..1 mapping, out-of-boundary linear
  continuation, degenerate-boundary null.
- `W2sHudViewTests` +2: dot rect centred on normalized position, corner
  placement.
- `OverlayFrameProjectorTests` +1: world coords survive projection even
  when the screen projection is null (behind camera).
- `ReadApiEndpointsTests`: world coords asserted through the frame endpoint
  for both in-front and behind-camera tanks.
- `MainViewModelTests` +1: god-view minimap — all three tanks appear
  (including the distance-0 self tank and a behind-camera wreck), normalized
  across the seeded boundary, camera marker set.
- Full solution: 0 warnings, all 13 test projects pass (~900 tests), gate
  green.

## Real-data verification (web host, `.data` DB)

savanna frame at t=190: 14 tanks all carry world coords; normalized
against the map-11 boundary (x[−254,198] z[−248,186]) the dots spread
sensibly (e.g. 3760565 → (0.42, 0.40) team 1 alive; 3760567 → (0.40,
0.75) team 2 wreck).

## Notes

- The HUD's own minimap image (loaded via `LoadMinimapAsync`/minimap
  cache) is the map *texture*; the new panel is the live dot layer. They
  are independent today — aligning the texture under the dot panel is a
  natural follow-up.
- Beacons are not yet on the minimap (they'd need world coords in the
  frame response) — tracked as a follow-up.
