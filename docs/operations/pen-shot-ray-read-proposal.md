# Penetration v0.3 — shot-ray read-surface proposal (G1 item 5)

**Date:** 2026-08-18 (UTC)
**Status:** implemented + tested (2026-08-18) — the `GunAim` anchor,
coordinator/endpoint tests, and `scripts/capture-pen-shot-ray.ps1` are on disk
(mirroring the shipped `shell-state` anchor). No live action, no promotion.
Follows the `pen-ownership-walk` and `shell-state` precedent (proposal → lead
review → implementation + tests → read-only security posture → merge).
**Blocker:** `BLK-0027` (G1 item 5 read surface is the remaining offline half;
the controlled turret/gun traverse is the live half)
**Depends on:** `2026-08-18-rotator-static-structure.md` (aim/ray static pass,
done), `pen-promotion-gates.md` (the G1.5 acceptance criteria this surface feeds).

## Question

G1 item 5 requires the **shot ray** — muzzle origin + gun direction — proven
through controlled transitions. The aim/ray static pass
(`2026-08-18-rotator-static-structure.md`) located the candidates but confirmed
turret yaw vs gun elevation is **not statically nameable**. This proposal
defines the minimal guarded read surface that observes the rotator's aim fields
live, so the controlled turret/gun traverse can name them. Nothing here
publishes or promotes; it is an investigation read.

## The read chain (byte-verified, hash-bound `1cda5c31…`)

The aim fields live **on the `VehicleGunRotator` object itself**, so this
surface is shorter than `shell-state` (which had to walk to the embedded
`AmmoController`). Reusing the already-merged `pen-ownership-walk` scan:

1. AOB-scan Private|Mapped for the unique `VehicleGunRotator` vftable
   (`moduleBase + 0x32eeb40`, exact 4-byte, alignment 4, ≤ 8 candidates).
2. Identity re-gate: re-read `[rotator+0x0]` under the guarded lease and
   require it to equal the expected vftable dword (never read off an
   unauthenticated object).
3. Owner round-trip (lightweight, reuses the proven ownership walk constants):
   `[rotator+0x10] → owner`, require `[owner+0x1fc] == rotator`. This confirms
   the candidate is the **viewpoint** vehicle's rotator, not a stray/bot
   candidate, when the scan is ambiguous.
4. Read the aim fields directly from the rotator (all within the one object —
   no further pointer hops):

   | Offset | Field | Shape | Source |
   |---|---:|---|---|
   | `+0xe0` | aim input 0 | float32 | `Update` (`FUN_01ed1df0`) stores arg 3 |
   | `+0xe4` | aim input 1 | float32 | `Update` stores arg 4 |
   | `+0x28` | aim hit X | float32 | `GetGunMarkerPosition` (`FUN_01ec12b0`) |
   | `+0x2c` | aim hit Y | float32 | `GetGunMarkerPosition` |
   | `+0x30` | aim hit Z | float32 | `GetGunMarkerPosition` |
   | `+0x34` | aim dir X (normalized) | float32 | `GetGunMarkerPosition` |
   | `+0x38` | aim dir Y (normalized) | float32 | `GetGunMarkerPosition` |
   | `+0x3c` | aim dir Z (normalized) | float32 | `GetGunMarkerPosition` |
   | `+0x40` | aim distance | float32 | `GetGunMarkerPosition` |

   The aim struct `[rot+0x28..0x40]` is the gun-marker aim ray: hit point,
   normalized direction (muzzle → hit), and distance. It is the closest static
   candidate for the gun direction, and the two `Update` inputs are the
   candidate turret-yaw / gun-elevation pair. Which input is which is exactly
   what the live correlation names.
5. Two-pass: repeat the whole read and require identical float values
   (fail closed on disagreement).

Every hop is a coordinator-owned read. Only the candidate count, the round-trip
boolean, the two aim inputs, the aim struct floats, and a `TwoPassStable`
boolean leave the coordinator. No address, pointer, id, or raw bytes are
returned.

## The discriminator (hull yaw) is NOT re-derived here

The G1.5 gate holds **hull yaw** (`ring +0x30`, already `Verified` via
OD-RECOVERY-088/089) as the discriminator: hull yaw must NOT respond to a
turret traverse. That field is already read live by the shipped
`ReadLiveFrameAsync` surface (the viewpoint tank's `YawRadians`) and by the
`RingRecord` region anchor. The shot-ray surface deliberately does **not**
re-read hull yaw:

- A controlled traverse keeps the hull stationary, so hull yaw is constant
  across the whole capture window — reading it in a separate, already-verified
  request cannot corrupt the discriminator.
- Keeping the new anchor to one object (the rotator) minimizes new failure
  modes and keeps the additive delta smaller than `shell-state`.

The capture driver correlates hull yaw (existing surface) with the new aim
fields (new surface) per sample; the correlator, not the anchor, asserts the
discriminator.

## Why this satisfies the G1.5 gate

The gate needs: (a) one field that tracks turret yaw independent of hull yaw,
(b) one that tracks gun elevation, (c) an aim direction that matches the known
gun direction, all (d) without using the CAM-013 camera pose as the ray source.
This surface exposes exactly the raw material:

- The two `Update` inputs (`+0xe0`/`+0xe4`) are the candidate (yaw, elevation)
  pair — a controlled turret traverse moves exactly one; a controlled elevation
  change moves exactly the other, with hull yaw pinned by the existing surface.
- The aim struct direction (`+0x34/38/3c`) is the gun-marker ray direction —
  the known gun direction, independent of the camera. Matching it against the
  controlled aim confirms it is the gun's, not the camera's.

Honest limits (recorded, not hidden):

- The aim struct is the gun-marker **aim** ray, computed per frame by
  `GetGunMarkerPosition`, not a shot-fire-time snapshot. The gate's
  "shot-synchronous" means "the gun's direction, not the camera's" (the
  gate's own parenthetical), and this surface proves that direction; strict
  shot-moment synchrony is a separate correlation against decoded
  `ShotImpact` events, not a property this anchor claims.
- The muzzle **origin** is derivable as `hit − dir · distance` only if the
  distance field is the muzzle→hit length (a correlation check, not an
  assumption here); a `VehicleGun` world-position field is not yet named, so
  origin is reconstructed from the aim struct and validated in the gate run,
  not asserted by this surface.

## Proposed contract changes

- `EntityRecordRegionAnchor.GunAim = 6`.
- `EntityRecordRegionReadRequest` gains the offset constants above
  (`RotatorAimInput0Offset`, `RotatorAimInput1Offset`, `RotatorAimHitOffset`,
  `RotatorAimDirOffset`, `RotatorAimDistanceOffset`).
- `Type10EntityPositionStatus` gains `GunAimNotFound`, `GunAimMismatch`,
  `GunAimUnstable`.
- `EntityRecordRegionReadResult` gains aggregate-only fields:
  - `int GunAimRotatorCandidateCount`
  - `bool GunAimOwnerRoundTripConfirmed`
  - `float? GunAimInput0` / `GunAimInput1`
  - `float? GunAimHitX` / `GunAimHitY` / `GunAimHitZ`
  - `float? GunAimDirX` / `GunAimDirY` / `GunAimDirZ`
  - `float? GunAimDistance`
  - `bool GunAimTwoPassStable`

All floats must be finite (a non-finite field fails the pass closed, it is
never reported as a resolved value).

## Capture driver

`scripts/capture-pen-shot-ray.ps1` mirrors `capture-pen-shell-state.ps1`: gate
poll → artifact/decode-run binding → single read or `-PollSeconds` polling at
100 ms cadence, reporting distinct `(input0, input1, dir, distance)` tuples,
the transition count per field, and the `TwoPassStable` flag. PS 5.1, ASCII,
exit codes 0/1/2/3. It also samples hull yaw through the existing live-frame or
ring-record read and prints it as the discriminator column (never derives it
from the new anchor).

## Security posture (read-only framing)

Identical to `pen-ownership-walk` / `shell-state`: gate-verified session,
exact-build identity check, guarded reader lease, per-hop identity/fail-closed
checks, two-pass stability, aggregate-only output. The rotator scan is the
already-audited `pen-ownership-walk` scan; the new reads are fixed-offset
float reads within the already-reached rotator object (no new dereference
chain). No new process, module, or build binding; no raw memory leaves. The
two aim inputs and the aim struct direction are world-space/angle values, not
identifiers or account data.

## Test plan

Mirror `GameSessionCoordinatorTests`'s `ShellState` cases: positive resolved,
no-rotator, candidate out-of-range, identity re-gate mismatch, owner round-trip
mismatch, a non-finite float (fail closed), two-pass instability → fail closed,
and privacy (no address/id/pointer in the response or log). Plus a DI pin and
an architecture boundary check (Core has no project references; the new anchor
string in the endpoint parser echoes fields only, no region bytes).

## Not in scope

- Promotion, badge change, or any `PenetrationCaptureEvidence` flag flip — the
  G1.5 promotion gate is the controlled turret/gun traverse (owner-run,
  two content-distinct positive repeats), recorded separately.
- Naming the two inputs — that is the live correlation's output, not a static
  claim this surface makes.
- The muzzle-origin `VehicleGun` position field (not yet statically named).
- Any shared-contract edit beyond this additive anchor; a `WeaponState` /
  `AimState` consumption seam is a G2 proposal requiring lead review plus a
  read-only security audit.
