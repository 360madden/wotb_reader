# Penetration v0.3 — CurrentGunAnglesComponent layout + rotator/component split (G1 item 5)

**Date:** 2026-08-18 (UTC)
**Status:** static, byte-verified, hash-bound `1cda5c31…` (evidence
`.build/ghidra-evidence-gunaxis/`, git-ignored, under the `ghidra-project`
workstream lock — acquired/released cleanly). Nothing promoted.
**Blocker:** `BLK-0027` (narrowed again — the *component* axis semantics are now
named; the *rotator input* order is still live-gated).
**Refines:** `2026-08-18-gun-angles-vocabulary.md`,
`2026-08-18-rotator-static-structure.md`.

## Question

Which offset holds turret yaw vs gun pitch, and is the rotator's `+0xe0/+0xe4`
pair the same values as the named DAVA gun-angle component?

## Findings

1. **`CurrentGunAnglesComponent` is a 0x18-byte component with two float fields
   — and they are now named (byte-verified).** The constructor `FUN_007e65b0`
   writes `[+0x00]=[+0x04]=CurrentGunAnglesComponent::vftable`, copies the DAVA
   base at `+0x08/+0x0c`, then copies two data dwords to **`+0x10`/`+0x14`**.
   The two accessors are single-instruction getters:
   - `FUN_00740cd0` = `FLD float ptr [ECX+0x10]; RET` → **turret yaw**.
   - `FUN_00740cc0` = `FLD float ptr [ECX+0x14]; RET` → **gun pitch**.

2. **The yaw/pitch naming is pinned by the visual bridge, not by inference.**
   `TankVisualUtils::UpdateTurretGunAngles` (`FUN_0082d950`) reads the two
   getters and applies them in order:
   ```c
   local_18 = FUN_00740cd0();   // [+0x10] -> SetTurretAngle(local_18)
   local_14 = FUN_00740cc0();   // [+0x14] -> SetGunAngle(local_14)
   FUN_0082a7b0(param_1, local_18);   // TankVisualUtils::SetTurretAngle
   FUN_0082a420(param_1, local_14);   // TankVisualUtils::SetGunAngle
   ```
   So **`+0x10` = turretYaw, `+0x14` = gunPitch**, and the earlier
   "turret yaw / gun pitch" vocabulary is now anchored to concrete offsets.

3. **`SetTurretAngle`/`SetGunAngle` are visual-only quaternion writers.**
   `SetTurretAngle` (`FUN_0082a7b0`) and `SetGunAngle` (`FUN_0082a420`) each
   build a rotation quaternion (4 floats) and write it into a transform
   component data array at stride `0x24` (offsets `+0x00..+0x0c`, dirty flag at
   `+0x20`). They do **not** write the logical `CurrentGunAnglesComponent`
   fields — confirming the muzzle origin is a computed transform, not a static
   field (consistent with `pen-shot-ray-read-proposal.md`).

4. **The rotator's inputs are NOT the component's fields — they come from a
   protobuf targeting message.** `AvatarGameLogic::updateTargetingInfo`
   (`FUN_016f2de0`) carries the diagnostic
   `"Error parsing protobuf message in AvatarGameLogic::updateTargetingInfo()"`
   and calls `VehicleGunRotator::Update` with four floats converted from four
   stack doubles (`local_64/5c/54/4c`). The doubles' source is the parsed
   targeting protobuf (network/replay), not `CurrentGunAnglesComponent`.
   `VehicleGameLogic::set_gunAnglesPacked` (`FUN_016ee230`) is the other side
   of this: it unpacks a 16-bit packed angle from `[state+0x7e]` (low 6 bits =
   interpolation fraction, high bits = destination index) and forwards
   `(destination, current)` into `FUN_0146d980`.

5. **Reach path found — the component lives in the DAVA entity's flat
   component array.** The lookup `entity->GetComponent(typeId, index)`
   (`FUN_00d51c80`) dereferences the component as
   `[[entity+0x2c] + componentIndex*4]`, where `componentIndex` is computed by
   the reflection type map (`FUN_00a81f40() → [global+0x78] → [ +0x2c] →
   FUN_009046f0(typeId)`). Walking that reflection map live is fragile, but the
   flat array at `entity+0x2c` can instead be **vftable-scanned** for
   `CurrentGunAnglesComponent::vftable 0x31a4868` — exactly the already-audited
   pattern the `pen-ownership-walk`/`GunAim` anchors use for the rotator
   (`moduleBase+0x32eeb40`). Since the ownership walk already proves
   `VehicleGunRotator+0x04 → entity` (OD-068/087/091), the candidate chain is
   `AvatarGameLogic+0x1fc → rotator → +0x04 → entity → +0x2c array → scan for
   0x31a4868 → +0x10/+0x14`. The array bounds (`[entity+0x38]` counts object)
   are not yet pinned to a static length, so the scan needs a bounded read of
   the array before it can ship.

## Conclusion

The **component-level** axis semantics are now closed: `CurrentGunAnglesComponent`
stores `turretYaw@+0x10` and `gunPitch@+0x14` (getters `FUN_00740cd0` /
`FUN_00740cc0`). The **rotator-level** order (`+0xe0` vs `+0xe4`) is still not
statically nameable because the rotator is fed by the targeting protobuf, not by
the component — so the two read surfaces expose *different* stages of the same
pipeline (targeting input vs applied angle state). The live controlled traverse
remains decisive for naming `+0xe0/+0xe4`; if a cleaner authoritative read is
ever wanted, the component fields `+0x10/+0x14` are the named target, and the
reach path is now concrete (vftable-scan the `entity+0x2c` component array for
`0x31a4868`, pending a bounded array-length read). Nothing is promoted.
