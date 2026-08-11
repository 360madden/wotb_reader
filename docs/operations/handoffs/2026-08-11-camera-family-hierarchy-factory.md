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

## Files touched

`.build/ghidra-evidence/` (ignored): `find-vftable-refs.txt`,
`functions-disasm.txt`, `window-disasm.txt`, `run-cam-*.log`,
`resolve-vftable-class.txt`; this handoff; roadmap camera track.

## Next steps

- **VP/view matrix**: `FUN_01dde860` is the world→camera composition seam
  (reads `[t+0x60..0x90]` + camera orientation); trace where its output
  4×4 lands (per-frame VP target) and where FOV (`DAT_035cd11c`) applies —
  the W2S projection matrix.
- Resolve the camera's **global root** (who owns the battle manager holding
  `[mgr+0x2C]`) so a live/offline read plan can name a fixed address chain.
- Pin the camera **world position** source (the angles at `+0x58/+0x5c`
  imply a position elsewhere in the camera-state object; the ring's
  `+0x60`-region matrix may be the view basis).
