# Penetration v0.3 — phase 2–4 semantic field static derivation

**Date:** 2026-08-16 (UTC)
**Status:** static write/read-site derivation complete (hash-bound); semantics
not promoted; live controlled-transition correlation still required
**Blocker:** `BLK-0027` (still open)
**Exact build:** `wotblitz.exe` 11.19.0.10 SHA-256
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`

## Question

After the ownership walk confirmed `AvatarGameLogic +0x1fc → VehicleGunRotator`
and `+0x204 → VehicleGun` live, which fields on those objects are configured
gun, loaded shell, turret yaw, gun elevation, and the shot-synchronous ray?

## Method

Hash-bound Ghidra headless on the already-analyzed `WotBlitz` project
(`-noanalysis`). New scripts:

- `tools/ghidra-scripts/TraceWeaponSemanticFields.java` — vtable walk plus
  listing-confirmed displacements (instruction boundary only; the unaligned
  byte-walk class of false positive is retired).
- `tools/ghidra-scripts/TraceWeaponNamedMethods.java` — named assert-string
  methods (`VehicleGunRotator::GetGunMarkerPosition`,
  `VehicleGunRotator::Update`, `AvatarGameLogic::updateVehicleGunReloadTime`,
  `AvatarGameLogic::updateTargetingInfo`).

Local evidence (gitignored): `.build/ghidra-evidence-weapon-fields/`. Script
logs were checked for `SCRIPT ERROR`; both tracers and the helper dumps
returned native exit 0 and wrote reports newer than the invocation.

## Finding 1 — primary vtables are not the domain method set

Each primary vftable is four code slots and then an RTTI pointer:

| Class | Slots | What they are |
|---|---|---|
| `VehicleGun` `0x32dacf4` | dtor, thunk, RTTI-name helper, empty ret | no gun/shell math |
| `VehicleGunRotator` `0x32eeb40` | dtor, battle-period subscribe, unsubscribe, empty ret | no aim math |
| `AvatarGunAgent` `0x324dae8` | dtor + two listener attach/detach thunks | still a 0xC bridge |

Domain methods are **named non-virtuals** identified by source assert strings.
A family-filter that only counted primary-vtable functions therefore reported
zero runtime writers — that was a method-set miss, not proof the fields are
dead.

## Finding 2 — `VehicleGun` carries reload/state, not a shell identity

`AvatarGameLogic::updateVehicleGunReloadTime` (`FUN_016f32e0`) calls
`FUN_01dd4b20` in the VehicleGun code neighborhood. That helper dispatches on
an integer and notifies `VehicleGunEventsListener`. On its `this` (size and
listener type match the 0x64-byte `VehicleGun`):

| Offset | Static role | Notes |
|---|---|---|
| `+0x3C` | reload/gun-state enum | ctor inits `9` (no `switch` case 9 = unset); runtime cases `0..8` |
| `+0x40` | float progress | ctor `1.0f`; case 2 writes `1.0f`, case 5 writes `0` |
| `+0x44` | float time | written from the reload-time argument |
| `+0x48` | listener / helper object | the ctor's 8-byte allocation |
| `+0x4C` | flag | several cases clear it; case 6 sets it |
| `+0x38` | ctor `100.0f` only | no named runtime writer in this pass |

**Loaded-shell identity was not found** on `VehicleGun` or `VehicleGunRotator`.
`ShellID` / `shells.xml` strings resolve to the vehicle-type reader
(`FUN_00810300` / `FUN_008120e0`), which is descr load, not a live clip slot.
That is an honest static no-go for G1 item 2 on these two objects, not a
reason to invent a shell field.

Confirming that `FUN_01dd4b20.this` is exactly `[owner+0x204]` is a cheap live
check (read the gun pointer, then `+0x3C`), not a promotion.

## Finding 3 — rotator gun-marker ray (candidate shot ray)

`VehicleGunRotator::GetGunMarkerPosition` (`FUN_01ec12b0`) writes a 7-dword
block at **`this+0x28`**: position triple, normalized direction triple, and a
range-like scalar at `+0x40`. It returns that block.

`FUN_01ed2040` (called from `VehicleGunRotator::Update` on first tick, and
from two other rotator sites) then:

1. Reads the proven owner at `rotator+0x10` and the vehicle descr at
   `rotator+0x130`.
2. Rebuilds a **16-float 4×4** into **`rotator+0xEC .. +0x128`** via
   `FUN_0133a410` (this is the ctor's zeroed `+0xEC` 0x40-byte block).
3. Calls `GetGunMarkerPosition` with that pose and `rotator+0x1C` (Update's
   clock float).
4. Copies the returned block to the **published marker** at
   **`rotator+0x50`**:

| Published | Meaning in this writer |
|---|---|
| `+0x50 / +0x54 / +0x58` | marker position xyz |
| `+0x5C / +0x60 / +0x64` | normalized direction |
| `+0x68` | range-like scalar |
| `+0x78 / +0x80` | previous position copy when `+0x15 == 0` |

This is a **client gun-marker ray**, not yet a proven muzzle origin. The
function walks a 12-byte point list at `rotator+0x44/+0x48` and can collide
(`FUN_00738db0`). CAM-013 camera direction still cannot satisfy G1 item 5.

## Finding 4 — turret yaw vs gun elevation still not separable here

The three floats fed to the 4×4 builder are:

- `FUN_0169f930(owner)` — a scaled float through
  `[AvatarGameLogic+0x1f8]` vtable `+0x188` and `[owner+0x4ec]` (not a
  single rotator field).
- A sibling object looked up from `rotator+0x4`: getter `FUN_00740cc0` =
  `*(float*)(obj+0x14)`, getter `FUN_00740cd0` = `*(float*)(obj+0x10)`.

`AvatarGameLogic::updateTargetingInfo` (`FUN_016f2de0`) parses a protobuf and
calls `Update` with four floats; `Update` stores only the last two at
`rotator+0xE0` and `+0xE4`. Those four values are the live targeting message,
but static tracing still cannot name which is turret yaw vs gun elevation vs
something else.

The earlier `+0x134/+0x138` `-100500` sentinels remain constructor markers
only. The previous slot-1 batch-struct finding (`+0x1A8`) is unchanged and is
not the marker ray.

## Ranked hypotheses

- **H-ray (primary):** `rotator+0x50` is the published client gun-marker
  (origin + direction + scalar). Live: hull-stationary turret traverse must
  move the direction independently of hull yaw; a decoded shot must join the
  ray inside the existing clock window. Fail if the field tracks hull or only
  the camera.
- **H-matrix:** `rotator+0xEC` is the gun/world 4×4 consumed to build that
  marker. Diagnostic only until a row/column convention is live-proven.
- **H-reload:** `VehicleGun+0x3C/+0x40/+0x44` is reload/state, not shell id.
  Useful later for freshness; it does not satisfy G1 item 2.
- **H-angles-sibling:** turret/gun angles live on the `rotator+0x4` lookup
  object at `+0x10/+0x14`, not as named floats on the rotator. Needs identity
  of that object plus controlled yaw-vs-pitch isolation.
- **H-shell (no-go on these objects):** loaded shell is not a field of
  `VehicleGun` or `VehicleGunRotator` in this pass. Next shell work needs a
  new owner (descr/clip/ammo component) or a proven protobuf slot, not a
  guessed `+0x3C`.

## Rejected shortcuts

- Treating ctor `VehicleGun+0x3C = 9` as shell count or caliber.
- Treating `+0x134/+0x138` sentinels as proven yaw/elevation.
- Labeling `GetGunMarkerPosition` as a muzzle origin without a shot join.
- Using CAM-013, a manual shell selector, or nominal XML as exact inputs.
- Promoting any offset or enabling a colored badge.

## Live protocol (next session; no product wiring)

Reuse the existing ownership-walk to obtain the unique rotator and
`[owner+0x204]` gun under `OfflineReplayVerified`. Coordinator-owned reads
only; aggregate verdicts; no raw bytes/addresses in the response.

1. **Marker ray.** Two-pass read of `rotator+0x50..+0x68`. Require finite
   position, finite unit-ish direction, and two-pass stability.
2. **Reload enum.** Two-pass read of `VehicleGun+0x3C/+0x40/+0x44/+0x4C`.
   Require `+0x3C` in `0..8` or the ctor `9`. This confirms the gun pointer,
   not a shell.
3. **Aim isolation (T1).** Hull-stationary camera traverse: marker direction
   must change with camera yaw and stay independent of decoded hull yaw;
   a pitch-only traverse must move a different component than a yaw-only
   traverse. Camera pose is diagnostic, never a success criterion.
4. **Shell A→B→A.** If no `VehicleGun`/`VehicleGunRotator` field maps to an
   installed shell identity, record `ShellTransitionUnproven` and stop. Do
   not widen the scan.
5. **Shot join.** On a decoded viewpoint shot, the marker ray must join the
   decoded attacker/target/impact inside the declared clock window. Post-shot
   only changes fail.

Stop conditions: non-finite values, two-pass mismatch, hull-following
direction, camera-only “proof”, or any attempt to promote from one replay.

No new shared `WeaponState` / `AimState` contract and no new public endpoint
until this protocol produces two content-distinct positives and the owner
reviews the package.

## What this does not close

G1 items 2 and 5 remain open. Exact armor/layers remain BLK-0027. The v0.3
badge stays `NotReady` on real data.
