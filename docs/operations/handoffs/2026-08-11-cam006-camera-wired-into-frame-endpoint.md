# CAM-006 — memory camera wired into the frame endpoint

- Date: 2026-08-11
- Status: committed; unit-verified (no live session this turn)
- Supersedes: completes the CAM-004 "Next steps" wiring (host exposes the
  pose — CAM-005 — then the overlay frame path consumes it — this handoff)

## What landed

The verified GameCamera pose now reaches the overlay frame path end-to-end,
behind the `cameraOverride` seam that `ReplayFrameSource` already tested:

| Piece | File | Change |
|---|---|---|
| Port | `IOverlayFrameSource.GetFrameAsync` | Additive optional `OverlayCamera? cameraOverride = null` — default null = today's viewpoint fallback, so no existing caller changes behavior |
| Source | `ReplayFrameSource.GetFrameAsync` | Passes the override through to `BuildFrame`/`BuildCamera` (which already fails closed on non-finite poses) |
| Endpoint | `ReadApiEndpoints.GetOverlayFrameAsync` | New optional `IGameMemoryScanner? scanner`; when present, calls `ReadCameraPoseAsync` and maps a `Resolved` pose → `OverlayCamera(X, Y, Z, YawRadians, PitchRadians, RollRadians: null)`; any failure (gate, status, cancellation) → null override → viewpoint fallback |
| Tests | `ReadApiEndpointsTests` | 2 new: resolved pose threads through to the projected response; failed read falls back to the viewpoint camera (fake records the override; `CameraScannerStub` implements the scanner port) |

The endpoint parameter is optional so the DI resolves the real scanner in the
host while existing tests (and any scanner-less deployment) keep working.

## Fail-closed behavior

- No scanner injected → viewpoint camera (unchanged).
- Gate not satisfied / pose not `Resolved` / read failure → viewpoint camera.
- Non-finite pose → `BuildCamera` falls through to the viewpoint (existing
  test `Frame_NonFiniteCameraOverride_FallsBackToViewpoint`).
- The overlay renders only `OverlayFrame`; the memory camera is a data-source
  swap, no overlay rewrite.

## Verified

- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- Host.Web.Tests: 144 passed (2 new); Application.Tests: 63; Host.Cli.Tests: 35.
- Full `scripts/validate.ps1` gate: see git log for the commit's result.

## Next steps

- **Live session:** launch a replay, then `GET /sessions/{id}/frame` while a
  gate-verified session is live — the response's camera fields should carry
  the memory pose (compare `CameraYawRadians` to the decoded camera yaw and
  `CameraX/Y/Z` to the 20-30 m third-person offset), and the projected tank
  nameplates should sit on their world targets.
- **Projection cross-check:** project decoded tank positions through the
  memory camera and assert the viewpoint tank lands near screen center —
  the end-to-end W2S validation that does not need a screen capture.
- The CAM-003 phase flip (`base+0x325ad2c`) is irrelevant to this path: the
  camera chain anchors on the avatar vftable, and the pose read is
  session-controller-gate-free.
