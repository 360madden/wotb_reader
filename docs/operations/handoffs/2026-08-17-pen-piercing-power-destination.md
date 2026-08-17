# Penetration v0.3 — piercingPower destination offset resolved (static)

**Date:** 2026-08-17 (UTC)
**Status:** `piercingPower` destination named at byte level; the runtime
shell-index link remains the only phase-2 gap
**Blocker:** `BLK-0027` (still open — shell-index link remains; penetration
offset now closed)

## Question

Where does the `piercingPower` curve parsed by `GunsReader::ParseBaseGunInfo`
(`FUN_008120e0`) get stored?

## Findings (hash-bound, `1cda5c31…`)

Evidence: `.build/ghidra-evidence-piercing/range-disasm.txt` (git-ignored;
headless `DumpRange` runs against the pinned build under the
`ghidra-project` workstream lock, which was acquired and released).

1. **`FUN_00813a30` is `std::vector<float>::push_back`** (byte-verified,
   `0x413a30..0x413ab0`): cursor `[ECX+4]`, end `[ECX+8]`; non-full path
   stores `*cursor = [arg]` and advances `cursor += 4`; full path calls
   `FUN_006e76a0` (the shared vector-grow helper).

2. **`piercingPower` (2-point curve) → `std::vector<float>` at `Gun +0x34`**:
   in the `ParseBaseGunInfo` key dispatch, the `piercingPower` handler
   parses the value string on `0x20` spaces, `atof`s each token, and pushes
   the floats with `LEA ECX,[EDI + 0x34]; CALL 00813a30` for both tokens
   (`0x4128d9` and `0x412900`). `EDI` is the object being filled — proven
   by the same function's `pumpGunReloadTimes` loop writing `[EDI+0x1c8]`
   cursor / `[EDI+0x1cc]` end, which matches the decompiled
   `param_3[0x72]/[0x73]` (Gun `+0x1C8/+0x1CC`). The SSO vector therefore
   occupies `Gun +0x34` (inline buffer), cursor `+0x38`, end `+0x3C`,
   capacity `+0x40`.

3. **Exactly two tokens are mandatory**: the fast path is taken when the
   tokenized value fits two inline tokens; otherwise the code falls into
   the fatal-assert path (`Guns.cpp` line 278, `FUN_0067cab0` crash
   machinery). So `piercingPower` is always a 2-point curve (consistent
   with the earlier "2-point range" reading).

4. **`pumpGunReloadTimes` → `std::vector<float>` at `Gun +0x1C4`** (cursor
   `+0x1C8`, end `+0x1CC`, cap `+0x1D0`) — a separate vector, no conflict
   with the piercing curve. The long-token loop at `0x412db5..0x412e04`
   belongs to this handler, not to `piercingPower`.

5. Re-confirmed in the same pass: per-`Shot` key stores land on the current
   shot via `iVar9 = *(param_3[0x6d] - 4)` with `defaultPortion +0x24`,
   `speed +0x28`, `gravity +0x2C`, `maxDistance +0x30`, `isATGM +0x40` —
   exactly the named `Shot` layout.

## Conclusion

The `piercingPower` destination offset is **resolved**: a 2-float
`std::vector<float>` at `Gun +0x34` (cursor `+0x38`, end `+0x3C`, cap
`+0x40`). This closes the first of the two phase-2 gaps. The remaining gap
before promotion is the runtime shell-index link (which `Shell`/`Shot` is
loaded at fire time) — candidates surfaced in the same session:
`AmmoController` (vftable RVA `0x327d3e0`, methods `ProcessCurrentSh_`,
`ResetAmmo_`), `InventoryAmmoControllerNew` (vftables `0x32b108c` /
`0x32b10c0`, methods `Acti_/Upda_/Plus_/Refr_/DoAp_`), and the
`ListenerHolder<TrayShellListener>` / `AmmoChangeListener` holders owned by
`VehicleGun`. Next bounded step: decompile `AmmoController::ProcessCurrentShell`
and the `InventoryAmmoControllerNew` vtable slots (same headless pattern) to
find the current-shell index field. Nothing is promoted.