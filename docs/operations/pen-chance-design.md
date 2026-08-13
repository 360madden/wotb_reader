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
3. **Decode-lane status (checked 2026-08-13):** the type-8 flag byte `+0x12`
   is present in the layout but **NOT decoded today** — `HealthChangeObservation`
   carries victim/postHitHealth/attacker/destroy only, no flag field — and
   type-32 is documented (impact mirror, flag prefixes `01 11`/`01 12` vs
   `01 02`/`01 03`) but **not decoded into events** (raw evidence only). So
   today's ground truth is **damaging-hit vs no-damage-hit** (type-8 damage vs
   the type-32 mirror's no-damage hits), NOT per-shot pen-vs-bounce-vs-absorb.
   That is sufficient for the geometry-first ricochet check and the full
   pen/no-pen classification; disambiguating bounce-vs-absorb needs the flag
   byte. The raw bytes ARE stored per event, but **the evidence offset is
   relative to the DECOMPRESSED event-stream archive entry, not the raw
   `.wotbreplay` file** (probed 2026-08-13: `EventStreamReader.Scan` runs over
   `archive[EventStreamEntry]`, and offsets exceed the on-disk file size) — so
   the flag-byte analysis must go through the archive + event-stream reader
   (the decoder's own path), not a simple file seek. The cleanest route is a
   small decoder-side surface change that surfaces the flag byte (and   the type-32 flags) at decode time.

4. **PN-4 status (checked 2026-08-13): the existing store cannot score yet.**
   The decoded sessions predate the migration-5 rotation persistence —
   `position_samples.yaw` is NULL for every sample (0/28236 in the rehearsal
   session), and the type-32 no-damage hits are raw-only. So the validation
   loop's aim-proxy needs the victim HULL FACING (null today) and the
   bounce ground truth (needs the decoder path). The fix is offline: a
   FRESH immutable re-decode with the current decoder (which persists the
   rotation tail), then the aim-proxy + ricochet check runs against it. The
   scratch `ricochet-check` script is written and ready for that re-decode.

## Phase plan

| Step | Deliverable | Gate |
|---|---|---|
| **PN-1** | Static-data extraction: tank armor models + hull geometry + gun/shell tables from the install's data files (read-only, evidence-first, CAM-009 style); a static store + verify script. **PROBED 2026-08-13 — the data is present and readable** (vehicle XML armor groups, shells.xml caliber/kind/normalization/ricochet, guns.xml piercingPower). Remaining: the plate-slope `.model` collision geometry (binary) | Offline |
| **PN-2** | Pen math module: raycast, incidence, effective armor, ricochet/overmatch, pen-at-range, banded verdict — pure, unit-tested, synthetic fixtures. **DONE 2026-08-13** (`Core/Overlay/ArmorPenetration.cs`, 12 tests). Refinement: the probe found `normalizationAngle` (per-shell, not yet modeled) and `piercingPower`'s 2-point range pair (maps to pen0 + linear drop) | Offline |
| **PN-3** | Replay-mode HUD: aim = camera pose (verified); pen badge (colored + numeric) on the aimed enemy's nameplate | Offline, no launch |
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
  decompress cleanly with the existing `DvplReader` contract). The remaining risk is the **plate slope/normal**, which lives in the
  `.scg` collision geometry (probed 2026-08-13: present, SCPG `PolygonGroup`
  DAVA KeyedArchive binary) — without a parser for it the model uses nominal
  thickness (a first-order approximation); with it, true effective armor. If
  the KeyedArchive/polygon-group format proves unreadable, the fallback is a
  per-armor-group slope table (community-derived) — flagged, not assumed.
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
   XML resolves to these `.sc2`/`.scg` files), needing a KeyedArchive +
   polygon-group parser.
2. Does the type-8 flag byte / type-32 flag prefix distinguish pen vs bounce
   vs absorb per shot? **Status (2026-08-13):** the flag byte is unread and
   type-32 is raw-only today (see the validation loop's decode-lane status) —
   so the open question is whether a flag-byte analysis of the stored evidence
   bytes surfaces the finer distinction.
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
