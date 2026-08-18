# Handoff — 2026-08-10: camera facing tick on the god-view minimap

**Status:** done, committed, pushed. Gate green.

## What and why

The god-view minimap showed *where* the viewpoint was (white ring) but not
*which way it was looking* — the key spatial context for an overlay during
replay playback. The frame already carried `cameraYawRadians` (threaded through
the projection → API in V4's original camera work); it just never reached the
minimap.

## The change

- View model: `MinimapCameraYawRadians` (`_minimapCameraYaw`), set from
  `frame.CameraYawRadians` in the same frame-loading path as the camera X/Z.
- `W2sHudView.Render` → `BuildMinimap` gained the yaw; a small white triangle
  now points from the camera ring toward the viewpoint's facing.
- Direction mapping follows the packet yaw convention documented in
  `WorldToScreen.ScreenHeadingDegrees` — **0 faces +Z, +π/2 faces +X** — mapped
  to panel pixels as world X → panel right, world Z → panel down, so the tick
  apex is `(sin yaw, cos yaw) * tickLength` from the camera position.
- New pure helper `CameraTickApex(cameraX, cameraZ, yaw, panelSize, tickLength)`
  for the test seam. Fails closed: no yaw → no tick (ring only).

## Verification

- New tests: `CameraTickApex` for yaw 0 (panel-down), +π/2 (panel-right) and π
  (panel-up); the god-view minimap view-model test now asserts
  `MinimapCameraYawRadians == 0.5` flows through. Overlay suite 89/89; full
  suite 12 projects green, 0 warnings.
- Real data (savanna at t=100): frame carries `cameraYawRadians = 0.564`
  → tick apex delta (7.5, 11.8) px on the 150 px panel (right-down, an
  east-ish heading — sensible). Endpoint → view model → HUD all wired.
- `scripts/python/offline_check.py`: links, blocker numbering, ledger OK.

## Notes for next

- The tick is white like the ring; if it ever blends into bright minimap
  textures, a dark outline (like the beacon diamonds) fixes it in one stroke.
- Pitch is not drawn (the minimap is a top-down god view by construction);
  `cameraPitchRadians` stays a nameplate/W2S concern.
