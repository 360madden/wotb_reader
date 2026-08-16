# Penetration v0.3 — viewpoint-vehicle → gun/rotator ownership walk

**Date:** 2026-08-16 (UTC)
**Status:** static derivation complete (hash-bound); live validation not yet run
**Blocker:** `BLK-0027` (still open — this walk is the discriminator that turns
the census's `BoundsExceeded` negative into a proven viewpoint owner)

## Question

The owner census proved there is exactly **one** live `VehicleGunRotator` and
**one** `AvatarGunAgent` in a managed offline replay (and 42 live `VehicleGun`
objects). Before any shell/aim/ray field offset inside those objects is
trusted, the walk must pin **which object owns the viewpoint vehicle's gun and
rotator, and at what field offsets**, and then live-validate that ownership.

## Static evidence (hash-bound, 11.19.0.10 `1cda5c31…`)

New tooling: `tools/ghidra-scripts/FindWeaponVtableInstallers.java` and
`tools/ghidra-scripts/ListCallers.java`. Evidence:
`.build/ghidra-evidence-weapon-install/` (weapon-vtable-installers.txt,
callers.txt, window-disasm.txt — local, ignored).

### vftable RVAs and object shapes

| Family | Primary vftable RVA | Object size | Notes |
|---|---|---|---|
| `VehicleGun` | `0x32dacf4` | `0x64` | primary vftable written only at `+0x0`; `+0x8`/`+0xC` are secondary sub-vtables (`CooldownInfoInterface`, `ListenerHolder<VehicleGunEventsListener>`) at different addresses |
| `VehicleGunRotator` | `0x32eeb40` | `0x1f0` | primary at `+0x0`; `+0x8`=0x36eeb54 (`FollowAimListener`), `+0xC`=0x36eeb64 (`DevOptionsDelegate`) |
| `AvatarGunAgent` | `0x324dae8` | `0xc` | `+0x4` stores the avatar-context reference |

The census therefore counts **distinct objects**, not vtable slots: each object
carries the primary vftable dword exactly once (the secondary slots are
different addresses). 42 `VehicleGun` = 42 live objects; 1 rotator + 1 agent
remain avatar-only.

### Constructor / installer sites

- `VehicleGun` in-place ctor `FUN_01da8bb0` (site `0x19a8c0a`) and allocating
  factory `FUN_01d9cc30`; dtor `FUN_01db0ff0`.
- `VehicleGunRotator` ctor `FUN_01eb6ba0` (site `0x1ab6c01`, source
  `VehicleGunRotator.cpp`); dtor `FUN_01eb9c50`.
- `AvatarGunAgent` ctor `FUN_01360360` (site `0xf603a0`).

### The ownership chain (proven statically)

1. **`AvatarGameLogic` ctor** `FUN_01683b00` (`AvatarGameLogic.cpp`) allocates a
   100-byte `VehicleGun` (`operator_new(100)` → `FUN_01da8bb0`) and stores the
   raw pointer at **`AvatarGameLogic + 0x204`**. It also sets byte **`+0x200` =
   1** (the avatar marker).
2. **`VehicleGameLogic::onEnterWorld`** `FUN_016ea010` (`VehicleGameLogic.cpp`)
   — only when `[this + 0x200]` is set and an id condition matches — allocates a
   0x1f0-byte `VehicleGunRotator` (`FUN_01eb6ba0`) and stores the refcounted
   pointer at **`+0x1fc`**. The `+0x200` marker is set only by the AvatarGameLogic
   ctor, so generic vehicles never get a rotator — matching the census's
   exactly-one rotator.
3. **Reverse pointer:** `VehicleGunRotator` ctor stores its owner `this` at
   **`VehicleGunRotator + 0x10`** (`param_1[4] = param_3`; the caller passes the
   game-logic object as `param_3`).
4. **Entity link (prior work, OD-068/087/091):** `VehicleGameLogic` vtable slot
   `+0x04` getter returns `[this + 0x04]` = the entity, and `[entity + 0xB8]`
   is the current-HP int16 (Verified). `onEnterWorld` uses exactly this getter
   to read `[entity + 0xB8]` for its ALIVE/DEAD check.

So the candidate chain is:

```
viewpoint entity  <-[+0x04]--  AvatarGameLogic (viewpoint vehicle game logic)
                                  |-- [+0x1fc] (refptr) --> VehicleGunRotator
                                  |-- [+0x204] (raw)    --> VehicleGun
VehicleGunRotator --[+0x10] --> AvatarGameLogic   (reverse/back-pointer)
```

## Ranked hypotheses

- **H1 (primary):** the single live rotator/agent pair is the viewpoint's; the
  rotator's `+0x10` points back to the AvatarGameLogic; that object's `+0x204`
  is the viewpoint `VehicleGun` and its `+0x04` is the viewpoint entity.
- **H2 (rejected):** the gun/rotator are owned by a different, unrelated
  object. Rejected because the ctor that installs the avatar marker (`+0x200`)
  is the same object that stores the gun, and `onEnterWorld` stores the rotator
  into the same `+0x1f8..+0x204` field block.
- **H3 (residual risk):** the `+0x04 → entity` link is inherited rather than
  re-derived here; it relies on OD-068's published getter plus OD-087/091's
  `[entity+0xB8]` HP proof. It is cheap to re-confirm in the live protocol.

## Live-validation protocol (bounded, no promotion)

All reads go through the existing coordinator-owned capture seam (loopback +
capability + `OfflineReplayVerified` + exact-build gates). Expected read plan,
executed once per battle with the two-pass stability discipline:

1. **Find the rotator.** AOB-scan Private|Mapped for `moduleBase + 0x32eeb40`
   (exact 4-byte, alignment 4). Require exactly 1 candidate (already
   live-proven by the census).
2. **Reverse pointer.** Read `[rotator + 0x10]` → `owner`. Require the owner to
   be a readable heap address (not image, not null).
3. **Forward pointer round-trip.** Read `[owner + 0x1fc]` and require it equals
   the rotator address (the refptr field points back to the same object).
4. **Gun pointer.** Read `[owner + 0x204]` → `gun`; read `[gun + 0x0]` and
   require it equals `moduleBase + 0x32dacf4` (it is a live `VehicleGun`).
5. **Entity link.** Read `[owner + 0x04]` → `entity`; read `[entity + 0xB8]`
   and require it is a plausible alive int16 consistent with the known
   viewpoint HP.

Expected honest result: steps 1–5 all succeed on the first pass and reproduce
identically on the second pass (`stable`), with no raw address/id/path leaving
the source. Success proves the walk; it does **not** promote any shell/aim/ray
field and does not change the `NotReady` badge.

Stop conditions: step 1 finds `≠ 1` rotator (walk not unique — fail closed);
any pointer does not point into Private|Mapped memory (fail closed); any
vtable check fails (wrong family — fail closed); or the two passes disagree
(fail closed, no promotion).

## What remains after the walk passes

The walk only establishes *ownership*. The next static steps are the
**phase 2–4 semantic field offsets** inside the now-proven objects:

- `VehicleGun`: configured-gun identity and loaded-shell state (controlled
  shell-swap transitions).
- `VehicleGunRotator`: turret yaw and gun elevation (the `+0x4d/+0x4e`
  `0xc7c44a00` constant and the `FollowAimListener` inheritance are leads).
- shot-synchronous muzzle origin + direction (ray).

None of these are derived yet, so `ConfiguredGunUnproven`,
`ShellTransitionUnproven`, `TurretYawUnproven`, `GunElevationUnproven`, and the
ray reasons remain honest. This document records the walk and its proof
protocol only.
