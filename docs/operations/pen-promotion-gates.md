# Penetration v0.3 — semantic-field promotion gates (G1 items 2/5)

**Date:** 2026-08-18 (UTC)
**Last verified:** 2026-08-18 (after the static shell-index chain closed and the
aim/ray static pass completed; hash-bound `1cda5c31…`; nothing promoted)

**Purpose:** the single place that maps the penetration semantic-field promotion
(Phase 6 `G1` items 2 and 5) to its **current, source-verified status** and the
**exact evidence that flips it**. This document is the target of the
`pen-promotion-gates.md` references in `blocker-log.md` (BLK-0027),
`offset-discovery-ledger.md`, and the 2026-08-18 shell-index handoffs.

These fields are **not** numeric offsets in `memory-offsets/11.19.0.10.json`
(which is frozen at eight `Verified` chains and untouched by this track).
"Promotion" here means flipping the two `G1` discovery verdicts — weapon state
(item 2) and shot ray (item 5) — from unproven to proven, which fixes the
provenance of the `G2` shared-contract inputs (`WeaponState`, `AimState`) and
unblocks `G4` exact-model work. Until they flip, the badge stays honest
`NotReady` with `WeaponStateUnavailable` / `AimUnavailable` (the live readiness
reasons in `PenetrationReadinessReason`).

## Current state (2026-08-18)

| `G1` item | Required result | Static state | Live state |
|---|---|---|---|
| 2 | Fresh configured gun + loaded shell via controlled transitions | **Resolved end-to-end** — see "Static chain" below | **Read live, not promoted** — `shell-state` anchor resolves on two replays, but neither contains a shell swap (see G1.2) |
| 5 | Shot-synchronous muzzle origin + gun direction (turret yaw, gun elevation) | **Bounded** — aim struct located; per-field naming not statically resolvable | **Unproven** — no live controlled-transition correlation yet |

The ownership walk that both items build on is **live-proven**
(`OD-PEN-OWNERSHIP-WALK`, 2026-08-16): one exact-build managed offline replay
returned `Resolved` — `rotator_candidate_count=1`, owner pointer readable,
forward round-trip, gun vtable, and entity HP all true across both passes.
That proves *which* object owns the gun/rotator, not what any field *means*.

## Static chain (derived, hash-bound, not yet live-read)

Ownership (live-proven): `AvatarGameLogic +0x1fc → VehicleGunRotator`
(refptr), `+0x204 → VehicleGun` (raw), `rotator+0x10 → owner`,
`+0x04 → entity` (HP at `[entity+0xB8]`, already `Verified`).

Shell-index chain (static, complete): `AmmoController` is embedded at
**`AvatarGameLogic +0x4B4`**; **`AmmoController +0x38`** is the current-shell
index (int32, written per loop by `ProcessCurrentShells` `FUN_015ef402`, reset
to 0 when no match); `+0x34` caches the last-processed shell; **`+0x40`** is
the refcounted gun/ammo ref (setter `ResetAmmo` `FUN_015eff70`, save-old →
`MOV [this+0x40],new` at `015effb3` → addref/release, called at equip time from
the `AvatarGameLogic` equip method with the new ref from `[vehicleInfo+0x68]`).
The loaded shell resolves via `[ammo+0x40] → [+0x20] → vector<ptr> at +0x1b0 →
element +0x1c → Shell`, where the `Shell` descriptor carries kind `+0x114`,
caliber `+0x118`, damage.armor `+0x11c`, damage.devices `+0x120`.

Aim/ray (static, bounded): `GetGunMarkerPosition` (`FUN_01ec12b0`) computes the
gun-marker aim struct at `[rot+0x28..0x40]` (hit pos, normalized direction,
distance); `Update` (`FUN_01ed1df0`) receives four angle/state-shaped floats
per frame from `AvatarGameLogic` and stores two at `[rot+0xe0]/[rot+0xe4]`.
Turret yaw vs gun elevation is **not** statically nameable.

## The gate: live controlled-transition correlation

Both remaining verdicts require one **exact-build managed offline replay** with
**controlled transitions**, read through the existing guarded capture seam
(loopback + capability + `OfflineReplayVerified` + exact-build gates), same
discipline as the already-merged `pen-ownership-walk` anchor (gated vftable AOB
scan, identity re-gate per hop, two-pass stability, aggregate-only).

### G1.2 — shell-swap correlation (weapon state)

Prove **`[AmmoController+0x38]` is the loaded-shell index** and the
`+0x40 → +0x20 → +0x1b0 → Shell` walk resolves the loaded shell:

- Record a controlled shell transition (switch between two distinct shell
  kinds) while the vehicle is loaded and idle.
- Correlate the index change at `+0x38` **and** the resolved `Shell` identity
  (kind `+0x114` / caliber `+0x118` / damage `+0x11c`/`+0x120`) against the
  known transition order.
- Control axis: with no transition, the index and the resolved identity must
  stay put (proves the field is transition-driven, not per-frame noise).

Acceptance: the index flips exactly when the transition flips, the resolved
`Shell` identity matches the known shell, and the control window is stable.
**Stop / fail closed:** index doesn't change on transition, resolved identity
mismatches the known shell, or the control window moves — no promotion.

**Status (2026-08-18):** the `shell-state` read surface is live-validated on
both available replays (Churchill I / Oasis, Dead Rail) — `index=0`,
`identity0=5` = `kArmorPiercingCr` (APCR), `identity1=71`, two-pass stable,
**0 transitions in both**. Neither replay swaps shells, and a passive replay
cannot supply a known transition order, so this gate is **not closed** — it
needs a freshly recorded controlled swap (manual gameplay), which is an
owner-run scenario. See `2026-08-18-deadrail-shell-swap-negative.md`.

### G1.5 — turret/gun transition correlation (shot ray)

Prove **which of the rotator's aim inputs is turret yaw vs gun elevation** and
that the aim struct is a shot-synchronous (not camera) direction:

- Record a controlled turret traverse (hull stationary) and a controlled gun
  elevation change.
- Correlate the two `Update` inputs and the `[rot+0x28..0x40]` aim struct
  against the known aim direction, with the hull yaw (`ring +0x30`, `Verified`)
  held as the discriminator (hull yaw must NOT respond to a turret traverse).
- Control axis: hull yaw stays put during the traverse; the elevation axis
  stays put during a yaw-only move.

Acceptance: one field tracks turret yaw (independent of hull yaw), one tracks
gun elevation, and the aim struct's direction matches the known gun direction —
**without** the CAM-013 camera pose being used as the ray source. CAM-013 stays
a validation reference only (see rejected shortcuts).
**Stop / fail closed:** no field cleanly separates yaw from elevation, hull yaw
responds to the traverse, or the only matching direction is the camera's — no
promotion.

## Repeatability rule

Each gate requires **two content-distinct positive repeats** (two distinct
replays / fresh processes), matching the Phase-4 `twoReplayRepeatability`
discipline already used for the eight published chains. A single positive run
records evidence but does not flip the gate.

## What is NOT promoted by this track

- **No numeric offset** enters `memory-offsets/11.19.0.10.json`; these are
  battle-scoped object fields, not module-rooted numeric offsets.
- **Armor stays `NotReady`** — G1 item 1/7 is an explicit no-go (recorded
  2026-08-18) and is independent of items 2/5.
- **A single controlled transition does not establish freshness** — the
  `WeaponState`/`AimState` contracts must carry an observation clock and
  verification state, not a one-shot read.
- **No shared-contract edit ships from this gate.** Any new read surface (e.g.,
  a shell-state or aim-state region) is a G2 proposal that requires lead review
  plus a read-only security audit before implementation — the
  `pen-ownership-walk` anchor is the only approved precedent.

## Rejected shortcuts (unchanged from `penetration-v0.3-plan.md`)

- Do not treat a manual shell selection as observed weapon state.
- Do not label CAM-013 camera direction as a shot-synchronous muzzle ray.
- Do not spend approved live launches until the offline discriminators and
  capture contracts are ready (they now are — see "Static chain").

## Sequencing

The offline half is complete. The next step is the **live controlled-transition
session** (shell-swap + turret/gun traverse), which is a consequential live
operation requiring owner approval and a controlled scenario. Until that runs,
BLK-0027 stays open and the badge stays honest `NotReady`.
