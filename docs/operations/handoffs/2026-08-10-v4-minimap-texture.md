# Handoff — 2026-08-10: minimap texture under the god-view dots

**Status:** done, committed, pushed. Gate green.

## What and why

The V4 god-view minimap panel drew tank dots and beacons against a plain dark
background. The map texture image was already loaded by the view model
(`LoadMinimapAsync`, cached per map id) and rendered by the separate
`PositionPlot` control — but the HUD minimap panel never drew it. This closes
that gap: dots now sit on the actual map.

## The change

- `W2sHudView.Render` gained an `ImageSource? minimapImage` parameter; the
  view model's `MinimapImageSource` is passed from `MainWindow`.
- `BuildMinimap` draws the texture first (under beacons and dots), stretched
  to the 150px panel square with 0.55 opacity. Stretching to the panel is the
  key alignment decision: dots and beacons map normalized 0..1 onto
  0..panelSize, so a stretch-filled texture lines terrain up with dot
  positions exactly. A non-square map boundary therefore distorts the
  *texture* rather than the dots — dot alignment is the invariant that
  matters for an overlay.
- New pure helper `MinimapImageRect(panelSize)` for the test seam.

## Verification

- New test: `MinimapImageRect_FillsPanelSquare` (texture rect = full panel).
  Overlay suite 86/86; full suite 12 projects, ~861 tests, 0 warnings.
- End-to-end on the dev host: `/api/v1/maps/11/minimap` returns 404 here
  because the game client is not installed in this environment — which
  exercises the intended fail-closed path: `MinimapImageSource` stays null
  and the panel renders exactly as before (dark background + dots). On a
  machine with the game installed, the texture renders under the dots.
- `scripts/python/offline_check.py`: links, blocker numbering, ledger OK.

## Notes for next

- If a future map's boundary is strongly non-square and the distortion
  bothers, the fix is to size the panel to the boundary aspect ratio and
  stretch the texture to that, keeping the dot mapping unchanged.
- The texture opacity (0.55) and panel background are tuned for dot
  legibility; both are single constants in `BuildMinimap`.
