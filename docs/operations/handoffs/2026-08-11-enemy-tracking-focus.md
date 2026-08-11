# Handoff — enemy-tracking focus: capability map + type-7 packet survey (2026-08-11)

## Focus shift

Temporary focus: reliable enemy-tank information for the HUD/overlay — enemy
position, HP, hull facing, turret facing, and any lock-on / auto-aim /
"has me targeted" state — plus the question of whether same-match replays
from other players' perspectives are beneficial.

## Capability map (evidence-grounded)

**Replay decode — available NOW (all enemies, not just the viewpoint):**
- Position x/y/z ~10 Hz per entity — type-10 → `position_samples` (642k rows).
- HP + max HP + alive/dead — type-8/subtype-1 + type-5 + destroy markers;
  the HP ledger is proven exact against battle_results.
- Hull yaw/pitch/roll — type-10 payload +36/+40/+44, canonicalized as
  `position_samples.yaw/pitch/roll` (newer decodes; older battles predate
  the migration).
- Team, tank, name, bot status — `participants`.
- Distance + "hull-aim-line" (is an enemy's hull pointed at me) — computed
  from position + hull yaw.

**Replay decode — PROVEN ABSENT (new survey):**
- Turret facing — the full type inventory covers 100% of the stream; no
  packet carries a second per-tank angle.
- Lock-on / auto-aim / targeted state — client-side UI state, not in the
  server-authoritative stream (same class as the V3 no-spotting finding).

**Live memory path (later):** the entity-collection walk already in the
camera script reaches all entities' ring records; per-entity HP, turret,
and target fields are discovery targets; the batch `entity-regions` surface
is the pre-staged read mechanism.

## Type-7 entity-status survey (new evidence, 2026-08-11)

Ran a payload-level survey of the 19 040 type-7 packets on Oasis Palms
11.19.0 (raw evidence bytes vs canonicalized hull yaw):

- Layout: entity-id u32 + 2 entity-specific state int32s + fast-rotating
  16-bit tail.
- The tail sweeps the full 16-bit circle at 2 000–3 600 deg/s across every
  roster entity — a tick counter/bitfield, NOT an angle (real turret
  traverse is ~20–60 deg/s). No scale (fixed-point or half-float) makes it
  track hull yaw.
- One effect entity (4124165, non-roster) carries a 16-byte layout with a
  rotating float X that moves while hull yaw is static; X ≠ yaw and X ≠
  pitch — an effect parameter, not a tank field.
- Type-32 damage-mirror variants carry shell signatures + ids, no bearing
  float aligned to the shot victim (checked damage events vs payload
  floats; no match).

**Conclusion:** the replay carries no turret angle and no lock/target
state. The enemy-track HUD is built on position + HP + hull yaw; turret and
true lock state require live-memory discovery later. Replay-format doc
updated (type-7 row + a new NO-turret/lock paragraph).

## Same-match replays from other players — assessment

**Beneficial, as an audit + camera-calibration multiplier, not a
requirement:** every replay carries the full battle (all entities), so
enemy tracking needs nothing extra. A second player's file of the same
battle gives (a) a free cross-validation of the per-entity position/HP/yaw
timeline (the `comparison_runs` scaffolding exists and is empty — built for
this), and (b) a second camera trajectory for W2S calibration. It cannot
reveal the other player's lock state (same absence reason). Sourcing is
the constraint: WoTB saves each player's own perspective only; the other
player must share the file.

## Round 2 (same session) — AimGeometry + real-data hull-aim validation

- **`AimGeometry` shipped** (`src/WotBTreader.Core/Overlay/AimGeometry.cs`,
  9 synthetic tests): `HullAimErrorRadians` + `HullAimsAt` — the hull-arc
  check in the proven packet yaw convention (yaw 0 = +Z, heading =
  atan2(dx, dz)), fail-closed on non-finite/zero-distance/out-of-range
  tolerance. The enemy-track frame ALREADY carried position + hull yaw +
  HP per tank (`OverlayTankState`), so the aim-line was the only missing
  piece; it stays client-computable (no shared-contract change).
- **Real-data validation (78 shots, Oasis Palms):** at fire instants the
  attacker's hull is 48–68° off the bearing to the victim on average
  (moving attackers med 48°, static med 68°); only 15–20% of shots land
  within a 15° hull arc. Convention itself re-verified (hull yaw vs motion
  heading med 0.0° moving forward; the p90 180° is the known reversal
  case). **Conclusion: the turret fires independently of the hull — hull-
  only "aims at" is a WEAK proxy and must not be presented as aim
  detection.** The utility stays as the honest necessary-condition layer
  (a hull pointed at you is a real, weak threat signal; a hull pointing
  away means the turret cannot be on you within the hull arc). True aim
  detection needs turret data — absent from the replay, a live-memory
  discovery target.

## Next units (offline, ready to start)

1. ~~Enemy-track overlay frame~~ — already present in the frame contract;
   the aim-line is now a tested Core utility.
2. Multi-perspective comparison via `comparison_runs` when a second
   player's file is obtainable.
3. Live enemy-roster read design (entity-regions serving all enemy ring
   records) — pre-staged for the next approved session; turret/target
   fields ride on that discovery.

## Files touched

- `offline/replay-format.md` (type-7 row + no-turret/lock finding)
- `docs/operations/handoffs/2026-08-11-enemy-tracking-focus.md` (this file)
