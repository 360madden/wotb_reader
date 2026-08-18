# Penetration v0.3 — `AmmoController +0x40` setter FOUND (static, byte-level)

**Date:** 2026-08-18 (UTC)
**Status:** the equip-time setter of `AmmoController +0x40` is located; the
static shell-index chain is now complete end-to-end. The only remaining
BLK-0027 item is the live controlled shell-swap correlation (promotion gate).
**Blocker:** `BLK-0027` (narrowed — static derivation complete)

## Question

Who writes `[AmmoController + 0x40]` (the gun/ammo ref) at equip time?

## Findings (hash-bound, `1cda5c31…`; evidence `.build/ghidra-evidence-ammo/`
and `.build/ghidra-evidence-ammo2/`, git-ignored, under the `ghidra-project`
workstream lock — acquired/released cleanly)

1. **Single construction site.** `FindFunctionReferences` on the
   `AmmoController::vftable` (`0x327d3e0`) returns exactly one reference: the
   `AvatarGameLogic` ctor (`FUN_01683b00`, `01684083`). The ctor initialises
   `[AmmoController+0x40] = 0` (dword `param_1[0x13d]` = byte `0x4F4`) and
   never stores a non-zero value, so the setter is a later method call, not a
   construction site.

2. **The setter is `AmmoController::ResetAmmo` (`FUN_015eff70`, RVA
   `0x11eff70`).** Byte-verified at `015effaa`–`015effc5`:
   ```
   015effaa: MOV ESI,dword ptr [EBX + 0x40]   ; old = [this+0x40]
   015effad: MOV ECX,dword ptr [EAX]          ; ECX = [arg] = new gun/ammo ref
   015effaf: CMP ESI,ECX
   015effb1: JZ  skip
   015effb3: MOV dword ptr [EBX + 0x40],ECX   ; [this+0x40] = new   <-- SETTER
   015effb6: TEST ECX,ECX
   015effb8: JZ 0x015effbf
   015effba: CALL 0x0090abc0                  ; helper(new)  (thiscall ECX)
   015effbf: TEST ESI,ESI
   015effc1: JZ 0x015effca
   015effc3: MOV ECX,ESI
   015effc5: CALL 0x0090a660                  ; helper(old)  (thiscall ECX)
   ```
   Save-old → store-new → helper(new) → helper(old) is the canonical
   reference-counted setter. The two helpers sit as labels inside the DAVA
   refcounted-base functions `FUN_00d0aad0` / `FUN_00d0a5d0` (Ghidra has no
   function boundary there; the swap semantics at the call site are
   unambiguous and their exact names are not needed for the read path).

3. **Equip-time call site.** `ResetAmmo` is called at `016d1bbe` from the
   `AvatarGameLogic` equip/setup method (`FUN_016d1b60`, RVA `0x12d1b60`):
   ```
   016d1bb4: LEA EDI,[ESI + 0x68]      ; &vehicleInfo[0x68] = &new gun ref
   016d1bb7: LEA ECX,[EBX + 0x4b4]     ; this = &avatar[0x4B4] = AmmoController
   016d1bbd: PUSH EDI
   016d1bbe: CALL 0x015eff70           ; ResetAmmo(&avatar[0x4B4], &vInfo[0x68])
   ```
   `EBX` is `AvatarGameLogic` (uses `+0x1f8`/`+0x1fc`/`+0x200`/`+0xd8`; the
   method then reconfigures `[EBX+0x1fc]` = the `VehicleGunRotator` slot from
   the ownership walk). The configured gun/ammo ref lives at `[vehicleInfo
   +0x68]` and is refcount-copied into `[AmmoController+0x40]`.

## Conclusion

**The static shell-index chain is now complete end-to-end:**
```
AvatarGameLogic +0x4B4            -> AmmoController            (ctor, 01684083)
AmmoController  +0x40             -> gun/ammo ref              (ResetAmmo setter,
                                                                 015effb3, from
                                                                 [vehicleInfo+0x68])
AmmoController  +0x38             -> current shell index       (ProcessCurrentShells,
                                                                 FUN_015ef402)
[+0x40] -> +0x20 -> +0x1b0        -> vector<ptr> of shells     (count=(end-begin)>>2)
vector[i] -> +0x1c                -> Shell*                    (identity +0x20/+0x24)
Shell +0x11c                      -> damage.armor HP damage
Shell +0x154                      -> pierce-loss factor
```
`AvatarGameLogic` is already live-proven reachable (pen ownership walk
`2026-08-16`), so every hop from the coordinator to a loaded `Shell`'s damage
fields is now statically resolved. `ResetAmmo` also re-asserts the
`ProcessCurrentShells` reading: the same `+0x40` field is the base of the
shell-vector walk.

Remaining before promotion (unchanged from the plan): the **live controlled
shell-swap correlation** of `AmmoController +0x38` (and the `+0x40` chain) in
an exact-build managed offline replay — per `pen-promotion-gates.md`. Nothing
is promoted.
