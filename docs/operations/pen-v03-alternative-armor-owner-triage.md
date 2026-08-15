# Penetration v0.3 alternative armor/layer owner triage

**Date:** 2026-08-15 (UTC)
**Status:** bounded static triage — no authoritative exact owner proven
**Blocker:** `BLK-0027`

## Question

After the exact-build hard-joint visualization path was rejected, is there one
nearby runtime/static owner that can provide struck surface identity, physical
thickness, ordered layers, and shell interaction without guessing an
`armor_N` mapping?

## Candidate families

The existing exact-build evidence identifies these adjacent families:

- `ArmorComponent` — likely entity component boundary;
- `ArmorConfiguration` — configuration-family boundary;
- `PushNormalsArmorConfiguration` — current normal/visualization consumer;
- `ArmorVisualizationSystem` — visualization and intersection-facing family;
- `TankComponent` / vehicle parameter resources — configuration and model
  metadata families.

RTTI identity is only a candidate locator. It does not prove a field's units,
producer, object ownership, or physical meaning.

## Evidence triage

1. The `PushNormalsArmorConfiguration` path is rejected: its current consumer
   rebuilds visualization surfaces and emits `ArmorNodeMaterial`; its builder
   supplies an empty lookup map. No ordered physical traversal, explicit
   millimeter conversion, or `armor_N` geometry join is present.
2. The loose vehicle XML contains armor-group thickness values and a
   `primaryArmor` frontal list, but no face geometry or complete side/rear/top
   ordering.
3. The loose collision `.scg` contains physics parts and surface normals, but
   no armor-group reference. The loose `.sc2` supplies placement, not armor
   layer meaning.
4. The XML-referenced per-plate `Hull.model` and `Turret.model` hit-test
   resources are not shipped as loose install files in the examined build.
5. The tracked exact-build evidence set contains RTTI candidate identities for
   the families above, but no independently documented producer/read path that
   returns the required thickness + order + struck-plate tuple.

## Verdict

**No authoritative exact armor/layer owner is proven offline.** The remaining
`ArmorComponent`/`ArmorConfiguration` families are candidates for a future
exact-build trace, not usable ports or read anchors. Adding an offset, mapping
stable hard-joint keys to XML groups, or treating visualization normals as
physical layers would be speculative and is prohibited.

This closes the current cheap static triage without spending a live session.
The armor blocker remains honest `NotReady`; the existing nominal front-armor
path remains diagnostics-only.

## Practical next decision

Do not widen the new weapon/aim capture contract to include an unproven armor
scan. Keep armor as a separate, bounded investigation. If the owner later
approves a deeper exact-build trace, it must first identify a producer that
returns all four required facts (surface identity, thickness/units, order, and
interaction kind) before any memory read or shared contract is added.
