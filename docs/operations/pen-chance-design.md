# PN — Armor penetration chance HUD (design)

**Status: DESIGN — replay-first; static-data extraction is the long pole.**
**Date:** 2026-08-13
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`)

## Purpose

Show, in the overlay HUD, the **armor penetration chance** when the viewpoint
tank's aim is on an enemy — the WoT PC-style indicator (green = high pen
chance, yellow = marginal, red = won't pen), extended with the numeric
readout the built-in indicator never shows (effective armor vs shell
penetration at range, and the aim point on the target's nameplate).

The feature is **replay-first by design**: in playback the camera drives the
viewed tank's turret (the T1 premise, `t1-turret-traversal-design.md`), and
the memory camera pose is already live-verified (CAM-013), so the replay
mode needs **no new memory discovery** — camera yaw/pitch *is* the aim line.
Live mode is a later, policy-gated step (T1 + X1).

## Why this repo can build it (and no mod can)

The decoded replay stream carries **shot-outcome ground truth**:

- **type-8 / subtype-1 (damage events)** — victim, post-hit HP, attacker,
  flag byte `+0x12`. A damaging hit = a penetrating shot.
- **type-32 (impact mirror)** — fires at the same instants as type-8 for the
  same victim; "every miss is an amt=0/no-damage event" (81/85 Oasis,
  107/120 Dead Rail). Non-damage hits = bounces/absorbs.

So the pen model is not *believed* correct — it is **scored against the
decoded pen-vs-bounce outcomes** of every viewer shot, the same evidence-first
methodology the whole repo runs on.

## Data inputs (what exists vs what's missing)

| Input | Status | Notes |
|---|---|---|
| **Aim line** (origin + direction) | ✅ replay, ⏳ live | Replay: camera pose CAM-013 (posA `+0x38` yz-swapped, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8`; aim point ~1.9 m above hull center). Live: T1 turret/gun discovery |
| **Target state** (position, hull yaw, identity, tank model) | ✅ | Position + yaw `+0x30` verified chains; identity via roster join; tank model id decoded |
| **Aim point on target** | build (PN-2) | Raycast the aim ray against the target's 3D hull (dimensions from static data) |
| **Armor model + hull geometry per tank** | ✅ **PN-1 probed (2026-08-13)** | Vehicle XML (`Data/XML/item_defs/vehicles/{nation}/{tank}.xml.dvpl`) carries per-group hull + turret `<armor>` (e.g. Churchill I hull `armor_1..16` 93.4→16.7 mm, `primaryArmor`, turret 102→30 mm). Plate SLOPE/normal is in the collision geometry — **PROBED 2026-08-13**: present at `Data/3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl` (SCPG `PolygonGroup`, DAVA KeyedArchive binary; `.sc2.dvpl` is the SFV2 scene descriptor). Recoverable in principle; needs a KeyedArchive + polygon-group parser |
| **Shell stats** (pen at range, caliber, normalization) | ✅ **PN-1 probed (2026-08-13)** | `components/shells.xml.dvpl` carries `caliber`/`kind`/`normalizationAngle`/`ricochetAngle` per shell; `components/guns.xml.dvpl` carries `piercingPower` (2-point range pair, e.g. "25 19"), shell `speed`, `maxDistance` |
| **Loaded shell type** | ⛔ open question | No fire/shell-type packet in the inventory; the type-32 6-byte "shell signature" is an *effect-entity id* (0x30xxxx range), not a stat reference. See Open questions |
| **Penetration math** | build (PN-2) | Pure module — see below |

## PN-2: the math module (pure, testable)

```
effectiveArmor = plateThickness / cos(incidenceAngle)
penAtRange     = shellPenetration - distanceDropOff(range)
verdict        = compare(effectiveArmor, penAtRange) + ricochet/overmatch rules
```

- **Raycast**: aim ray (origin + normalized direction) vs the target hull —
  a box/plate decomposition (per-plate thickness + normal) built from the
  tank's static armor model.
- **Angle of incidence**: `dot(plateNormal, -rayDirection)` → effective armor.
- **Ricochet (auto-bounce)**: AP/APCR shells bounce at impact angle ≥ 70°
  (with a ~25% penetration loss on the bounce). **This rule is
  shell-stat-independent geometry** — the single most validation-friendly
  term.
- **Overmatch**: shell caliber > 3× plate thickness → no ricochet at 70°.
- **Classification**: green/yellow/red from effective-armor-vs-pen (a banded
  deterministic verdict), plus an optional numeric %.
- **RNG is a validation target, never assumed**: the real game randomizes
  penetration (~±25%). PN-2 ships the deterministic classification; matching
  the exact RNG band is a stretch goal measured against the validation loop,
  not a first deliverable.

## PN-4: the validation loop (the proof)

1. **Geometry-only check first (shell-independent):** the ≥70° ricochet rule
   predicts bounce from the aim ray vs plate normal alone. Score that
   prediction against the decoded type-32 no-damage hits — a ricochet
   outcome should coincide with a predicted ≥70° impact. This validates the
   raycast + incidence geometry **before any shell stats exist**.
2. **Full classification:** with armor + shell data, predict pen/no-pen for
   every decoded viewer shot and score against type-8 (pen) vs type-32
   no-damage (bounce/absorb) outcomes. Report hit-rate, plus the per-shot
   margin (effective armor vs pen) to localize which plate/thickness
   assumption is wrong.
3. **Decode-lane status (2026-08-13, shipped):** the type-32 impact mirror is
   now **decoded into `CanonicalEventKind.ShotImpact`** (the `01 11`/`01 12`
   damage-with-payload variants), carrying `victimEntityId` + `hitResult` +
   `penetrated`. The hit-result byte (payload offset 19 for `01 12`, 18 for
   `01 11`) is **pinned with two-replay repeatability**: <c>0x03</c> =
   penetrating, <c>0x00/0x01/0x02/0x04</c> = non-penetrating (bounce/absorb),
   ~98% agreement with the type-8 damage ledger across THREE distinct replays
   (190/192 pen, 62/63 non-pen; the ~1% outliers are post-destruction hits
   and same-tick pen+bounce pairs). The finer bounce-vs-absorb mapping of the
   non-0x03 values is not yet pinned. The type-8 flag byte `+0x12` is still
   unread — that is a separate, narrower question now.

4. **PN-4 status (checked 2026-08-13): the cheap aim proxy is too coarse.**
   The live store (`%LOCALAPPDATA%\WotBTreader\treader.db`) DOES have yaw
   persisted (2.24M of 8.23M samples) — the earlier "yaw is NULL" finding was
   the DEV store (`.data/treader.db`) only. The ricochet check ran on a real
   121-damage session: the attacker→victim center-line incidence vs the
   victim's hull facing is NEARLY UNIFORM across 0–80° (13/14/17/16/15/18/10/
   9/9 by 10° bucket) with 18/121 (15%) at ≥70°. A strict ricochet model
   would cluster penetrating shots sub-70°, so the hull-facing proxy washes
   out the plate-specific incidence. **Honest negative:** the geometry-first
   check needs the actual aim plate (the `.scg` polygon geometry) or the
   live camera aim (CAM-013) — the center-line proxy cannot validate the
   ricochet rule. The scratch script is retained for the plate-level model.
   **The mesh lane now ships (2026-08-13):** `CollisionMeshParser` reads the
   `.scg` polygon groups (per-vertex normals) and
   `CollisionRaycast`/`EvaluateAgainstMesh` turn the aim ray into the struck
   triangle's true surface normal — the badge uses it when a mesh is present.

   **Attacker attribution (2026-08-13):** the type-8 subtype-8 packet (33 B)
   carries the ATTACKER entity id at payload +0x0C (plus the victim twice +
   the same 6-byte shell signature as the type-32 mirror). It fires for BOTH
   penetrating and bouncing shots (verified on bounces at t=79.93/86.63 whose
   shell signatures match the type-32 mirror), so it is the bounce-attribution
   source the center-line proxy lacked. Caveat: coverage is PARTIAL — in the
   sample run it produced 28 packets vs 69 type-32 shots and none before
   t≈79.9 s, so it is not a complete per-shot attribution source; penetrating
   shots are still attributed by the type-8 subtype-1 damage event (100%
   coverage). **The full geometry-first validation should be a C# harness**
   reusing `PenetrationDataService` + `CollisionRaycast` (the store's
   `tank_id`/`tank_name` columns are inconsistent — a mix of `nation:id`
   strings, raw compact descriptors, and meta.json player-vehicle names — so
   the descriptor→tank-id resolution must go through the enrichment, not a
   raw SQL join).

## Phase plan

| Step | Deliverable | Gate |
|---|---|---|
| **PN-1** | Static-data extraction: tank armor models + hull geometry + gun/shell tables from the install's data files (read-only, evidence-first, CAM-009 style); a static store + verify script. **PROBED 2026-08-13 — the data is present and readable** (vehicle XML armor groups, shells.xml caliber/kind/normalization/ricochet, guns.xml piercingPower). Collision geometry **PARSED 2026-08-13** (`CollisionMeshParser` + `CollisionRaycast`, verified on the real Churchill mesh) — the per-plate NORMAL is now available; the remaining sub-problem is mapping the XML armor groups to the mesh faces for per-plate THICKNESS | Offline |
| **PN-2** | Pen math module: raycast, incidence, effective armor, ricochet/overmatch, pen-at-range, banded verdict — pure, unit-tested, synthetic fixtures. **DONE 2026-08-13** (`Core/Overlay/ArmorPenetration.cs`, 12 tests). Both probe findings are MODELED and wired: `normalizationAngle` → `ShellSpec.NormalizationDegrees` (reduces the incidence before ricochet/effective-armor) and `piercingPower`'s 2-point range pair → `ShellSpec.FromPiercingPower` (pen0 + linear drop over `maxDistance`) | Offline |
| **PN-3** | Replay-mode HUD: aim = camera pose (verified); pen badge (colored + numeric) on the aimed enemy's nameplate. **DONE 2026-08-13** — `PenetrationBadge`/`StruckFace`/`PenetrationAim.ResolveBadge` (Core), `IOverlayPenetrationData` + `PenetrationContext` (Application), `PenetrationDataService` (GameIntegration, reads the install armor/shell/gun + collision-mesh data), badge threaded through the frame → projection → response, and rendered by the WPF HUD (reticle-centered, green/yellow/red + numeric). Honest limits: front-only armor (side/rear = 0 → Unknown, never guessed), stock AP shell (loaded shell not decodable), thickness still nominal (the mesh surface NORMAL now drives the incidence angle — the true plate normal the box model approximated; per-plate thickness mapping is the remaining gap) | Offline, no launch |
| **PN-4** | Validation loop: score the model vs decoded shot outcomes (geometry-first, then full); report hit-rate + per-shot margin | Offline, this is the proof |
| **PN-5** | Live mode: T1 turret/gun aim + the same module, behind the X1 policy gate | Live-gated |

## Dependencies

- ✅ Replay aim line (camera pose) — done (CAM-013).
- ✅ Target state — done (verified chains + join).
- ⏳ T1 turret discovery — required only for PN-5 (live).
- ✅ PN-1 static data — PROBED: present + readable (the long pole is now the plate-slope `.model` geometry).
- ⛔ Loaded-shell resolution — see Open questions.

## Synergy note

PN-1's read-only install game-data extraction is the **same capability** the
open Phase-4 V4 gap needs (arena-id → minimap-folder mapping is install data
too). Consolidating "install static-data extraction" as one lane lets PN-1
and the minimap-texture fix compound rather than each re-deriving a DVPL/LZ4
reader.

## Risks and honest caveats

- **PN-1 feasibility is RESOLVED — the data ships readable** (probed
  2026-08-13: vehicle XML + shells.xml + guns.xml, all DVPL/LZ4 → XML,
  decompress cleanly with the existing `DvplReader` contract).
  **Plate slope/normal is RESOLVED (2026-08-13)** — `CollisionMeshParser`
  reads the `.scg` SCPG `PolygonGroup` KeyedArchive (per-vertex normals,
  uint16/uint32 indices) and `CollisionRaycast` raycasts the aim ray against
  it; the badge now uses the struck triangle's true outward normal (verified
  against the real Churchill mesh). The remaining gap is per-plate THICKNESS.
  **Precise blocker (probed 2026-08-13):** the standalone
  `CollisionMeshes/{nation}-{tank}.scg.dvpl` is ONE merged mesh under a single
  generic `##name = PolygonGroup` (no per-plate/zone names), so the armor
  groups cannot be attached to mesh faces from it; the vehicle XML's
  `primaryArmor` lists the FRONTAL ARC, not a clean face split (the Churchill
  turret's primary `armor_1 armor_3 armor_4` includes `armor_4` = 76, a side
  plate), so side/rear are not derivable from it either; and the per-part
  collision models the `hitTester` references
  (`vehicles/british/GB08_Churchill_I/collision/Turret_01.model`) are packed
  (the `.sc2` beside the mesh is an SFV2 scene descriptor, not yet parsed).
  Until one of those is unpacked/parsed, side/rear/turret stay fail-closed
  Unknown — never a guessed convention.
- **Blitz has a built-in reticle penetration indicator** (settings toggle).
  The overlay's value-add is the *numeric* readout (effective armor vs pen,
  actual %) and the aim-line-on-nameplate — not just re-deriving the color.
- **Loaded shell is not decodable today.** Until resolved, PN-3/PN-4 default
  to the viewer's standard AP shell with a manual overlay override; the
  validation loop's geometry-first step does not need it.

## Open questions (answered by evidence, never assumed)

1. Does the install ship armor/shell/hull data in a readable format?
   **ANSWERED 2026-08-13: YES.** `Data/XML/item_defs/vehicles/{nation}/
   {tank}.xml.dvpl` (per-group hull + turret armor), `.../{nation}/components/
   shells.xml.dvpl` (caliber/kind/normalizationAngle/ricochetAngle),
   `.../components/guns.xml.dvpl` (piercingPower pair + speed + maxDistance),
   all DVPL/LZ4 → XML, decompress cleanly. The remaining sub-question is the plate-slope geometry — **PROBED
   2026-08-13**: it is the `Data/3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl`
   SCPG `PolygonGroup` KeyedArchive binary (the `.model` string in the vehicle
   XML resolves to these `.sc2`/`.scg` files) — **PARSED 2026-08-13**:
   `CollisionMeshParser` + `CollisionRaycast` now read it and the badge
   consumes the per-vertex normals (verified against the real Churchill mesh).
   The remaining sub-question is now THICKNESS per face: the armor XML
   carries per-group mm (`armor_1..16` hull / `armor_1..14` turret) but no
   group→face mapping, and none of the standalone install files declare it
   (see the PN-1 blocker note) — so the per-plate thickness mapping is the
   open item, not the geometry.
2. Does the type-8 flag byte / type-32 flag prefix distinguish pen vs bounce
   vs absorb per shot? **ANSWERED (partially) 2026-08-13:** the FLAG PREFIX
   does not — the same `01 12` fires for both pen and bounce — but the payload's
   HIT-RESULT byte (offset 19 for `01 12`, 18 for `01 11`) does: `0x03` =
   penetrating, `0x00/0x01/0x02/0x04` = non-penetrating (pinned on three
   distinct replays, ~98% agreement; shipped as `CanonicalEventKind.ShotImpact`
   with `penetrated` = `hitResult == 0x03`). The finer bounce-vs-absorb
   mapping of the non-0x03 values is NOT yet pinned — that is the remaining
   sub-question, not the pen-vs-bounce split.
3. Is the viewer's loaded shell recoverable at all from the stream (shell
   signature → stat mapping via game data), or is a manual override the only
   honest path?
4. What is Blitz's exact penetration-RNG band, and can the deterministic
   verdict be matched to it within the indicator's green/yellow/red bands?

## Evidence contract (fill on completion)

- PN-1: a static store + `verify-*.py` read-only script (exit 0 on both
  installs, no fabricated values), with the source file/format recorded.
- PN-2: unit tests covering the ricochet/overmatch/incidence/pen-at-range
  edges with synthetic plate fixtures.
- PN-4: a `score-pen-model.py` report — per-shot prediction vs decoded
  outcome, hit-rate, and the per-shot margin table.
