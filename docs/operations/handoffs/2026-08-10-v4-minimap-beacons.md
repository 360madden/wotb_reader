# Handoff: beacons on the V4 god-view minimap

**Date:** 2026-08-10 · **Status:** Complete (offline) · **Scope:** overlay
HUD + frame API; no live memory work

## What shipped

The V4 minimap panel now draws beacons too: each world-anchored POI appears
as a small diamond in its own marker color **under** the tank dots, so
overlaps still read. Beacons are god-view like everything else on the
panel — they appear regardless of the camera, and only beacons inside their
replay-time window (the frame's existing visibility filter) are shown.

## Plumbing

- **Projection/API**: `ProjectedBeacon` and `OverlayBeaconResponse` gained
  `WorldX`/`WorldZ` (the beacon's world-anchored position); endpoint and
  CLI `overlay-frame` output include them.
- **View model**: `MinimapBeaconItem` (name, color, normalized 0..1
  coords); `BuildMinimap` normalizes the frame's visible beacons against
  the same map boundary used for tanks (fail-closed when degenerate).
- **HUD**: `BuildMinimap` gained a beacons argument and draws each as a
  5 px diamond (`Polygon`) with a dark outline, translated to its
  normalized position; the panel renders when either tanks or beacons
  exist.

## Tests (all green)

- `MainViewModelTests`: the minimap test's frame now carries a behind-camera
  beacon at world (50, -50) → asserted at (0.75, 0.25) with name + color.
- `OverlayFrameProjectorTests`: world coords asserted through projection for
  both in-front and behind-camera beacons (screen null but WorldX/WorldZ
  intact).
- `ReadApiEndpointsTests`: beacon `WorldX`/`WorldZ` asserted in the frame
  response.
- Full solution: 0 warnings, all 13 test projects pass (~900 tests), gate
  green.

## Real-data verification (web host, `.data` DB)

Added `CenterPOI` at world (0, 35, 0) to the Oasis Palms session via the
CLI `beacon add`; the frame API returned it with `worldX=0.0, worldZ=0.0`
and the boundary normalization landed at (0.56, 0.57) on map 11 — near map
center, as expected. Removed the test beacon afterward.

## Notes

- The map *texture* (loaded via `LoadMinimapAsync`) is still not aligned
  under the dot panel — that remains the last minimap follow-up.
- Beacons are only shown while their replay-time window covers the frame
  time (the frame projection already filters), which matches the nameplate
  behavior.
