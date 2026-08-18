# Penetration v0.3 — VehicleGunRotator static structure (aim/ray pass, G1 item 3/5)

**Date:** 2026-08-18 (UTC)
**Status:** the rotator's byte-verified structure is documented; the
per-field angle semantics (turret yaw vs gun elevation) remain **not
cleanly resolvable statically** — confirming the 2026-08-16 conclusion. The
live controlled turret/gun transition correlation is the promotion gate.
**Blocker:** `BLK-0027` (aim/ray static pass done; live correlation remains)

## Question

Against the live-proven `AvatarGameLogic +0x1fc → VehicleGunRotator`, what
are the turret-yaw / gun-elevation / muzzle-ray fields (G1 item 3/5: shot
ray = muzzle origin + gun direction)?

## Findings (hash-bound, `1cda5c31…`; evidence `.build/ghidra-evidence-rotator2/`,
git-ignored, under the `ghidra-project` workstream lock — acquired/released
cleanly)

The rotator is a 0x1F0-byte object with three vtables (`0x32eeb40`,
`0x32eeb54`, `0x32eeb64`). RTTI strings name `Update` (`0x32eec5c`) and
`GetGunMarkerPosition` (`0x32eec34`); xref resolves `Update = FUN_01ed1df0`,
`GetGunMarkerPosition = FUN_01ec12b0`.

1. **Vtable method roles (new, byte-verified):**
   - slot 0 `FUN_01eb9c50` — destructor (frees `+0x188/+0x140/+0x44`,
     `operator delete` 0x1F0).
   - slot 1 `FUN_01ec50e0` — event subscription/setup (entity callbacks).
   - slot 2 `FUN_01ec5760` — subscribes `GES::Avatar::PeriodBattleStarted` /
     `PeriodBattleFinished`, registers `FUN_01ec4110`.
   - slot 5 `FUN_01ec4880` — **gun-marker target setter**: `[rot+0x1d4] =
     arg`, `[rot+0x1d8] = FUN_016a7bf0(arg)` (derived ref).
   - slot 6 `FUN_01ec48b0` — clears `[rot+0x1d4]/[rot+0x1d8]`.
   - slot 7 `FUN_01eb89c8` — HUD/aim-info plumbing (reads `[rot+0x1c]`
     list, `[rot+0x20]`, `[rot+0x8c]/[0x94]`, `[rot+0x1c]+0x2b8`).
   None of the vtable methods write the aim-shaped candidate fields
   (`+0x84`, `+0xEC`, `+0x134/+0x138`, `+0x1BC`).

2. **`GetGunMarkerPosition` computes an aim struct at `[rot + 0x28..0x40]`
   (new, byte-verified):** iterates the point list at `[rot+0x44]..[rot+0x48]`
   (stride 0xC) with step `[rot+0x1e8]`, ray-casts to a hit, then stores
   `[rot+0x28] = hit xyz` (packed), `[rot+0x34/+0x38/+0x3C] = normalized
   direction` (muzzle → hit), `[rot+0x40] = distance`, returning `rot+0x28`.
   This is the gun-marker aim ray (a direction vector + hit point), the
   closest static candidate for G1 item 5's "gun direction" — but it is the
   *aim* ray, not a shot-synchronous ray, and its exact role as the muzzle
   ray is unproven.

3. **`Update` (`FUN_01ed1df0`) is a per-frame method (new):** called from an
   `AvatarGameLogic` method at `016f303d` with **4 float args** (four doubles
   converted via `CVTPD2PS`); it stores the last two at `[rot+0xe0]/[rot+0xe4]`,
   writes `[rot+0x18] = 0` and `[rot+0x1c] = (float)FUN_016a14b0(0,0)`. The
   4 inputs are angle/state-shaped but their order (yaw vs elevation) is not
   nameable without live correlation.

4. **Confirmed the 2026-08-16 conclusion:** the `-100500.0f` sentinel
   candidates (`+0x134/+0x138`, `+0x1BC`) and the `+0x1A8..+0x1C0` struct
   (synced from the controller at `rot+0x148` by `FUN_01ac4f70`, whose
   producer `FUN_01aa7140` is a batch/iteration computation) are not
   angle-semantics-resolvable statically.

## Conclusion

**Static pass for the aim/ray fields is DONE and bounded:** the rotator's
vtable methods are event/UI plumbing plus the gun-marker target refs at
`+0x1d4/+0x1d8`; the concrete gun-marker aim struct is `[rot+0x28..0x40]`
(hit pos + normalized direction + distance); `Update` receives 4
angle/state-shaped floats and stores two at `+0xe0/+0xe4`. Naming turret
yaw vs gun elevation (and confirming the muzzle-ray role) **requires the
live controlled turret/gun transition correlation** — the same promotion
gate as the shell-swap correlation. Nothing is promoted; no shared contract
changes.
