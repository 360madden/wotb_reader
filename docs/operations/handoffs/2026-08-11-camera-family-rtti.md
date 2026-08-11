# 2026-08-11 — Camera family RTTI foothold: ReplayCameraController + BaseCameraController

## Summary

The W2S overlay's static anchor is the camera. This pass resolves the
camera **class hierarchy from RTTI** (first camera-family evidence in the
repo) and pins the camera-state ring writer — the first concrete camera
member offsets.

## New tools

- `tools/ghidra-scripts/FindVftableForType.java` — reverse RTTI: from a
  mangled type-name string address, find the TypeDescriptor, every COL
  referencing it, and every vftable whose `(vftable-4)` points to a COL.
  Works under `-noanalysis` (raw scans; Ghidra computes no references).
  Verified: `ReplayCameraController` name string at abs `0x4278660`.
- `tools/ghidra-scripts/DumpHierarchy.java` — walk a vftable's RTTI class
  hierarchy and print every base's type name + name-string address
  (x86 descriptor layout: pTypeDescriptor +0x00, numContainedBases +0x04,
  PMD 12 bytes +0x08..+0x10, pVTable +0x14 — pVTable is 0 for primary
  bases, so reverse lookup is the reliable path for bases).

## Verified anchors (hash `1cda5c31…1760307d`)

| Anchor | RVA | Verified by |
|---|---|---|
| `ReplayCameraController::vftable` | `0x326dd0c` | forward `ResolveVftableClass` → `resolved:.?AVReplayCameraController@@`; hierarchy: BaseCameraController, GameLogicAware, DevOptionsDelegate, PlayerOptionsDelegate, DragListener, SecondMouseButtonListener |
| `BaseCameraController::vftable` | `0x32dddcc` | forward `ResolveVftableClass` → `resolved:.?AVBaseCameraController@@` |

## First camera member offsets

`BaseCameraController` slot 4 = **`FUN_01dd2cd0` (RVA 0x19d2cd0)** writes
the camera-state **ring** at `[camera+0x28]`:

- `*(ring + (ringIndex + 0x36) * 0x10) = param_2` — entry at `0x360 +
  ringIndex*0x10` (0x10-byte = 4-float stride)
- `*(ring + 0x364 + ringIndex*0x10) = param_3` — sibling entry
- `ringIndex` lives at `[ring + 0x320]` (800)
- `ReplayCameraController` slot 3 (`FUN_015f0730`) forwards to this writer
  when `[cam+0x54] == 0` — the replay controller delegates camera-state
  ring writes to the base.

`BaseCameraController` slot 7 = **`FUN_01dbdb70` (RVA 0x19bdb70)**: 2D drag
delta accumulated into `[camera+0x34]`/`[camera+0x38]` with activity flag
`[camera+0x3d]` (input path, not the VP matrix).

The ring pattern matches the repo's G0 position-ring architecture — the
camera state is another bounded ring, written per-frame. This is the
anchor for the view/projection hunt: the camera object's ring holds
per-frame camera state (position/rotation candidates); the VP matrix
writer should be reachable from the camera update path.

## Files touched

`tools/ghidra-scripts/FindVftableForType.java` (new),
`tools/ghidra-scripts/DumpHierarchy.java` (new),
`docs/operations/product-roadmap.md` (camera track note),
`docs/operations/handoffs/2026-08-11-camera-family-rtti.md` (this).

## Next steps

- Resolve the camera object's global root (who owns a BaseCameraController
  instance and the `[cam+0x28]` ring) — then the ring entries are live
  camera state candidates.
- Find the VP/view-matrix writer: scan functions that read the camera ring
  and write 4×4 matrices (16-float), the same shape as the transform
  record's `+0x60` fill.
