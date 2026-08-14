# Pen-chance — ShotImpact attacker attribution decoded (2026-08-13)

**Phase 6** (`docs/operations/pen-chance-design.md`). Offline, no launch.
Continues `2026-08-13-pen-per-face-thickness-not-feasible.md`.

## What shipped

The type-8 **subtype-8** packet (the bounce-attribution source the type-32
`ShotImpact` mirror lacks) is now **decoded into the canonical event stream**
instead of lingering as raw evidence only:

- `EventPacketDecoders.TryReadShotAttribution` reads a subtype-8 packet
  (33 B payload: victim u32 at +0x00, subtype 8 at +0x04, attacker u32 at
  +0x0C) into a `ShotAttributionObservation`.
- The decode loop collects these; `BuildEvents` joins each `ShotImpact` with
  its attribution by **(victim, clock rounded to centiseconds)** — the same
  key the two-replay probe used — and emits an optional `attackerEntityId`
  on the event's `values_json` (null when no attribution exists, never
  fabricated).
- Synthetic fixture + test extended: a penetrating hit (attributed), a
  bounce (attributed), and an un-attributed hit (null) are all pinned.

## Why

Closes one of the four offline-PN-4 blockers recorded in the thickness
handoff: the bounce half previously had no first-class attacker. The other
three remain (center-line ≈ hull aim, zero `ShotImpact` rows in old
sessions → needs re-decode, NULL `position_samples.yaw` for shot entities).

## Verified

Full `scripts/validate.ps1` gate green (exit 0): 1079 tests + 7 Pester +
offset validator + repo scan + offline pack. The `ShotImpactMirror…` test
now asserts `attackerEntityId` for pen/bounce/null.

**Re-decode verification (2026-08-14):** a fresh decode of the ground-truth
session (`019ffdcd`) persisted 69 `ShotImpact` rows — **69/69 attributed**
(zero null attackers), and all **51/51 penetrating shots cross-check EXACT
against the independent type-8 subtype-1 damage ledger** (0 mismatches).
The earlier "28 packets vs 69 shots" partial-coverage probe is superseded.

## Center-line finding (2026-08-14)

With full attribution + yaw, the center-line incidence measured from the
DOMINANT face normal (front/rear/side) is **< 45° for all 67 shots**
(pen 50 / bounce 17; median ~13°, max 44.7°) — the ≥70° ricochet threshold
is NEVER reached offline, so the decoded bounces are armor-vs-pen failures,
not angle. Three of the four offline-PN-4 blockers are cleared by the
re-decode; the center-line aim (no turret/gun in the stream) remains the
sole gap, and it is fatal to offline ricochet validation. Scratch:
`.data/pn4-centerline-incidence.py`.

## Remaining (unchanged)

Live PN-4 (CAM-013 aim at shot time), the single-launch OD cluster,
`ConsistentDoubleRead` owner approval, L4 replayTime + T1 turret-facing.
