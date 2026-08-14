# Pen-chance — per-face armor thickness mapping (2026-08-13): NOT FEASIBLE

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.
Continues `2026-08-13-pen-live-badge-and-aim-scoping.md`.

## Question

Can the `.scg` collision mesh or the vehicle XML map the 16 hull / 14 turret
`armor_N` thickness groups to specific faces, so the pen badge uses per-plate
thickness instead of the nominal front/side/rear?

## Answer: no, from the accessible install data

Investigated the real Churchill I install (`C:/Games/World_of_Tanks_Blitz`)
and the whole `Data/3d/Tanks` tree. Every candidate source terminates in a
dead end:

1. **Vehicle XML** (`XML/item_defs/vehicles/uk/GB08_Churchill_I.xml.dvpl`):
   `armor_1..16` (hull) + `armor_1..14` (turret) carry a thickness number
   only. `primaryArmor` names the frontal group set (`armor_2 armor_3 armor_4`
   for the hull). There is **no per-group geometry and no side/rear/top
   declaration** — nothing says which `armor_N` is a side plate vs a rear
   plate vs a deck.
2. **Collision mesh** (`3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl`):
   SCPG with **2–11 polygon groups** (Churchill = 3: `#id` 1/3/5 = hull /
   turret / gun). These are the PHYSICS collision parts (movement/tank-tank),
   not the 16 armor plates; they carry per-vertex normals and no
   armor-group reference. Distribution across all 735 files: {2: 63, 3: 424,
   4: 61, 5: 42, 6: 71, 7: 31, 8: 24, 9: 15, 10: 2, 11: 2}.
3. **Collision scene** (`…/uk-GB08_Churchill_I.sc2.dvpl`): identity placement
   transforms for hull/turret/gun only (already parsed by `SceneFileParser`).
4. **Visual model** (`3d/Tanks/GB/Churchill_I.sc2/.scg`): 114 render polygon
   groups, but the scene key table has **no `armor_*` names** — only
   hull/turret_01/turret_02/gun_01..11/chassis_*/Instance-NNN render nodes.
5. **The real per-plate models are not shipped loose.** The XML's `hitTester`
   references `vehicles/british/GB08_Churchill_I/collision/Hull.model` and
   `Turret.model` — the actual armor-hit-test meshes that would name their
   polygon groups by armor plate. Neither file exists anywhere in the install
   (no `.pack` archives; the only `.model` files are 9 unrelated UI
   ViewModels). `Parameters/uk/GB08_Churchill_I.yaml.dvpl` adds per-part
   bounding boxes and a hull `averageThickness` (turret_01 81.23 / turret_02
   85.03) but no per-face armor.

## What this means for the badge

The current honest ceiling stands and needs **no code change**: front via
`primaryArmor` (thickest declared group), hull/turret side/rear = 0 →
`Unknown` fail-closed, turret/gun hits scored against the turret's frontal
primary armor. The collision mesh's true surface normal already drives the
incidence ANGLE; only the THICKNESS stays nominal. No speculative face
convention (e.g. "armor_5 is the side") was added — that would violate the
evidence-first constraint.

## Docs updated

- `pen-chance-design.md` — PN-1 row, Risks and honest caveats, and Open
  question 1 now record the negative finding instead of "the remaining
  sub-problem".
- `product-roadmap.md` — PN-1 row marked RESOLVED: NOT FEASIBLE.

## Verified

Docs-only change; `scripts/validate.ps1` gate green (no code touched).
Offline pack regenerated for the new handoff.

## Remaining (unchanged — all live- or owner-gated)

1. PN-4 live validation (CAM-013 aim at shot time → `PenValidation.Score`).
   Offline PN-4 is re-confirmed BLOCKED on four gaps (store forensics this
   turn): no attacker for the bounce half, center-line ≈ hull aim (no turret
   aim), zero `ShotImpact` rows in the existing store (pre-capability
   sessions), and NULL `position_samples.yaw` for the shot-relevant entities
   — recorded in the design doc's PN-4 section.
2. Single-launch cluster (completion-marker verify + batch rehearsal +
   Branch-B camera double-reads + `DamageDealt` E2E).
3. `ConsistentDoubleRead` flag-flip approval (owner).
4. L4 replayTime + T1 turret-facing sessions.
