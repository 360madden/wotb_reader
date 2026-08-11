# 2026-08-11 — Camera family: full class hierarchy + factory + ring mechanics (hash `1cda5c31…1760307d`)

## Summary

The W2S overlay needs the camera's per-frame state. This pass resolves the
**entire camera class family** — constructors, destructors, factory, and the
per-frame camera-state ring — as hash-bound static evidence on the verified
11.19.0.10 binary.

## Forward-verified vftables (RTTI, `ResolveVftableClass` verdicts)

| Class | vftable RVA | hierarchy |
|---|---|---|
| `BaseCameraController` | `0x32dddcc` | GameLogicAware + DevOptionsDelegate + PlayerOptionsDelegate + DragListener + SecondMouseButtonListener |
| `CameraController` | `0x32de028` | BaseCameraController + the 5 delegates + ClientArenaListener |
| `ReplayCameraController` | `0x326dd0c` | BaseCameraController + the 5 delegates (no ClientArenaListener) |

**Correction:** the earlier draft pinned "CameraController::vftable = 0x36de028"
from a stale Ghidra symbol. The raw bytes at RVA 0x36de028 are an x86
exception/unwind table (slots 1/4 = 1 and 3 are not code pointers). The true
CameraController vftable is **RVA 0x32de028** — RTTI-verified
(`COL abs=0x39cf854, td abs=0x4300a34, name .?AVCameraController@@`), which
is exactly the constant the CameraController ctor writes.

## Constructors / destructors (byte-pinned)

| RVA | role |
|---|---|
| `0x19a0450` | `BaseCameraController` ctor — sets 5 delegate vftables, then base vftable; `[+0x28]=ring`, `[+0x30]=refcounted obj`, `[+0x34/0x38]=drag floats`, `[+0x3c]=byte`, `[+0x3d]=activity`, `[+0x4c]=5.0f`, `[+0x50]=1.0f` |
| `0x19a1280` | `CameraController` ctor (0x98 alloc) — base part + `[+0x54]=ClientArenaListener::vftable`, `[+0x10..0x14]` → CameraController vftable family `0x32ddfe8..0x32de050`, `[+0x60]=FUN_01d9d480`, `[+0x64]=FUN_01d9d270` |
| `0x115d6320` | `ReplayCameraController` ctor (0x60 alloc) — base + vftable `0x326dd0c`; `[+0x54]=0` (mode), `[+0x5c]=1.0f`; calls ring inits `FUN_01dd4e20`/`FUN_01dd5630` with `[+0x20]/[+0x24]` floats |
| `0x19ad070` / `0x115daee0` | Base / Replay destructors |

## Camera factory — `FUN_0165fe40` (battle manager member)

- Mode via `(**(**)(mgr+0x94)+0xac)()`:
  - **== 2 → ReplayCameraController** (`FUN_015d6320`, alloc 0x60)
  - **else → CameraController** (`FUN_01da1280`, alloc 0x98)
- Result stored refcounted at **`[mgr+0x2C]`**; second object (`FUN_015d5f50`,
  0x80) at `[mgr+0x30]`; post-init `FUN_01ecd270(camera)`.
- Ring source: `[mgr+0xC]+0x8C` → becomes `[camera+0x28]`.
- Called from battle-load init `FUN_01662f00` ("BattleResources" path).

## Camera-state ring mechanics

- Ring object at `[camera+0x28]` (BaseCameraController layout).
- **Ring writer = BaseCameraController vtable slot 4 = `FUN_01dd2cd0`**
  (confirmed: abs ref in the vftable at RVA 0x32ddddc):
  - `*(ring + (idx + 0x36)*0x10) = f0` → entry at `0x360 + idx*0x10`
  - `*(ring + 0x364 + idx*0x10) = f1` → second float of the 16-byte entry
  - `idx` lives at `ring + 0x320` (800); mirrors at `+0x324`/`+0x328`
- Two floats per frame, 0x10-byte entry stride — same bounded-ring shape as
  the G0 entity position ring; entry floats +2/+3 are written by the shared
  ring-write tail (`0x19dd2f0..0x19dd3a0` region: `FUN_01dd9100`,
  `FUN_01dd8530`, drag handlers `FUN_01dd9300/0x9320`).
- `ReplayCameraController` vtable slot 3 (`FUN_015f0730`) forwards into this
  writer when `[cam+0x54] == 0`.

## CameraController vtable slots decoded

| slot | RVA | role |
|---|---|---|
| 0 | `0x19af7ab` | dtor thunk |
| 1 | `0x19c5440` | drag handler: gates on `[ring+800]!=0` and mode ∉ {3,4,5,6}, point-in-rect test on `[cam+0x20/0x24/0x18]`-derived rect, then ring-write via `FUN_01dd9300/0x9320`; `[cam+0x49]` flag |
| 2 | `0x19c51d0` | **drag accumulator**: `[cam+0x34] += dx`, `[cam+0x28_off] += dy * [cam+0x40]` (sensitivity), `[cam+0x30]=0.7f` |
| 3 | `0x19c5310` | state machine: modes 1/3/6, `[cam+0x68]=bool`, `[cam+0x6c]=[gfxMgr+0x40]`, `FUN_01db4160(0/2)` |
| 8 | `0x19cd1a0` | mode-gated `FUN_01db4160(0)` |

The ring's two per-frame floats are the **camera screen-space/input state**
(drag deltas + ring mirrors at `+0x324/+0x328`) — the camera's world
position/view-projection matrix are NOT in this ring; they remain the open
VP-matrix lead (next: the per-frame camera update consumer of this ring, and
the 16-float matrix writer reachable from the camera update path).

## Camera math functions identified (matrix-composition pipeline)

Six call sites of the verified 4×4 matrix multiply `FUN_00729570` land in the
camera region — the view-rotation builders:

| RVA | role |
|---|---|
| `0x19b3bf0` | yaw/pitch → rotation matrix: cos/sin of both angles, 3×3 basis assembled, `FUN_00729570` multiply, 4× MOVUPS copy (16 floats) |
| `0x19dc9c0` | camera-state update: integrates `[+0x58]/[+0x5c]` (yaw/pitch) + deltas `[+0x80]/[+0x84]`, clamps (`DAT_035cd128`, `DAT_035cd11c` = FOV-ish limits), builds rotation from cos/sin (`DAT_03fa2878`/`DAT_03fa2880` quaternion components), writes `[+0x60]/[+0x64]` smoothed |
| `0x19dce80` | same with exponential smoothing: `smoothed += (target - smoothed) * dt * DAT_035919dc` |
| `0x19de860` | combines the **world/transform matrix `[t+0x60..0x90]`** (the hash-bound transform record!) with camera orientation — reads 4× MOVUPS from the transform, `FUN_012a2fb0`, `FUN_00d29ea0` (transform getter) — the world→camera composition seam |

These run on the ring object's header fields (`+0x58..+0x64` yaw/pitch +
smoothed, `+0x80/+0x84` deltas), i.e. the camera-state object at
`[cam+0x28]` holds both the live angles AND the ring entries (`+0x320` index,
`+0x360/0x364` entries). The 2 floats pushed per frame by `FUN_01dd2cd0` are
camera angles/state, not world position.

## Camera state object layout (W2S-critical) — pinned

`FUN_01ddb130` is the **per-frame camera update dispatcher**
(`__thiscall(cameraState, dt)`): it calls the three math functions by mode
(`param_1[0x43] == -1` replay → `FUN_01ddce80`; `param_1[0x4a] == 0` →
`FUN_01ddc9c0`; else → `FUN_01dde860`) and integrates movement input
(`param_1[0x23] += dt * param_1[0x39]`, direction flags `+0x12/+0x13`).

The cameraState object (ring object at `[cam+0x28]`) layout, raw-byte
verified:

| offset | field |
|---|---|
| `+0x58/+0x5C` | current yaw/pitch (rad) |
| `+0x60/+0x64` | smoothed yaw/pitch |
| `+0x80/+0x84` | yaw/pitch deltas |
| `+0xAC..0xC4` | **composed view basis (6 floats = rows 0-1 of the world→camera transform)** — `FUN_01dde860` builds yaw×pitch rotation, multiplies by the transform-record world matrix (`FUN_00729570`), adds the camera position read from `+0x11C` triple, and stores rows at `+0xAC` (2 floats), `+0xB4`, `+0xB8`, `+0xC0` (raw-byte verified: `SUBSS … [ESI+0xAC/0xB0/0xB4]` translation reads at `0x19ddeXX`). NOT a full 4×4 — the full matrix composition / projection rows are composed elsewhere (other camera modes or the renderer) |
| `+0x11C/+0x120/+0x124` | **camera world position (3 floats)** — integrated per frame (`pos += delta` at `0x19db433..0x19db45d` in `FUN_01ddb130`); consumed as the translation input by `FUN_01dde860` (`local_4c/50/48` = the `+0x11C` triple) |
| `+0x320` | ring index (800) |
| `+0x360/0x364 + idx*0x10` | per-frame ring entries (2 floats each) |

This is the **W2S camera anchor**: position + yaw/pitch + view basis all live
in one object reached as `[[mgr+0x2C]+0x28]`. The projection matrix (FOV) and
the full 4×4 view composition (the remaining rows, and the other camera
modes' stores) remain open — the last pieces before a full world→screen
projection can be assembled from static evidence.

## Files touched

`tools/ghidra-scripts/camera-family-disasm.txt` (committed evidence: the six
core camera decompiles), `.build/ghidra-evidence/` (ignored):
`find-vftable-refs.txt`, `functions-disasm.txt`, `window-disasm.txt`,
`run-cam-*.log`, `resolve-vftable-class.txt`; this handoff; roadmap camera
track; ledger OD-RECOVERY-085.

## Next steps

- **Offline verification session** (pre-staged, no game code changes):
  signature-scan the launched replay process for the cameraState ring,
  correlate camera yaw `+0x58` vs the type-10 viewpoint yaw and camera
  position `+0x11C` vs the tank position (the third-person offset), and
  cross-check the `+0xAC` basis — full plan in
  `docs/operations/record-diffing-groundwork.md` → "Camera family static
  discovery — offline verification plan". Deliverable: the true camera for
  `ReplayFrameSource.BuildCamera` (replacing the viewpoint-tank
  approximation).
- **Projection matrix**: find where FOV builds the projection (perspective)
  matrix — likely multiplied with the view basis in the renderer; completes
  world→screen.
- Resolve the camera's **global root** (who owns the battle manager holding
  `[mgr+0x2C]`) so a live/offline read plan can name a fixed address chain
  instead of a signature scan.
