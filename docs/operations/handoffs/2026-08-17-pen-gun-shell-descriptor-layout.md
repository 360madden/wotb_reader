# Penetration v0.3 — Gun/Shell descriptor field layout

**Date:** 2026-08-17 (UTC)
**Status:** `Shell` damage fields and `Gun` `vector<Shot>` ballistic entries
named (static, producer-side); penetration (`piercingPower`) destination
offset and the runtime shell-index link remain unresolved
**Blocker:** `BLK-0027` (still open — the shell-index link and penetration
offset are the remaining phase-2 gaps before promotion)

## Question

What is the field layout of the `Gun` / `Shell` descriptors, so the shell's
penetration/damage fields can be named?

## Findings (hash-bound, `1cda5c31…`)

Two new tools run against the pinned build (evidence local-only,
`.build/ghidra-evidence-gun-fields/`, ignored):

- `tools/ghidra-scripts/DumpDescriptorVtables.java` — decompiles the vtable
  methods and allocators for the descriptor classes.
- `tools/ghidra-scripts/TraceShellGunProducers.java` — traces the
  `ShellsReader`/`GunsReader` attribute handlers to map each config key to the
  exact store offset.

1. **`Shell` descriptor** (vftable RVA `0x31a1e14`, size `0x158` = 344 B).
   Producer `ShellsReader` attribute handler `FUN_00840570` (`0x440570`);
   defaults from factory `FUN_0083b650` (`0x43b650`). Named fields:
   - `+0x114` int `kind` (`eShellKind`), `+0x118` int `caliber`
   - `+0x11c` float `damage.armor` — per-shell HP damage
   - `+0x120` float `damage.devices` — module damage
   - `+0x12c` bool `isTracer`, `+0x130` string `effects`
   - `+0x148` float `normalizationAngle`, `+0x14c` float `ricochetAngle`
     (stored as `cos(angle)`), `+0x150` float `explosionRadius`,
     `+0x154` float `piercingPowerLossFactorByDistance`
   - `eShellKind`: 0 unknown, 1 HEAT, 2 HE, 3 AP, 4 APHE, 5 APCR.

2. **`Gun` descriptor** (vftable RVA `0x31a7080`, size `0x21c` = 540 B).
   Producer `GunsReader::ParseBaseGunInfo` = `FUN_008120e0` (`0x4120e0`).
   Named fields include `impulse`, `extraPitchLimits.*`, `rotationSpeed`,
   reload/fire-rate params, `vector<Shot>` (`+0x1b0..+0x1b8`), `pumpGunMode`,
   `pumpGunReloadTimes`. The per-shot ballistic entries in `vector<Shot>`
   (`Shot` = `0x44` B) are: `defaultPortion` `+0x24`, `speed` `+0x28`,
   `gravity` `+0x2c`, `maxDistance` `+0x30`, `isATGM` `+0x40`.

3. **Penetration is not a `Shell` field.** `piercingPower` is parsed in the
   `Gun` base-info handler as a space-separated float curve into a temporary
   vector; the destination store offset is **not resolved by this pass**, so
   penetration cannot yet be named to a concrete offset.

## Conclusion

The shell damage fields are named (`Shell +0x11c` = HP damage, `+0x120` =
module damage). Penetration (`piercingPower`) is a ballistic property parsed
alongside the `Shot` entries, not a `Shell` field, and its destination offset
is unconfirmed. Remaining before any promotion: the runtime shell-index link
(which `Shell`/`Shot` is loaded at fire time) and the `piercingPower` store
offset — either a shot-path consumer trace or the live controlled shell-swap.
Nothing is promoted.
