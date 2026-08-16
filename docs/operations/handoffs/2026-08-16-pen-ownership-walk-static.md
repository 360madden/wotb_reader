# Penetration v0.3 — viewpoint-vehicle ownership walk (static derivation)

**Date:** 2026-08-16 (UTC)
**Status:** static half of the ownership walk derived and recorded; live
validation not yet run
**Blocker:** `BLK-0027` (open — the walk is the discriminator that turns the
census `BoundsExceeded` negative into a proven viewpoint owner)

## What changed

The census's `42 VehicleGun / 1 VehicleGunRotator / 1 AvatarGunAgent` signal
was followed to its static root. Two new hash-bound Ghidra tools locate the
weapon-family vtable-installer (constructor) sites and their callers, and the
resulting chain is recorded in
[`pen-ownership-walk-proof-protocol.md`](pen-ownership-walk-proof-protocol.md).

## Static findings (hash-bound, 11.19.0.10 `1cda5c31…`)

- The census counts **distinct objects**: each weapon object carries its
  primary vftable dword exactly once (`+0x0`); `+0x8`/`+0xC` are secondary
  sub-vtables at different addresses. So 42 `VehicleGun` = 42 live objects.
- **`AvatarGameLogic` ctor** (`FUN_01683b00`, `AvatarGameLogic.cpp`) creates a
  100-byte `VehicleGun` and stores it at **`+0x204`**; sets the avatar marker
  byte **`+0x200` = 1**.
- **`VehicleGameLogic::onEnterWorld`** (`FUN_016ea010`) stores the 0x1f0-byte
  `VehicleGunRotator` (refptr) at **`+0x1fc`** only when the `+0x200` marker is
  set — matching the census's exactly-one rotator.
- **Reverse pointer:** `VehicleGunRotator + 0x10` = its owner (`param_1[4] =
  param_3` in the ctor).
- **Entity link:** inherited from OD-068/087/091 — `[this + 0x04]` = entity and
  `[entity + 0xB8]` = HP (Verified).

Candidate chain: `viewpoint entity <-[+0x04]- AvatarGameLogic` with
`+0x1fc → VehicleGunRotator` and `+0x204 → VehicleGun`, plus the rotator's
`+0x10 → AvatarGameLogic` back-pointer.

## Bounded live-validation protocol (not run)

Five gated reads through the existing capture seam: unique rotator → `+0x10`
owner → `+0x1fc` round-trip → `+0x204` gun vtable check → `+0x04` entity HP
check, two passes, fail closed. Full stop conditions are in the proof-protocol
doc. Success proves ownership only; it does not promote any shell/aim/ray
field and does not change the `NotReady` badge.

## Validation

- New tools: `FindWeaponVtableInstallers.java`, `ListCallers.java` (hash-bound
  evidence in `.build/ghidra-evidence-weapon-install/`, local/ignored).
- No product code or shared contract changed.
- Full `scripts/validate.ps1` gate: green (see terminal output).

## Next step

Run the live-validation protocol on one exact-build managed offline replay
(the launcher + a new capture phase), adjudicate H1, and only then start the
phase 2–4 semantic field derivation (configured gun, loaded shell, turret yaw,
gun elevation, muzzle ray) inside the now-proven owner objects.
