# L3 damage-dealt — avatar/player-stats family discovery plan

**Date:** 2026-08-12
**Status: PLAN (pre-staged).** Gated behind the operator-approved publication
applies (HP then yaw) — the project's serial order; this document pre-stages
the methodology so the lane can run immediately after. No code changed, no
memory touched.

## Goal

Find the LIVE damage-dealt counter for the player's own tank in memory and
prove it against the decoded damage events to the Phase-4 standard (two
content-distinct replays, strict exact sums within the bounded memory-lag
window). The live overlay frame currently carries `DamageDealt: 0` honestly
(no read exists); a verified counter would fill the own row of the live
scoreboard (V2's panel in live mode) and, if published, the live frame's
`DamageDealt` field.

## Why the entity records are dead (append-only reference)

OD-RECOVERY-090 (2026-08-11) ran three honest sweeps, all negative:

1. **320-byte entity-base** (session `019ff250`, 50 dumps, 6 event windows +
   controls): top candidate `+0x3C` scored 0.833 but demoted by flatness
   0.091 — it is the already-measured position-copy float.
2. **4096-byte entity-base wide sweep**: candidate `+0x7EC` demoted —
   moving-float decoy.
3. **Sibling entity-tank-record anchor**: live-verified dead —
   `[entity+0x3C]` is not a stable pointer (`0x36CE3AE8` at 32.8 s vs
   `0xC36046AA` at 59.8 s).

Conclusion (kept): damage-dealt is NOT reachable via the per-entity records.
It lives in a per-PLAYER object family (the avatar / player-stats object),
which this plan targets.

## Candidate object family

- The game keeps per-player battle statistics (the values the battle-results
  screen shows: damage dealt, kills, damage blocked, ...) in an object keyed
  by the player, not by the victim entity. In a replay, the memory-side
  counter must track the decoded `battleStats.damageDealt` events as the game
  applies them (same variable ~1–3.4 s memory lag established by HP/yaw
  discovery).
- **Runtime reachability already exists:** the live frame's camera pose read
  resolves the player's avatar address (`avatarAddress`, from the
  camera-helper walk; `GameSessionCoordinator.ReadLiveFrameAsync` branches on
  it). The player-stats sub-object is a candidate child of that avatar (or a
  module-rooted chain reaching it), NOT a new read surface class — reads ride
  the existing guarded reader lease + ONE G2 attestation.
- **Static search signature:** Ghidra hash-bound scan of the avatar /
  player-stats family for a counter that the damage-application code writes
  on the ATTACKER side (the HP path writes the victim; damage-dealt is the
  attacker-side write — the two writes share the same damage-application
  method, so the attacker-stats write is discoverable near the victim-HP
  write).

## Verification methodology (increment correlator)

HP/yaw discovery matched value DROPS and static reads; damage-dealt
INCREMENTS. The correlator family is the same attribution machinery
(`HpDamageCorrelator` / `RecordDiffing`) with an increment direction:

- Ground truth: the decoded `HpDamageEvent` timeline — events where
  `AttackerEntityId == own entity id`, summed per window
  (`HpDamageEvent.Damage`). The own entity id is already resolved live
  (`OwnEntityId`, name-join step 4).
- Hypothesis: `counter(t)` == cumulative Σ(own attacker damage events applied
  by t), within the bounded bidirectional lag window (`--lag-tolerance` /
  `--lag-lead-seconds`, default 0 = exact; the OD-089/091 finding: Dead Rail's
  memory clock LEADS the decoded clock ~2.5 s while Oasis LAGS ~4.8 s — the
  window family must stay additive and per-replay).
- Score: same contract as HP (score 1.0, flatness 1.0, strict exact sums per
  window; control windows flat).

## Bounded live-session protocol

1. Approved launches via `scripts/launch-offline-replay-for-od.ps1`
   (monitor-2 placement, gate, G2 anchor — all current machinery reused).
2. Driver mode: dense dumps of the candidate counter over each damage window
   (mirror `hp-diff`'s dense-span dumping), ONE guarded reader lease + ONE
   G2 clock label per batch.
3. Candidate scoring offline: increment-vs-events correlation, score +
   flatness, control windows (no own-damage windows must stay flat).
4. **Honest-negative discipline (OD-090's rule):** bounded sweeps — a fixed
   candidate budget per session; if the family yields no surviving candidate
   after three sweeps (e.g. the counter is on a differently-rooted object),
   declare the family dead append-only and stop — never widen the read
   surface on a hunch.

## Definition of done (Phase-4 standard)

`twoReplayRepeatability = true`: the same counter agrees on BOTH Oasis Palms
and Dead Rail (strict exact sums, score 1.0, flatness 1.0, controls flat).
Then a publication package (`g2-damage-dealt-publication-draft.md`) +
operator-gated apply, mirroring HP/yaw. The live scoreboard's own row and the
frame's `DamageDealt` field are the consumers; enemy/teammate per-row damage
stays honest-unknown (their stats objects are not in the player's memory
map) — the live scoreboard is own-row-only, documented as such.

## Sequencing

1. Operator-approved publication applies (HP then yaw) — pre-requisite order.
2. Static candidate search (avatar/player-stats family).
3. Bounded live sessions + increment correlation (this plan).
4. Phase-4 repeat on the second replay.
5. Publication package (operator-gated).
6. Item 7 (hardware atomicity) remains LAST regardless.
