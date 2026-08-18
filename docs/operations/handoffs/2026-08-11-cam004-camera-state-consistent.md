> **SUPERSEDED 2026-08-11 (CAM-010):** the "23.57 m third-person offset"
> was the yz-swap artifact `√2·|tank.z − tank.y|` (z − y = 16.7 m at the
> read moment) — GameCamera posA `+0x38` is stored (x, z, y) with world
> Y/Z swapped, and the yz-swapped posA tracks the viewpoint tank to
> within ~2 m, not a third-person eye 23.57 m behind it. See
> `docs/operations/handoffs/2026-08-11-cam010-yz-swap-position-convention.md`.
> The chain walk, identity gates, and field offsets remain valid; the
> interpretation of posA as a chase eye does not.

# CAM-004 — camera-state-consistent: the GameCamera pose is the true W2S camera

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`).
Approved offline replay launch (savanna) + `scripts/invoke-camera-state-verify.ps1`
v6. Read-only; nothing promoted, resolver/read surface untouched.

## Verdict: `camera-state-consistent` (schema v6, `cam001-…-093701`)

| evidence | value |
|---|---|
| chain | 3/3 (avatar scan → battleResources → camera → cameraState) |
| camera vftable matches | true (ReplayCameraController `base+0x326dd0c`) |
| cameraState vftable matches | true (GameCamera `base+0x32dafa0`) |
| finite rounds | 8/8 |
| **position-correlated rounds** | **7/8** |
| **third-person offset norm** | **23.57 m** (GameCamera posA `+0x38` vs memory tank via `/entity-position`, same wall time) |
| extra position copy offset | 368.8 m (posC `+0xB0` is NOT the camera — posA confirmed) |
| yaw-correlated rounds | 3/8 (delta 0.047 rad — loose but same convention) |
| memory↔decoded delta | 51.4 m at the yaw-aligned time (approximate: loose yaw alignment; od-073 24/24 within-1-unit evidence establishes the spaces align) |

## What this proves

The GameCamera pose fields live-verified in CAM-002 (position `+0x38/+0x3C/+0x40`,
yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8`) are the
**replay camera's world pose**: the camera tracks the viewpoint tank at a
~23.6 m offset — the real third-person camera distance the overlay's
`WorldToScreen` needs (replacing the viewpoint-tank approximation).
`posC` (+0xB0) is a different quantity (368.8 m away) — recorded so future
sessions do not re-attempt it.

## CAM-003 resolver gate — phase-dependent, now resolved

The session-controller vftable **flips between launches**: the previous
session ran `base+0x325ad2c` (resolver gates reject → `UnsupportedSessionController`,
od-073 0/12), this session ran `base+0x323d9bc` (the resolver's expected
live variant) and `/discover/entity-position` + `/position-page` resolved
normally. The CAM-001 v6 direct-walk fallback + the gate-free walk are the
mitigation for the `0x325ad2c` phase; when the resolver is up it is used
directly (`memoryTankSource: entity-position`). A session-controller
field-scan probe (`.data/cam003-controller-scan.ps1`) maps the variant
when needed.

## Next steps

- Wire the verified GameCamera pose into the overlay: host exposes the
  memory camera pose (position + yaw cos/sin + pitch + basis) during the
  gate-verified session; the overlay feeds it through the
  `cameraOverride` seam in `ReplayFrameSource.BuildCamera` (integration
  design staged in `record-diffing-groundwork.md`).
- Coordinate calibration: the od-073 within-1-unit evidence establishes
  memory↔decoded space alignment; the wiring can proceed on that basis,
  with a projection-based cross-check (project decoded tank positions
  through the memory camera) as the follow-up validation.
