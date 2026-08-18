# Penetration v0.3 — runtime shell-index link FOUND (static, byte-level)

**Date:** 2026-08-18 (UTC)
**Status:** the current-shell index field is located; the `AmmoController +0x40`
gun-ref setter remains the last link before a live-read chain is complete
**Blocker:** `BLK-0027` (narrowed — the phase-2 gaps are now: the +0x40 setter
trace, then live controlled-transition correlation)

## Question

Which `Shell` is loaded at fire time — the runtime shell-index link?

## Findings (hash-bound, `1cda5c31…`; evidence `.build/ghidra-evidence-ammo/`,
git-ignored, under the `ghidra-project` workstream lock)

1. **`AmmoController::ProcessCurrentShells` = `FUN_015ef402`** (RVA `0x11ef402`),
   identified by xref to its RTTI debug string (`0x326eeac`). It writes
   **`this + 0x38` = the current shell index** (int32): the shell list is
   scanned per index; `[this+0x38]` is written on every iteration and reset
   to 0 when no match. `this + 0x34` caches the last-processed shell
   (early-out guard), `this + 0x40` holds the gun/ammo ref whose
   `+0x20 → +0x1b0` is a `std::vector<ptr>` of shells (count =
   `(end - begin) >> 2`), elements `+0x1c → Shell*`, identity dwords at
   `Shell +0x20/+0x24`.

2. **`AmmoController` is embedded at `AvatarGameLogic + 0x4B4`** — the ctor
   (`FUN_01683b00`, RVA `0x1283b00`, debug strings
   `"AvatarGameLogic::AvatarGameLogic"` / `AvatarGameLogic.cpp`):
   `LEA EDI,[EBX + 0x4b4]` then `MOV [EDI],0x367d3e0`
   (`AmmoController::vftable`, RVA `0x327d3e0`, after the
   `ListenerHolder<AmmoChangeListener>` fixup at `0x367d3d8`).

3. **Ctor initializes the index fields**: `[EDI+0x34] = 0x7fffffff`
   (last-shell cache), **`[EDI+0x38] = 0` (current shell index)**,
   `[EDI+0x3c] = 0`, `[EDI+0x40] = 0` (gun ref, linked later), `[EDI+0x2c]`
   = allocated list head (0x3C-byte node).

4. `AmmoController::ResetAmmo` = `FUN_015eff70` (RVA `0x11eff70`) also reads
   `this + 0x40`, confirming the field's role.

## Conclusion

**The runtime shell-index link is `AvatarGameLogic + 0x4B4 → AmmoController
+ 0x38` (int32)**, byte-verified in both the ctor (init 0) and the
`ProcessCurrentShells` scan writer. `AvatarGameLogic` is already live-proven
reachable (pen ownership walk: `+0x1fc` VehicleGunRotator, `+0x204`
VehicleGun), so the index field is on an object the coordinator already
walks. Remaining before a full static chain: find the write that sets
`[AmmoController+0x40]` (the gun ref; not in the ctor or the 9 gun-aware
functions) — then the index→Shell mapping is `[+0x40] → +0x20 → vector at
+0x1b0 → [index] → +0x1c → Shell` (damage `+0x11c`, pierce-loss `+0x154`).
Live validation of the index (controlled shell-swap, per the plan) is still
the promotion gate. Nothing is promoted.