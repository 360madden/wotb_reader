# Handoff — 2026-08-10: V1 facing arrows on nameplates (replay mode, packet yaw)

**Branch:** `main` — gate green after, tree clean.

## Milestone: the overlay now shows each tank's hull facing

The roadmap's V1 row (facing arrows / heading glyphs on nameplates) is done
for REPLAY mode — entirely offline, no live discovery needed. The data seam
was already proven: `OverlayTankState.YawRadians` (the type-10 packet tail
rotation, migration 5) was in every overlay frame but was DROPPED before
the projection layer. The fix threads it end-to-end and renders it.

## What shipped

1. **`WorldToScreen.ScreenHeadingDegrees`** (new, pure) — the tank's hull
   heading as a screen-space angle in degrees, clockwise from screen-up
   (0 = facing away from the viewer). It projects the tank position AND a
   probe point 8 m along the facing, so the direction is exactly the one the
   perspective viewport renders — an off-center tank facing away points at
   the vanishing point (not a screen-constant vector), which is the
   physically correct behavior. Fail-closed: null when the camera has no
   rotation evidence, the probe is behind the camera, or the facing projects
   to a single pixel (facing exactly along the view axis — genuinely
   unobservable).

2. **Threaded through the whole seam:** `ProjectedTank.ScreenHeadingDegrees`
   (Application) → `OverlayTankResponse.ScreenHeadingDegrees` (ApiContracts)
   → web endpoint + CLI `overlay-frame` output → `NameplateItem` →
   `MainViewModel` → `W2sHudView`.

3. **Rendered:** `BuildHeadingArrow` draws a small triangle above each
   nameplate label, rotated to the tank's screen heading (positive =
   clockwise, matching the packet yaw convention). No arrow when the heading
   is null (no rotation evidence or degenerate facing).

## Verified

- 5 new `WorldToScreenTests` (facing right → +90°, off-center facing away →
  toward vanishing point −90°, off-center facing camera → +90° away from
  vanishing point, dead-on view-axis → null, no camera evidence → null).
- 1 new web endpoint test (heading projected when rotation known, null
  without); 1 view-model test extended (nameplate carries −35°).
- Full solution: 0 warnings; all 13 test projects pass (Core 153,
  Application 44, Overlay 77, Web 133, CLI 35, …).
- Real-data sanity: `overlay-frame` at t=150 s on Oasis Palms emits
  `screenHeadingDegrees` for 12/13 tanks — ±90° for tanks crossing the
  camera's view in opposite directions, matching the map geometry.

## Files changed

- `src/WotBTreader.Core/Overlay/WorldToScreen.cs` — `ScreenHeadingDegrees`.
- `src/WotBTreader.Application/Replay/OverlayFrameProjection.cs` —
  `ProjectedTank` + projector computes the heading.
- `src/WotBTreader.ApiContracts/ReadContracts.cs` — response field.
- `src/WotBTreader.Host.Web/Endpoints/ReadApiEndpoints.cs` — maps it.
- `src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs` — CLI preview emits it.
- `src/WotBTreader.Overlay/ViewModels/NameplateItem.cs`,
  `ViewModels/MainViewModel.cs`, `Views/W2sHudView.xaml.cs` — HUD arrow.
- Tests: `WorldToScreenTests`, `ReadApiEndpointsTests`, `MainViewModelTests`.

## Next

- **Live mode** (future): the same `ScreenHeadingDegrees` renders once the
  L2 live session discovers the in-memory yaw offset — the seam needs no
  overlay changes.
- **V2 (objective markers / event-feed tie-ins):** O4 already proved capture
  zones are map-static (not in replay files), so V2 rides the O3 beacon
  layer; the natural next step is a minimap or battlefield "event pips"
  (damage/death) projected like the beacons.
