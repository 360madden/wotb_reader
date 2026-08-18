# Handoff — 2026-08-10: V2 event-feed tie-ins (damage pips + death markers)

**Branch:** `main` — gate green after, tree clean.

## Milestone: the overlay now shows the live damage/death feed

The roadmap's V2 row is partially closed: capture-zone *objective markers*
ride the O3 beacon layer (O4 proved zones are map-static, not in replay
files — nothing to add there), and the **event-feed tie-ins** (damage pips +
death markers) are now done end-to-end in replay mode. Fully offline — the
data is the decoded canonical event stream.

## What shipped

1. **`OverlayEventPip`** (Core) — a transient event-feed marker: affected
   entity, kind (Damage with the amount | Destroyed), and replay time.
2. **`ReplayFrameSource.BuildPips`** — collects damage/destroyed events from
   the trailing **2 s replay window** (an event at the frame time itself is
   the current tick and counts; older events are no longer "live").
   0/unparseable-damage events are dropped.
3. **`OverlayFrame.Pips`** + **`ProjectedPip`** — the projector anchors each
   pip at the affected tank's viewport pixel, but ONLY when that tank is
   in-viewport (a pip for a behind-camera tank is dropped — nothing renders
   off-screen).
4. **API + CLI + HUD** — `OverlayPipResponse` on the frame endpoint,
   `pips` in `overlay-frame` output, `PipItem` in the view model, and
   `BuildPip` in `W2sHudView`: `+N` in amber for damage, a dark `✖` for
   death, floating just above the nameplate anchor.

## Verified

- 2 frame-source tests (window boundaries: 4.5 s-ago event excluded, 1 s-ago
  + current-tick included, destroyed pip with 0 damage; zero-damage events
  are not pips).
- 2 projector tests (pips anchor at the in-viewport tank's pixel; behind-
  camera pip dropped; empty-by-default).
- 1 web endpoint test (Damage pip serialized with entity/kind/amount/pixel);
  1 view-model test (pips populated from the frame JSON).
- Full solution: 0 warnings; all 13 test projects pass (Core 153,
  Application 47, Overlay 78, Web 134, CLI 35, …).
- Real-data sanity on savanna: `overlay-frame` at t=178 s emits two
  damage pips (511 + 256 on entity 3760567 — the real hit timeline), and the
  2 s window expires them by t=180 s. This is the first HUD element whose
  data came from the actual decoded battle, not a fixture.

## Files changed

- `src/WotBTreader.Core/Overlay/OverlayFrameModels.cs` — `OverlayEventPip`,
  `OverlayFrame.Pips`.
- `src/WotBTreader.Application/Replay/ReplayFrameSource.cs` — `BuildPips` +
  window constant.
- `src/WotBTreader.Application/Replay/OverlayFrameProjection.cs` —
  `ProjectedPip` + anchoring.
- `src/WotBTreader.ApiContracts/ReadContracts.cs`, web endpoint, CLI output,
  `MainViewModel` (`Pips`), `PipItem`, `W2sHudView.BuildPip`.
- Tests: `ReplayFrameSourceTests`, `OverlayFrameProjectorTests`,
  `ReadApiEndpointsTests`, `MainViewModelTests`.

## Next

- **V3 (visibility model):** documented god-view default + optional spotted
  reproduction — design-only until a spotting source exists.
- **Capture-zone markers** (the other half of V2): need map-static zone
  geometry (O4); the O3 beacon layer is ready to receive it.
- The live phase (L1–L4) remains approval-gated; V1 + V2 pips are pure
  replay-mode overlay and do not depend on it.
