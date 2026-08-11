# CAM-005 — host camera-pose read (`POST /discover/camera-pose`)

- Date: 2026-08-11
- Status: committed; unit-verified (no live session this turn)
- Supersedes: nothing (additive to CAM-004)

## What landed

The CAM-001 fixed member-path is now a **host endpoint** — the first half of
the CAM-004 "Next steps" wiring (host exposes the memory camera pose; the
overlay then feeds it through the `cameraOverride` seam in
`ReplayFrameSource.BuildCamera`).

| Piece | File | Notes |
|---|---|---|
| Version-pinned camera layout | `src/WotBTreader.Core/Discovery/Type10CameraPoseLayout.cs` (new) | Same `GameVersion`/`ExecutableSha256` binding as the entity layout; avatar vftable RVAs (replay `0x03277e8c` / live `0x03277da4`), hop offsets (`[avatar+0x154]` → br, `[br+0x2C]` → camera, `[cam+0x28]` → GameCamera), camera vftable RVAs (replay `0x0326dd0c` / live `0x032de028`, GameCamera `0x032dafa0`), pose region `+0x38` len `0x78` |
| Port method | `IGameMemoryScanner.ReadCameraPoseAsync(CancellationToken)` | Application contract, gate-verified only |
| Coordinator walk | `GameSessionCoordinator.ReadCameraPoseAsync` | Gate + build identity → **anchor scan first** (avatar vftable dword = runtime base + RVA; replay variant then live; guarded reader only opened after the anchor exists) → 3 pointer hops with identity gates on every hop → pose region read **twice byte-identically** → parse pos `+0x38/3C/40`, yaw `atan2(sin@+0x54, cos@+0x50)`, pitch `+0x58`, basis `+0x80..0xA8` |
| DTO | `ApiContracts.CameraPoseReadResponse` | Addresses formatted hex (diagnostic evidence); pose floats widened to double; `Status`, per-hop identity flags, `ConsistentDoubleRead`, `ModuleRooted` |
| Endpoint | `POST /discover/camera-pose` (`GameApiEndpoints.DiscoverCameraPoseAsync`) | No request body; gate-verified session only |
| Refactor | `IMemoryScanDiscoverer` extracted from `MemoryScanDiscoverer` (Scan / ScanNeighborhood / ResolvePointerChain) | The coordinator field + constructor param now use the interface so the anchor scan is unit-testable without a process |
| Tests | 5 coordinator (`GameSessionCoordinatorTests`) + 2 endpoint (`GameApiEndpointsTests`) | Gate-fail (never opens reader/scan), unsupported-build fail-closed, exact-build resolved pose (identity gates, double-read, yaw atan2), anchor-not-found (both RVA variants probed, reader never created), camera-vftable mismatch → `ChainBroken`; endpoint happy path + failure propagation |
| Doc | `docs/operations/record-diffing-groundwork.md` step 2 mapping corrected | The integration design still listed pre-CAM-002 offsets (position `+0x11C`, yaw `+0x58`); now the live-verified mapping (pos `+0x38`, yaw cos/sin pair `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8`) |

## Design decisions

- **Anchor scan before reader creation.** A missing anchor (game not in a
  battle, or the avatar not yet spawned) never opens a guarded process lease —
  the endpoint stays cheap when the game is idle.
- **Deliberately gate-free with respect to the session controller.** The chain
  anchors on the avatar vftable, not the session controller, so the CAM-003
  phase flip (`base+0x325ad2c` vs `base+0x323d9bc`) does not matter; identity
  gates are on the camera hops themselves.
- **`UnsupportedBuild` fails closed** (`discover.camera_pose.unsupported_build`
  failure), matching the entity-address diagnostic path rather than the
  region-read's success-with-status.

## Verified

- `dotnet build WotBTreader.sln -c Release`: 0 warnings, 0 errors.
- GameIntegration.Tests: 277 passed / 2 skipped (5 new).
- Host.Web.Tests: 142 passed (2 new).
- Full `scripts/validate.ps1` gate: pending this turn's commit run (see git
  log for the result).

## Next steps

- **Live session:** launch a replay, hit `POST /discover/camera-pose` during
  playback, confirm `Status=Resolved` with both identity flags + a sane
  20-30 m third-person pose; the CAM-003 flip phase should not matter.
- **Overlay wiring (second half):** thread the pose into the frame path — the
  `cameraOverride` seam exists (`BuildFrame`/`BuildCamera`, internal), but
  `IOverlayFrameSource.GetFrameAsync` has no camera parameter yet. Options:
  optional `OverlayCamera?` param on `GetFrameAsync` (additive, default null =
  today's viewpoint fallback) + the frame endpoint obtains the pose from the
  new port when a gate-verified session is live.
- **Projection cross-check:** project decoded tank positions through the
  memory camera (posA + yaw/pitch + basis) and confirm the viewpoint tank
  lands near screen center — the end-to-end W2S validation.
