# Penetration v0.3 — phase 2–4 semantic field static derivation

**Date:** 2026-08-16 (UTC)
**Status:** hash-bound static derivation recorded; nothing promoted
**Blocker:** `BLK-0027` (open — live controlled transitions still required)

## What changed

The ownership walk is live-proven, so this session ran the ordered static
write/read-site trace on `VehicleGun` / `VehicleGunRotator`. New Ghidra
scripts dump primary vtables, listing-confirmed displacements, and named
assert-string methods. Findings are recorded in
[`pen-weapon-semantic-fields.md`](../pen-weapon-semantic-fields.md).

## Static findings (hash-bound, 11.19.0.10 `1cda5c31…`)

- Primary vtables are dtor + RTTI only. Domain methods are named non-virtuals.
- `VehicleGun +0x3C/+0x40/+0x44/+0x4C` are reload/state (enum + progress +
  time + flag), not a shell identity. Loaded shell is a static no-go on these
  two objects.
- `VehicleGunRotator::GetGunMarkerPosition` writes a working ray at `+0x28`;
  `Update`'s helper publishes it at `+0x50` (pos + dir + scalar) and rebuilds
  a 4×4 at `+0xEC..+0x128` from `+0x130` (vehicle descr) plus three floats.
- Those three floats are not named rotator fields: one is a scaled owner
  getter, two are `+0x10/+0x14` on a sibling looked up from `rotator+0x4`.
  Turret yaw vs gun elevation is still not separable statically.
- `AvatarGameLogic::updateTargetingInfo` feeds four protobuf floats into
  `Update`; only two are stored (`rotator+0xE0/+0xE4`).

Nothing was promoted. CAM-013 and the manual shell selector stay diagnostics.

## Validation

- Ghidra scripts: `TraceWeaponSemanticFields.java`,
  `TraceWeaponNamedMethods.java`, plus `DumpFunctions` of the Update helper,
  targeting/reload writers, and the two sibling float getters.
- Hash pin matched. Script logs have no `SCRIPT ERROR`. Evidence stays under
  ignored `.build/ghidra-evidence-weapon-fields/`.
- No product code, shared contract, or read surface changed.

## Next step

One approved managed offline replay runs the live protocol in
`pen-weapon-semantic-fields.md`: published marker at `+0x50`, reload enum
confirm, hull-stationary traverse, honest shell A→B→A (expected unproven on
these objects), then a decoded shot join. Two content-distinct positives
before any G2 contract.
