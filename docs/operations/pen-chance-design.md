# PN — Armor penetration chance HUD (design)

**Status: IMPLEMENTED — the badge renders in BOTH replay and live frames (live aim = the CAM-013 chase-camera pose); per-part armor + mesh raycast + `.sc2` parser landed; live validation (PN-4) is the remaining proof.**
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
Live mode renders the SAME badge now (2026-08-13): the live frame scores the
CAM-013 chase-camera pose (the chase camera aims at the turret-level aim
point ~1.9 m above the hull center), so no T1 turret/gun discovery is needed
for the badge to render — T1 remains the validation lane for the exact gun
lock-on.

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
| **Aim line** (origin + direction) | ✅ replay + live | Replay (in-game playback): camera pose CAM-013 (posA `+0x38` yz-swapped, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xA8`; aim point ~1.9 m above hull center). Live: the same CAM-013 chase-camera pose (the chase camera aims at the turret-level aim point) — wired 2026-08-13, no T1 discovery needed to render the badge. **OFFLINE decode**: the viewpoint tank's HULL facing (type-10 yaw) only — no turret/aim rotation exists in the replay stream (`offline/replay-format.md`), so the offline aim is the hull direction, not the gun |
| **Target state** (position, hull yaw, identity, tank model) | ✅ | Position + yaw `+0x30` verified chains; identity via roster join; tank model id decoded |
| **Aim point on target** | build (PN-2) | Raycast the aim ray against the target's 3D hull (dimensions from static data) |
| **Armor model + hull geometry per tank** | ✅ **PN-1 PARSED (2026-08-13)** | Vehicle XML (`Data/XML/item_defs/vehicles/{nation}/{tank}.xml.dvpl`) carries per-group hull + turret `<armor>` (e.g. Churchill I hull `armor_1..16` 93.4→16.7 mm, `primaryArmor`, turret 102→30 mm). Plate SLOPE/normal is in the collision geometry at `Data/3d/Tanks/CollisionMeshes/{nation}-{tank}.scg.dvpl` (SCPG `PolygonGroup`, DAVA KeyedArchive binary) — **PARSED by `CollisionMeshParser.ParseAll`** (three groups: hull/turret/gun) and the `.sc2.dvpl` SFV2 scene descriptor is **PARSED by `SceneFileParser`** |
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
- **Ricochet (auto-bounce)**: AP/APCR shells bounce at impact angle ≥ 70° —
  checked on the **raw** impact angle, **before** normalization (normalization
  never digs a shell out of a bounce). A bouncing shell loses 25% of its
  penetration, but for a single-target indicator that means NoPen on the
  aimed tank. **This rule is shell-stat-independent geometry** — the single
  most validation-friendly term.
- **Overmatch**: shell caliber > 3× plate thickness → no ricochet at 70°.
  The 3-caliber rule applies to AP/APCR only — **HEAT ricochets at its 85°
  angle regardless of caliber** (threaded via `ShellSpec.Kind`, fixed
  2026-08-13).
- **Normalization**: 5° (AP) / 2° (APCR) / 0° (HE/HEAT), applied only when
  there is no ricochet, amplified by the **two-caliber rule** (caliber > 2×
  thickness → norm × 1.4 × caliber / (2 × thickness)).
- **Classification**: green/yellow/red from effective-armor-vs-pen (a banded
  deterministic verdict), plus an optional numeric %.
- **RNG is a validation target, never assumed**: the live game randomizes
  PENETRATION by **±5%** (Update 6.0+; the ±25% figure is the DAMAGE spread).
  PN-2 ships the deterministic classification; matching the exact RNG band is
  a stretch goal measured against the validation loop, not a first
  deliverable.

**Mechanics authority + install-data evidence (2026-08-13):** the WoT Blitz
support article "Armor Penetration Mechanics"
(https://wargaming.net/support/en/products/wotb/article/15409/) — ricochet
70° from the surface normal (AP/APCR; the article says HEAT/HE never
ricochet), 3-caliber overmatch, 25% pen loss on a bounce, 5°/2°/0°
normalization, the two-caliber normalization amplification, ±5% penetration
spread (Update 6.0+), ±25% damage spread. The install's per-nation
shells.xml carries the per-shell values the game actually reads (probed
across all nine nations: 716 AP / 400 APCR / 658 HE / 267 HEAT):
ricochetAngle 70° (AP/APCR), 85° (HEAT), ABSENT (HE → never ricochet);
normalizationAngle 5° or 15° (AP), 2° (APCR), ABSENT (HE/HEAT → 0). **The
model follows the DATA (the game's source), and two article-vs-data
conflicts are recorded, not papered over:** (1) some AP shells carry 15°
normalization (the article says 5° flat), and (2) HEAT carries ricochetAngle
85° (the article says "never ricochet"). A ricochet angle ≤ 0 is treated as
"never ricochet" (HE), not invalid. `ArmorPenetration` implements all of the
single-target terms (ricochet ordering, overmatch, normalization +
two-caliber, pen-at-range).

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
   coverage). **The geometry-first SCORING harness now ships (2026-08-13):**
   `PenValidation.Score` (Core) scores a list of `ScoredShot`s — the aim ray
   (from ANY source), the victim's tank state + collision parts + armor, the
   shell, and the decoded `penetrated` outcome — and reports the ricochet
   agreement (a predicted ricochet must be non-penetrating) plus the band
   accuracy (Pen ⇒ penetrated, NoPen ⇒ not), with per-shot rows. 6 tests.
   The aim-source WIRING (a CLI command reading the store + install data,
   reconstructing the attacker→victim aim) is the deferred half — it needs a
   real aim source to be worth running (the store's `tank_id`/`tank_name`
   columns are inconsistent — a mix of `nation:id` strings, raw compact
   descriptors, and meta.json player-vehicle names — so the descriptor→tank-id
   resolution must go through the enrichment, not a raw SQL join).

   **Shell direction is NOT decodable offline (probed 2026-08-13):** the
   position stream carries no short-lived projectile entities — every entity
   id has ≥500 samples (tanks, the duplicate-self streams, and debris, no
   shells) — so the per-shot aim cannot be reconstructed from a projectile
   trajectory. The remaining PN-4 aim sources are the attacker→victim
   center-line (already shown too coarse, point 4) and the LIVE camera aim
   (CAM-013) captured at shot time, which needs a live session — so PN-4's
   offline geometry validation is blocked, not just deferred. `PenValidation`
   is the scoring core the live CAM-013 capture will feed.

## `.sc2` SFV2 format spec (PARSED 2026-08-13 — `SceneFileParser`)

The `.sc2.dvpl` scene descriptor (DVPL→LZ4→binary) is the per-part placement
source the turret/gun collision groups need. Reverse-engineered on
`uk-GB08_Churchill_I.sc2` (3 395 bytes) and now **parsed end-to-end** by
`SceneFileParser` (pure span-in → `SceneDescription` out, pinned by the
opt-in `SceneFileParser_WhenExplicitlyOptedIn_ReadsRealSceneTransforms`
test). The full container and value walk are CONFIRMED from the bytes — no
remaining unknowns.

- **SFV2 header:** magic `SFV2` + two uint32 (opaque version/format counts) +
  a `KeyedArchive` v1 header archive (`KA` + uint16 version + three uint32 +
  two floats, opaque) — skipped to reach the scene archive.
- **Scene archive:** `KA` + uint16 version=2 + uint32 keyCount (52 for the
  Churchill), then the key table — keyCount × (uint16 length + ASCII), keys
  carrying a `#`/`##` FastName marker prefix — followed by the **hash table**
  (keyCount × uint32 LE FastName hashes, in key order) which resolves
  hash → key name. The node names (`hull`/`turret_01`/`turret_02`/`gun_01..11`)
  and the `tc.local*` transform keys are in the table.
- **Value section (complete):** a uint32 entry count, then count ×
  `<uint32 keyHash, 1-byte type, value>` entries. The observed type codes in
  WoT Blitz's newer DAVA are: 1 bool, 2 int32, 3 float32, **4 FastName**
  (the 4-byte hash — NOT 18; the newer DAVA reordered the `eVariantType`
  enum, a correction to the earlier v1-era guess), 5 string (uint32 len +
  ASCII), 6 bytes (uint32 len + bytes), 7 uint32, 8 archive, 9 int64,
  10 uint64, 0x0b vec2, 0x0c vec3, 0x0d vec4 (float32 LE), 0x13 aabbox3,
  0x15 float64, 0x1b vector (uint32 count + typed elements).
- **Nested archive (type 8):** a leading uint32 byte LENGTH, then `KA 02 01`
  + uint32 keyCount + keyCount × `<uint32 keyHash, value>` — nested archives
  drop the key table and hash table and write hash-keyed entries directly;
  the length is validated so a corrupt archive fails closed. This is the
  concrete per-value layout the earlier probe called "the remaining unknown".
- **Hierarchy semantics:** `#hierarchy` is a type-0x1b vector of node values
  (each a type-8 archive), not raw archives. A node archive carries a `name`
  FastName and a `components` archive mapping `0000`/`0001`/… to component
  archives; the one with `tc.localTranslation` (a vec3) is the
  `TransformComponent`, whose `tc.localRotation` quaternion (x,y,z,w) and
  `tc.localScale` vec3 complete the placement.

**Identity-transform finding (2026-08-13, decisive):** on the real Churchill,
every collision part (`hull`, `turret_01`/`turret_02`, `gun_01/07/08/11`)
carries an IDENTITY transform in the collision `.sc2` — the `.scg` polygon
groups 1/3/5 are ALREADY in one shared Z-up rest-pose space (the turret and
gun sit at Z≈0.86 m atop the hull). So **no per-part placement is needed**:
`CollisionMeshParser.ParseAll` reads all three groups and the badge raycasts
them as a union, taking the nearest hit. The `SceneFileParser` still ships
(and is opt-in-tested) to VERIFY that assumption per-tank rather than trust
it blindly — a tank whose transforms are non-identity would fail that test
and flag the placement gap. Caveat (unchanged): the turret's RUNTIME rotation
is not in the replay stream, so a placed turret is the REST pose — per-part
armor is a better approximation than hull-only, never exact offline.

## Phase plan

| Step | Deliverable | Gate |
|---|---|---|
| **PN-1** | Static-data extraction: tank armor models + hull geometry + gun/shell tables from the install's data files (read-only, evidence-first, CAM-009 style); a static store + verify script. **PROBED 2026-08-13 — the data is present and readable** (vehicle XML armor groups, shells.xml caliber/kind/normalization/ricochet, guns.xml piercingPower). Collision geometry **PARSED 2026-08-13** (`CollisionMeshParser` + `CollisionRaycast`, verified on the real Churchill mesh) — the per-plate NORMAL is now available; the remaining sub-problem is mapping the XML armor groups to the mesh faces for per-plate THICKNESS | Offline |
| **PN-2** | Pen math module: raycast, incidence, effective armor, ricochet/overmatch, pen-at-range, banded verdict — pure, unit-tested, synthetic fixtures. **DONE 2026-08-13** (`Core/Overlay/ArmorPenetration.cs`, 18 tests). Both probe findings are MODELED and wired: `normalizationAngle` → `ShellSpec.NormalizationDegrees` (applied AFTER the raw-angle ricochet check, amplified by the two-caliber rule) and `piercingPower`'s 2-point range pair → `ShellSpec.FromPiercingPower` (pen0 + linear drop over `maxDistance`). **Mechanics corrected 2026-08-13** against the official support article: ricochet on the RAW angle (normalization never prevents a bounce) and penetration RNG is ±5% (not ±25%) | Offline |
| **PN-3** | Replay-mode HUD: aim = camera pose (verified); pen badge (colored + numeric) on the aimed enemy's nameplate. **DONE 2026-08-13** — `PenetrationBadge`/`StruckFace`/`PenetrationAim.ResolveBadge` (Core), `IOverlayPenetrationData` + `PenetrationContext` (Application), `PenetrationDataService` (GameIntegration, reads the install armor/shell/gun + collision-mesh data), badge threaded through the frame → projection → response, and rendered by the WPF HUD (green/yellow/red + numeric, struck face FRONT/SIDE/REAR shown, anchored to the aimed tank's nameplate with a reticle fallback). **Per-part armor landed 2026-08-13:** `CollisionMeshParser.ParseAll` + `SceneFileParser` read all three `.scg` groups (hull/turret/gun, identity transforms → one shared space) and `EvaluateAgainstMesh` raycasts them as a union, scoring turret/gun hits against the turret's frontal `primaryArmor`. **Shell selector landed 2026-08-13:** the badge offers a MANUAL shell choice — `PenetrationContext` carries every shot of the viewer's stock gun (`ShellOption` name+kind+spec), the frame scores with the `?shell=`-selected shell, the response surfaces `PenShells`/`PenShell`, and the overlay hotkey <c>Q</c> or the sidebar ComboBox selects AP/APCR/HE/HEAT (short label on the badge). **Shell kind threaded 2026-08-13:** `ShellSpec.Kind` excludes HEAT from the 3-caliber overmatch rule. Honest limits: nominal thickness per part (hull front/side/rear + turret front; side/rear of the turret/gun = 0 → Unknown, never guessed), the loaded shell is not decodable (the selector covers the stock gun's ammo, not the in-battle selection), thickness still nominal (the mesh surface NORMAL drives the incidence angle — the true plate normal; per-face thickness mapping is the remaining gap) | Offline, no launch |
| **PN-4** | Validation loop: score the model vs decoded shot outcomes (geometry-first, then full); report hit-rate + per-shot margin | Offline, this is the proof |
| **PN-5** | Live mode: the badge already renders from the CAM-013 chase-camera aim (wired 2026-08-13); T1 turret/gun aim remains for validating the exact gun lock-on | Live-gated |

## Dependencies

- ✅ Replay aim line (camera pose) — done (CAM-013).
- ✅ Target state — done (verified chains + join).
- ⏳ T1 turret discovery — the live badge now renders from the CAM-013 chase-camera aim; T1 remains for validating the exact gun lock-on (PN-5).
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
  **Mesh orientation FIXED (2026-08-13):** the `.scg` collision mesh is stored
  Z-UP (+X right, +Y FORWARD, +Z up — its rear normal is −Y, deck normal +Z)
  while the decoded world/box model are Y-up; the first mesh raycast cast the
  ray in Y-up space and misclassified a head-on shot as the deck/back face.
  `EvaluateAgainstMesh` now Y↔Z-swaps into mesh space before the raycast and
  classifies the face from the mesh-local normal (front=+Y); the real-install
  opt-in test pins a head-on Churchill shot to `StruckFace.Front` with
  effective armor ≥ the 186.7 mm nominal (the sloped glacis thickens it).
  A top/bottom deck hit (no horizontal surface component) fails closed to
  Unknown rather than borrowing a horizontal face's armor (fixed 2026-08-13).
  **Face classification FIXED for steep plates (2026-08-13):** the first
  deck-hit fix gated on the VERTICAL component dominating, which
  misclassified the Churchill I's ~22° glacis (normal (0, 0.38, 0.93)) as a
  deck hit. `ClassifyMeshFace` now classifies by the HORIZONTAL projection
  alone (dominant X/Y axis → side/front/back; negligible horizontal → deck),
  so a shallow glacis is Front while a true deck/belly hit stays Unknown.
  **Three-part structure (PARSED 2026-08-13):** the `.scg` is NOT one merged
  mesh — the header's count (`a=3`) is real: three polygon groups keyed `#id`
  1/3/5 = hull / turret / gun (the three `hitTester` collision models).
  `CollisionMeshParser.ParseAll` reads ALL three into `CollisionMeshPart`s,
  and the collision `.sc2` (parsed by `SceneFileParser`) carries IDENTITY
  transforms for them — the parts share ONE Z-up rest-pose space, so the badge
  raycasts them as a union and takes the nearest hit. Per-part armor is now
  wired: a turret/gun hit (`#id` 3/5) scores against the turret's declared
  frontal `primaryArmor` (`TankArmor.TurretFrontMm`), a hull hit against the
  hull front/side/rear. The vehicle XML's `primaryArmor` lists the FRONTAL
  ARC, not a clean face split (the Churchill turret's primary
  `armor_1 armor_3 armor_4` includes `armor_4` = 76, a side plate), so
  turret/gun side/rear THICKNESS still has no face mapping and stays
  fail-closed Unknown — never a guessed convention.
- **Blitz has a built-in reticle penetration indicator** (settings toggle).
  The overlay's value-add is the *numeric* readout (effective armor vs pen,
  actual %) and the aim-line-on-nameplate — not just re-deriving the color.
- **Loaded shell is not decodable today.** The pen badge instead offers a
  MANUAL selector (overlay hotkey <c>Q</c>, `?shell=` query param): every
  shot of the viewer's STOCK gun (the loaded-gun is not decodable either),
  surfaced as `PenShells`/`PenShell` on the frame and scored with the
  selected shell's ricochet/normalization/pen profile. The first is the
  default (stock) shell. Until the loaded shell is decodable the selector
  covers the stock gun's ammo, not the in-battle selection.

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
   **ANSWERED 2026-08-13:** penetration spread is **±5%** (Update 6.0+), per
   the official "Armor Penetration Mechanics" support article; the ±25%
   figure is the DAMAGE spread. The deterministic verdict is NOT matched to
   the ±5% band — the green/yellow/red margin (default ±10%) is a display
   threshold deliberately wider than the RNG; matching the exact ±5% band is
   a PN-4 stretch goal.

## Evidence contract (fill on completion)

- PN-1: a static store + `verify-*.py` read-only script (exit 0 on both
  installs, no fabricated values), with the source file/format recorded.
- PN-2: unit tests covering the ricochet/overmatch/incidence/pen-at-range
  edges with synthetic plate fixtures.
- PN-4: a `score-pen-model.py` report — per-shot prediction vs decoded
  outcome, hit-rate, and the per-shot margin table.
