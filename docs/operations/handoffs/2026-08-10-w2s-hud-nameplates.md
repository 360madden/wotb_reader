# W2S HUD — projected nameplates over the game window (Phase 1 O2) — 2026-08-10

Status: OFFLINE COMPLETE. No live session, no product read-surface change.
The replay overlay now renders the actual 3D view: while the web host
serves a decoded replay, the WPF HUD draws each tank's nameplate at its
world-to-screen projected pixel, clock-anchored to the replay timeline.

## What was done

1. **Shared projection path** — `OverlayFrameProjector` (Application/Replay):
   frame + vertical FOV + viewport → camera + projected tanks (screen X/Y,
   depth, inViewport), sorted by distance. The CLI `overlay-frame` command
   and the web endpoint both call it, so preview and HUD can never disagree.
   3 unit tests.
2. **Web endpoint** `GET /api/v1/sessions/{id}/frame?timeSeconds&fov&width&height`
   in ReadApiEndpoints: resolves `IOverlayFrameSource` (registered in the
   foundation DI), validates query bounds (400), maps session failures to
   404, returns `OverlayFrameResponse`. 3 endpoint tests.
3. **Contracts** — `OverlayFrameResponse`/`OverlayTankResponse` in
   ApiContracts (the machine contract consumed by the WPF client).
4. **Client** — `TreaderApiClient.GetOverlayFrameAsync(sessionId, time,
   fov, width, height)`; 1 test pins the URL and deserialization.
5. **`W2sHudView`** (WPF UserControl): transparent canvas; renders a
   nameplate per tank — label (player name, fallback tank name), team color
   (blue team 1 / red team 2), HP bar (green→red by fraction, greyed when
   destroyed), distance — anchored above the projected point via the pure,
   tested `AnchorRect` clamp. 3 tests.
6. **Wiring** — MainViewModel gains `Nameplates`, `HudFovDegrees`,
   `LastFrameReplayTimeSeconds`, and `RefreshOverlayFrameAsync(w, h)`: on
   each 50 ms playback tick (and on scrub while paused) it fetches the frame
   at `CurrentTime` with a generation guard + CTS so a stale response can
   never clobber a newer one; failures keep the previous frame. MainWindow
   renders `Nameplates` onto `W2sHudView` on collection change. Own tank
   (distance < 1), behind-camera, and off-viewport tanks are never drawn.
   2 view-model tests (filtering, no-session guard).

## How to see it

1. `dotnet run --project src/WotBTreader.Host.Web` (serves the decoded
   replay DB on the loopback host).
2. Run the Overlay app (`src/WotBTreader.Overlay`), select a session, press
   play — nameplates track tanks as the timeline advances; scrubbing moves
   them too. FOV defaults to 90° (view-model setting).

## Test counts

- Core 147/147, Application 41/41, CLI 32/32, Web 127/127, Overlay 76/76.
- `validate.ps1` exit 0 — all suites green, PSSA 0 violations, offset
  validator PASS. Tree clean at commit time.

## Notes / limits

- The camera is the viewpoint tank's packet pose (pos + yaw/pitch from the
  type-10 packet), not the game's free-look render camera — for the replay
  overlay this is exact and fully offline; the live render camera remains a
  Phase-5 discovery track.
- Replays only show roster tanks with position evidence at the frame time;
  tanks without a decoded name fall back to the entity id.
- FOV is a setting, not measured — the projection is correct for any value;
  matching the game's exact FOV is a display-preference question, not a
  correctness one.
