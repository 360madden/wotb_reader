# CAM-002 — live pose layout: the GameCamera owns the pose (live-verified)

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`).
Two approved offline replay launches (savanna) with read-only probes
against the server-owned endpoints. Nothing promoted; resolver, read
surface, and offset table untouched. Console-only probes persisted no raw
data.

## What was verified live

The CAM-001 chain **walks and the identity gates PASS** on the real
process (ASLR runtime base observed `0x1A0000`, one avatar candidate):

```
avatar  (scan for dword base + 0x3277e8c)      → 1 candidate
[avatar+0x154] → BattleResources
[br+0x2C]      → camera     vftable = base + 0x326dd0c  ✓ ReplayCameraController
[cam+0x28]     → cameraState vftable = base + 0x32dafa0 ✓ GameCamera (RTTI .?AVGameCamera@@)
```

## Key correction: the pose lives on the GameCamera, NOT the controller

The ReplayCameraController object itself is a **frozen shell** — repeated
reads show constant defaults (yaw 0, pitch 1.0, pos ≈ (0.53, 3.20, 0.09))
with **zero changed fields** in a 0x200-byte diff-scan. The per-frame pose
is written into the **GameCamera** at `[cam+0x28]`. Live diff-scan (two
snapshots ~3 s apart) located every live field:

| offset | field | evidence |
|---|---|---|
| `+0x38/+0x3C/+0x40` | camera position (primary) | moved smoothly per sample |
| `+0x44/+0x48/+0x4C` | interpolated previous copy | ~1 m behind primary |
| `+0x50/+0x54` | yaw cos/sin pair (yaw = atan2(sin, cos)) | unit magnitude, live |
| `+0x58` | pitch | small, live |
| `+0x80..0xA8` | view basis rows | rotation-matrix values, live |
| `+0xB0/+0xB4/+0xB8` | extra position copy | live, diverges from primary |

Earlier static attributions that are now superseded: yaw/pitch are NOT raw
radians at `+0x58/+0x5C` (yaw is the cos/sin pair at `+0x50/+0x54`; `+0x58`
is pitch), the position is NOT at `+0x11C` (that offset reads a constant
near-origin value on the controller and zero on the GameCamera), and the
view basis is `+0x80..0xA8`, not `+0xAC..0xC4`.

## Yaw alignment — memory camera == decoded camera

Memory yaw `atan2(sin, cos)` aligns to the decoded frame camera yaw with
the **same sign convention**: best match delta **0.0027 rad (0.15°)** at a
decoded time, vs 0.204 rad for the negated convention. So the GameCamera
yaw IS the decoded camera yaw; the yaw also confirms the memory object is
the live replay camera.

## Open item: memory ↔ decoded coordinate-space calibration

At the yaw-aligned decoded time, the camera position (all three triples)
is **363-440 m** from the decoded viewpoint tank — far beyond any
third-person offset. The yaw matching so cleanly while positions disagree
points at a **coordinate-space offset/axis difference between the engine's
memory world space and the replay-decode coordinate system** (or a
cinematic camera phase). The next session resolves this by reading the
viewpoint tank position **in memory space** via `/discover/entity-position`
at the same wall time as the camera (no decoded-clock alignment needed):
if `memoryCamera - memoryTank` is 1-30 m, the camera pose is verified in
memory space, and the memory-vs-decoded tank comparison calibrates the
transform into decoded space for the W2S path.

## Script state

`scripts/invoke-camera-state-verify.ps1` is now **v5**: identity gates on
both vftables, GameCamera pose reads at the diff-scan-verified offsets,
memory-space camera-to-tank correlation via `/discover/entity-position`,
and a pre-fetched decoded-yaw timeline for the alignment. Still gated on
`OfflineReplayVerified`, privacy-safe aggregate
(`wotbtreader.cam001.camera-state-verify.v5`). The decisive session run
was not completed this turn (launcher window lost on retries); the script
is ready to run after the next approved launch.

## Files touched

- `scripts/invoke-camera-state-verify.ps1` (v5: GameCamera pose owner +
  memory-space correlation)
- `docs/operations/record-diffing-groundwork.md` (plan corrected to the
  live-verified offsets)
- `docs/operations/product-roadmap.md` (camera track progress)

## Next steps

- Approved launch → `pwsh -File scripts/invoke-camera-state-verify.ps1
  -WaitVerifiedSeconds 240`; expect `camera-state-consistent` if
  `memoryCamera - memoryTank` lands in 1-30 m.
- Calibrate the memory→decoded transform (translation/axis) from the
  same-session memory-vs-decoded tank comparison, then wire the camera
  into `ReplayFrameSource.BuildCamera` via the `cameraOverride` seam.
