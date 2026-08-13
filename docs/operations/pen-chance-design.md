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
| **Armor model + hull geometry per tank** | ⛔ PN-1 | Game-static data, **not in the replay** (same class as capture zones). Read-only DVPL/LZ4 install extraction (CAM-009 precedent) |
| **Shell stats** (pen at range, caliber, normalization) | ⛔ PN-1 | Same static-data lane |
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
3. **Open decode dependency:** whether the type-8 flag byte `+0x12` and the
   type-32 flag prefixes (`01 11`/`01 12` damage-with-payload vs
   `01 02`/`01 03` short companion) already distinguish pen vs bounce must be
   confirmed in the decode lane before step 2 — if they do, ground truth is
   exact; if not, ground truth is damage-vs-no-damage (still a strong
   discriminator for ricochet, weaker for partial pens).

## Phase plan

| Step | Deliverable | Gate |
|---|---|---|
| **PN-1** | Static-data extraction: tank armor models + hull geometry + gun/shell tables from the install's data files (read-only, evidence-first, CAM-009 style); a static store + verify script | Offline |
| **PN-2** | Pen math module: raycast, incidence, effective armor, ricochet/overmatch, pen-at-range, banded verdict — pure, unit-tested, synthetic fixtures | Offline |
| **PN-3** | Replay-mode HUD: aim = camera pose (verified); pen badge (colored + numeric) on the aimed enemy's nameplate | Offline, no launch |
| **PN-4** | Validation loop: score the model vs decoded shot outcomes (geometry-first, then full); report hit-rate + per-shot margin | Offline, this is the proof |
| **PN-5** | Live mode: T1 turret/gun aim + the same module, behind the X1 policy gate | Live-gated |

## Dependencies

- ✅ Replay aim line (camera pose) — done (CAM-013).
- ✅ Target state — done (verified chains + join).
- ⏳ T1 turret discovery — required only for PN-5 (live).
- ⛔ PN-1 static data — the long pole; feasibility unproven (see Risks).
- ⛔ Loaded-shell resolution — see Open questions.

## Synergy note

PN-1's read-only install game-data extraction is the **same capability** the
open Phase-4 V4 gap needs (arena-id → minimap-folder mapping is install data
too). Consolidating "install static-data extraction" as one lane lets PN-1
and the minimap-texture fix compound rather than each re-deriving a DVPL/LZ4
reader.

## Risks and honest caveats

- **PN-1 feasibility is the main risk.** Armor models and shell tables may
  not be in a readable config format, or may be engine-serialized (not
  DVPL/LZ4 JSON/XML). Evidence-first: if the install does not ship them
  readable, the fallback is packaging community-derived stat data — a
  different policy decision, flagged, not assumed.
- **Blitz has a built-in reticle penetration indicator** (settings toggle).
  The overlay's value-add is the *numeric* readout (effective armor vs pen,
  actual %) and the aim-line-on-nameplate — not just re-deriving the color.
- **Loaded shell is not decodable today.** Until resolved, PN-3/PN-4 default
  to the viewer's standard AP shell with a manual overlay override; the
  validation loop's geometry-first step does not need it.

## Open questions (answered by evidence, never assumed)

1. Does the install ship armor/shell/hull data in a readable format, and
   where (DVPL/LZ4 config vs engine-serialized)?
2. Do the type-8 flag byte / type-32 flag prefixes distinguish pen vs bounce
   vs absorb per shot, or only damage-vs-no-damage?
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
