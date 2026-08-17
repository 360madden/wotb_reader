# Penetration v0.3 — gun-descriptor producer trace

**Date:** 2026-08-17 (UTC)
**Status:** equip-writer trace + symbol pass complete; the configured gun /
loaded shell are located in descriptor classes, not in `VehicleGun`
**Blocker:** `BLK-0027` (still open — phase 2–4 shell/aim/ray fields remain
underived)

## Question

Where does the per-instance configured gun / loaded shell state get written at
equip time?

## Findings (hash-bound, `1cda5c31…`)

Two new tools run against the pinned build:

- `tools/ghidra-scripts/TraceGunEquipWriters.java` — enumerates the 9
  gun-aware functions (vtable refs + creators + callers) and reports every
  gun field-block read/write.
- `tools/ghidra-scripts/ListGunSymbols.java` — lists gun/shell/turret/descr
  symbols (evidence `.build/ghidra-evidence-gun-fields/`, local, ignored).

1. **There is no equip-time write into `VehicleGun`.** The only writers of
   `+0x38/+0x3C/+0x40` are the ctor and allocating factory (hardcoded
   `100.0f / 9 / 1.0f`). The `AvatarGameLogic` ctor stores the gun at `+0x204`
   but never reconfigures its field block; the `+0x38/+0x3C/+0x40` writes in
   that ctor land on the avatar's own sub-objects.

2. **The configured gun / shell live in descriptor classes**, surfaced by RTTI
   and config-key symbols:
   - `Gun` (vftable RVA `0x31a7080`): keys `maxAmmo`, `pumpGunMode`,
     `pumpGunReloadTimes`, `GetShotsPerMinute`; parsed by
     `GunsReader::ParseBaseGunInfo`.
   - `Shell` (vftable RVA `0x31a1e14`) + `eShellKind` (`ARMOR_PIERCING`,
     `ARMOR_PIERCING_CR`, `ARMOR_PIERCING_HE`, `HIGH_EXPLOSIVE`,
     `HOLLOW_CHARGE`); parsed by `ShellsReader`.
   - `Turret` / `TurretsReader`.
   - `VehicleDescr` (vftable RVA `0x31a3510`): config sections
     `.chassi/.engine/.fuelTank/.turret/.gun`, `MakeConfigFromVehicle`,
     `s_vehicleGun`, `s_shells`.

3. Aim angles are a separate concern: `CurrentGunAnglesComponent` /
   `DestinationGunAnglesComponent` and `GetGunAngle` / `SetGunAngle` /
   `GetTurretAngle` / `SetTurretAngle`.

## Conclusion

`VehicleGun` is the runtime fire/reload state machine; its `+0x38/+0x3C/+0x40`
are hardcoded defaults and never receive the per-instance config. The
configured-gun identity and shell list live in the `Gun` / `Shell`
descriptors reachable from `VehicleDescr`. The remaining work is either the
`Gun`/`Shell` descriptor field layout + the runtime shell-index link, or the
live controlled shell-swap.
