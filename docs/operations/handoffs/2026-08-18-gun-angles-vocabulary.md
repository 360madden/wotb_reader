# Penetration v0.3 — gun-axis vocabulary + rotator Update caller (G1 item 5)

**Date:** 2026-08-18 (UTC)
**Status:** static refinement, hash-bound `1cda5c31…` (evidence
`.build/ghidra-evidence-gunangles/`, git-ignored, under the `ghidra-project`
workstream lock — acquired/released cleanly). Nothing promoted; the live
controlled traverse remains the decisive step.
**Blocker:** `BLK-0027` (narrowed — the field *semantics* are now named; the
field *order* still needs the live traverse).
**Refines:** `2026-08-18-rotator-static-structure.md`,
`pen-shot-ray-read-proposal.md`.

## Question

Which of the rotator's two per-frame `Update` inputs (`+0xe0`/`+0xe4`) is turret
yaw vs gun elevation, and where do they come from?

## Findings

1. **`VehicleGunRotator::Update` field mapping (byte-verified, refined):**
   the decompile confirms the earlier "stores the last two" is specifically
   `arg3 → [rot+0xe0]`, `arg4 → [rot+0xe4]` (plus `[rot+0x18] = 0`,
   `[rot+0x1c] = (float)FUN_016a14b0(0,0)`). The first two args are transient
   (not stored).

2. **The caller is `AvatarGameLogic::updateTargetingInfo`**
   (`FUN_016f2de0`; the function's own `"AvatarGameLogic::updateTargetingInfo"`
   debug string confirms it). It calls `Update` with four floats converted from
   four stack doubles (`local_64/5c/54/4c` → args 1..4, so
   `+0xe0 = local_54`, `+0xe4 = local_4c`). The four doubles' *source* is not
   cleanly traceable in this pass (they are local stack slots; the write path
   is not resolved by the decompiler), so the order is still not statically
   nameable.

3. **The axis vocabulary is decisive (symbols, not inference):** WoTB calls the
   two axes **"turret yaw"** and **"gun pitch"** (not "gun elevation"):
   - `s_ON_UPDATE_TURRET_YAW,24` / `s_ON_UPDATE_GUN_PITCH,25` — the two update
     event names (ids 24/25).
   - `s_GetTurretAngle` / `s_GetGunAngle`,
     `s_TankVisualUtils::SetTurretAngle` / `s_TankVisualUtils::SetGunAngle`.
   - `s_gunPitchLimits` / `s_gunPitchMinLimit` / `s_gunPitchMaxLimit` /
     `s_gunPitchUpLimit` / `s_gunPitchDownLimit`, and
     `s_turretYawLimits` / `s_turretYawMinLimit` / `s_turretYawMaxLimit` /
     `s_turretYawLeftLimit` / `s_turretYawRightLimit`.

4. **The gun angles are DAVA components:** `CurrentGunAnglesComponent`
   (vftable `0x31a4868`) and `DestinationGunAnglesComponent` (vftable
   `0x319f94c`), initialized by `GunAnglesInitializationSystem` (vftable
   `0x32eed4c`); a `VehicleGameLogic::set_gunAnglesP…` setter exists. This is
   the architecture behind the rotator's angle inputs, and it is why the muzzle
   origin is a *computed transform* (`turretTransformComponent`,
   `VehicleTransformComponent` / `TankVisualTransformComponent`), not a static
   field.

## Conclusion

The two `Update` inputs are the **turret-yaw / gun-pitch** pair (the axis
semantics are now named), stored at `+0xe0` (arg3) and `+0xe4` (arg4). **Which
offset is yaw vs pitch is still not statically resolvable** — the four input
doubles' provenance is unresolved, so the live controlled turret/gun traverse
remains the decisive step, now with the precise vocabulary to name the two
fields (`turretYaw` / `gunPitch`) once it runs. Nothing is promoted.
