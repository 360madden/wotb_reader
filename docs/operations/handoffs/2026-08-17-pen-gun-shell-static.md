# Penetration v0.3 — VehicleGun configured-gun/loaded-shell static trace

**Date:** 2026-08-17 (UTC)
**Status:** static write/read-site trace complete; the three simple candidates
are refuted as per-instance config; per-field semantics remain unproven
**Blocker:** `BLK-0027` (still open — phase 2–4 shell/aim/ray fields remain
underived)

## What was done

Following the ownership walk's live H1 confirmation
(`2026-08-16-pen-ownership-walk-live.md`), the next action was the phase 2–4
semantic field derivation, starting with the static write/read-site trace for
the `VehicleGun` configured-gun/loaded-shell candidates (`+0x38/+0x3C/+0x40`).

Two new hash-bound tools were added and run against the pinned build
(`11.19.0.10`, `1cda5c31…`):

- `tools/ghidra-scripts/TraceGunFieldAccess.java` — enumerates the
  `VehicleGun` vtable (0x32dacf4) and scans each method plus ctor/factory for
  `+0x38/+0x3C/+0x40` accesses with decompiled context.
- `tools/ghidra-scripts/DumpGunLifecycle.java` — decompiles the gun
  creation/configuration lifecycle (`AvatarGameLogic` ctor, allocating
  factory, its caller, `onEnterWorld`) and scans the gun field block.

Evidence: `.build/ghidra-evidence-gun-fields/` (local, ignored).

## Findings (recorded in `pen-ownership-walk-proof-protocol.md`)

1. **`VehicleGun +0x38 = 100.0f`, `+0x3C = 9`, `+0x40 = 1.0f` are class-level
   constructor defaults, not per-instance configured-gun/loaded-shell
   identity.** Both the in-place ctor `FUN_01da8bb0` and the allocating
   factory `FUN_01d9cc30` write the identical hardcoded constants, so they
   cannot carry the per-vehicle gun/shell state. This refutes the earlier
   "possibly reload/rate / shell count/caliber" guesses.

2. **The ownership chain is re-confirmed at the source.** `FUN_01683b00`
   (`AvatarGameLogic` ctor) sets `+0x200`, allocates the 100-byte gun, and
   stores it at `+0x204`; it does not overwrite the gun's `+0x38/+0x3C/+0x40`
   after construction.

3. **The `GunStatusPresenter` path is a batch/iteration presenter, not the
   combat gun config.** `FUN_01da3f20` owns a `VehicleGun` and computes
   `FUN_01650750 = ceil( *(desc+0x1a0) / *(desc+0x19c) )` — the same
   integer-ceil-over-counts batch pattern as the rotator producer, not a
   shell count.

4. `FUN_016ea010` (`onEnterWorld`) confirms the descriptor link: vehicle
   descriptor at `+0x68`, `descr->maxHealth` at `+0x34`.

## Honest conclusion

The configured-gun/loaded-shell identity does **not** live in the
`VehicleGun` field block (`+0x38/+0x3C/+0x40` are defaults) nor in the
presenter's batch count. It must be applied at equip time from the gun
descriptor, or observed via the live controlled shell-swap. Per-field
semantics remain unproven and now need either a deeper gun-descriptor
producer trace or the live controlled shell-swap transitions G1 item 2
already mandates.

## Next step

The live controlled-transition correlation (one clustered launch: turret
traverse → yaw, elevation sweep → pitch, shell A→B→A → loaded-shell identity)
is the remaining way to prove per-field semantics; a deeper gun-descriptor
producer trace is the optional offline follow-up. Both are recorded in
`next-10-actions.md` action #1.
