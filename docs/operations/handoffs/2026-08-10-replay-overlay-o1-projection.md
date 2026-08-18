# Replay overlay O1 — WorldToScreen projection + overlay-frame CLI preview — 2026-08-10

Status: OFFLINE COMPLETE. No live session, no product read-surface change.
This is the first Phase-1 deliverable of the replay-overlay roadmap: the
pure projection math every overlay layer (nameplates, beacons, live
swap-in) will share.

## What was done

1. **`Core/Overlay/WorldToScreen.cs`** (pure, no dependencies): projects a
   world point to viewport pixels from the camera pose (position + packet
   yaw/pitch) and a vertical FOV. Conventions match the decoded telemetry:
   - World: X/Z horizontal, Y up; packet yaw 0 faces +Z (yaw ≈
     atan2(dx, dz)); camera-space +X right / +Y up / +Z forward (depth).
   - Right = cross(forward, worldUp) → world +X is on the camera's right
     when facing +Z (matches the packet heading convention).
   - Up = cross(forward, right) → a pitched-up camera drops the horizon
     below center, the way a real camera renders (the initial
     cross(right, forward) convention was wrong and is pinned by tests).
   - Perspective: focal = (h/2)/tan(fov/2); screen origin top-left, Y
     flips. Fail-closed: depth ≤ 0 (at/behind camera) or invalid
     pose/viewport → null; an `OverlayCamera` without rotation evidence
     (pre-migration-5) → null.
2. **CLI `overlay-frame <time> --session <guid> [--fov --width --height]`**:
   renders one frame through `ReplayFrameSource` + `WorldToScreen` —
   viewpoint camera (pos + yaw/pitch), every roster tank with
   name/team/HP/alive/distance plus projected screen X/Y/depth +
   inViewport (or behind-camera), sorted by distance. Pure offline preview
   of the replay-overlay data seam; no UI, no process access.
3. **Real-data fixes surfaced by previewing savanna at 200s**:
   - The position stream carries non-participant entities (a duplicate
     "self" stream that starts at the viewpoint's spawn then teleports to
     origin, plus projectiles/debris). `ReplayFrameSource` now renders
     ONLY roster entities — nameplates never target non-tanks. The frame
     at 200s shows 14 roster tanks (was 17 with junk).
   - `ISessionQueryRepository.GetProjectionAsync` predated migration 5:
     its position SELECT/reader omitted yaw/pitch/roll, so frames saw no
     rotation. The query and `SqliteDomainReaders.ReadPosition` now carry
     the three rotation columns (the only other `ReadPosition` caller is
     the same repository).

## Tests

- 9 `WorldToScreenTests`: center projection, right/above placement, behind
  camera → null, yaw quarter-turn faces +X, pitched-up camera drops the
  horizon, wider FOV pulls points toward center, no-rotation camera → null,
  invalid viewport → null, inViewport bounds. (The two "failures" during
  development were the wrong up-vector convention and MSTest's
  `IsLessThan(upperBound, value)` argument order — both now pinned.)
- 1 new `ReplayFrameSourceTests` case: non-participant entities with full
  position evidence are omitted from the frame.
- 4 `CliOverlayFrameTests` (seeded DB through the real CLI): camera
  rotation + tank screen pixels end-to-end, late-tank fail-closed omission,
  missing-session/bad-time argument rejection.
- Core 147/147, Application 38/38, CLI 32/32.

## Docs

- `docs/operations/record-diffing-groundwork.md` — "WorldToScreen
  projection + overlay-frame preview" section with the convention notes and
  the two real-data findings.
- `docs/operations/product-roadmap.md` — O1 marked ✅ with the CLI preview.

## Gate

`validate.ps1` exit 0 — all suites green, PSSA 0 violations, offset
validator PASS. Tree clean at commit time.

## Next (Phase 1, all parallel, file-disjoint)

- O2 nameplate layer over the replay window (clock-anchored: frame at
  replay time t → nameplates at projected pixels) — reuses O1 verbatim.
- O3 beacon/POI model (world coords + label + color + replay-time tag) +
  persistence.
- O5 `--heading-delta` extractor mode for the plan/tooling reuse.
- The `overlay-frame` CLI is the preview harness for all of them until the
  UI exists.
