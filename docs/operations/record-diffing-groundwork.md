# Record-diffing groundwork — replay event inventory (2026-08-10)

Purpose: inventory the "known events" side of the record-diffing discovery
playbook (dump the entity record with a trusted reader, correlate byte
changes against authoritative replay events). This doc records what exists
today so the next discovery milestone (player HP, entity-id binding) starts
from the actual data model, not assumptions.

## What the replay decoder already exposes

`WotbReplayDecoder` builds `CanonicalEvent`s with kinds
(`src/WotBTreader.Core/TelemetryModels.cs`):

| Kind | Replay source | Carries |
|---|---|---|
| `ParticipantObserved` | arena participant packets | account/entity ids, team |
| `Position` | position packets | per-participant trajectory samples |
| `Damage` | `EventPacketDecoders.TryReadDirectDamage` → `DamageObservation` | `AttackerEntityId`, `VictimEntityId`, `Damage`, `ReplayTime`, evidence |
| `Destroyed` | battle events | victim entity id + replay time |
| `BattleStarted` / `BattleEnded` | battle lifecycle | replay clock bounds |

Each `CanonicalEvent` has `Sequence`, `ReplayTime`, `ParticipantId`,
`EntityId`, `ValuesJson`, `Confidence`, `Evidence` — so damage/destroyed
events are joinable to the roster by entity id **and** have replay
timestamps. `ReplayCapability.Damage` is set when damage events decode
(`WotbReplayDecoder.cs:405`).

`BattleStats` (per participant, from battle_results.dat) adds totals:
`DamageDealt`, `Shots`, `HitsDealt`, `PenetrationsDealt`,
`EnemiesDestroyed`, `DamageBlocked`, etc. (`TelemetryModels.cs:147`).

## What is persisted

- `canonical_events` table — every decoded canonical event, including
  Damage/Destroyed, with kind, replay time, participant/entity id,
  values JSON, evidence (`SqliteDecodeRunRepository.cs:608-618`).
- Participants (`entity_id`, `tank_name`, `account_id`, `clan_tag`) and
  position trajectory samples.

## The query path (implemented 2026-08-10)

`IHpGroundTruthProvider` / `SqliteHpGroundTruthProvider` now expose the
Damage/Destroyed `canonical_events` for a session: replay time, victim entity
id, victim participant id, best-effort `damage`/`attackerEntityId` parsed
from `values_json` (null when unparseable — never guessed), plus the session
duration; fail-closed on an unknown/zero-duration session. Registered in
`AddSqliteStorage`. `SqliteTrajectoryGroundTruthProvider` (positions) is
unchanged.

## The memory-side diffing harness

### Implemented (2026-08-10, offline core)

`WotBTreader.Core.Discovery` now carries the pure, synthetic-tested core
(`RecordDiffingTests`, 9 tests):

- `RecordChangeBucketer.Bucket(snapshots)` — time-buckets trusted-reader
  region dumps (full-region bytes + replay clock label) into
  `ByteChangeWindow`s: one per consecutive snapshot pair whose bytes
  differ, with the exclusive/inclusive time span (From, To]. Snapshots must
  be strictly increasing (fail-closed); unchanged pairs produce no window.
- `HpDamageCorrelator.Correlate(windows, damageEvents, targetEntityId,
  matchMode)` — for each window, sums the target entity's damage events
  whose replay time falls in (From, To]; a candidate is a 4-byte-aligned
  int32 whose value drop matches −Σ damage per mode: **Strict** (default)
  requires the drop to equal the summed damage exactly; **Lenient** accepts
  any drop ≥ Σ damage (the destroying hit's overkill, multi-source
  under-sums). Ranked by score (matched / damage windows), then
  **flatness** (fraction of zero-damage control windows in which the field
  was UNCHANGED — separates HP, flat except when hit, from monotonic
  drains that drop every window), then precision (matched / changed
  damage-windows), then offset.Proven by synthetic fixtures: HP-at-+0x48 ranks first across three
damage windows; an unrelated changing counter is never a candidate;
sparse snapshots sum multiple events in one window; other entities' damage
is ignored; a damage window with no HP drop yields no candidates; Lenient
matches the overkill killing blow (HP 500 → 0 vs 150 damage) while still
rejecting a small coincidental drop and subsuming exact matches; a
realistic event mix (Damage + Destroyed + unrelated damage) still ranks
HP first; a monotonic drain (drops ≥ every window's sum) ties HP on score
under Lenient but is demoted by **flatness** (HP unchanged in control
windows, drain keeps dropping — `Correlate_Lenient_DrainingDecoy_RanksBelowHp_OnFlatness`);
a magnitude-mismatched decoy that is flat in control windows still ties
Lenient but is EXCLUDED by the Strict confirmation
(`Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp`). **End-to-end compose proof** (`SqliteHpGroundTruthProviderTests`):
seeded `canonical_events` → `IHpGroundTruthProvider` (REAL `values_json`
damage extraction) → `RecordChangeBucketer` → `HpDamageCorrelator` finds
the HP field 2/2 — the two halves compose with the actual data shape.
**Documented limitations:** events outside the observed snapshot
span are observation gaps and do not inflate the denominator; healing (no
in-battle healing in WoTB) is not modeled; a non-int32 HP representation
(wrong field size/alignment) is out of scope for this correlator.

### Remaining: the live trusted reader (approved-session step)

The correlation core needs only the memory side fed in: a trusted reader
dumping the entity record around the avatar spine (via the walkable
position chain / `entityLookup`) at replay-clock-labeled times, bucketed by
the core above and matched against the damage events this provider returns
(`victim entity id → HP-drop event at replay time T`). No live session has
run; the reader is the next approved-session step.

#### Live session plan (pre-staged for the approval gate)

The read surface today exposes ONLY the gate-verified position read and the
diagnostic position-page endpoint (`/discover/entity-position`,
`/discover/position-page`) — there is no generic region read, so the trusted
reader needs ONE bounded, gated product addition:

1. **`EntityRecordRegionReadRequest(EntityId, RegionLength)` /
   `EntityRecordRegionReadResult(...)`** — mirrors the existing
   `EntityPositionReadRequest`/`EntityPositionReadResult` shape: the caller
   supplies only the decoded entity id + a bounded region length (≤ 4 KB);
   the coordinator owns process identity, resolves the entity address via the
   resolver (or the walker on the published walkable chain — the walker now
   exposes the FOUND ENTITY BASE in `OffsetChainWalkResult.ResolvedEntityAddress`,
   the region anchor for the dump), requires
   `OfflineReplayVerified` + current authorization, reads the region through
   the guarded reader, labels the dump with the replay clock (the G2
   same-decoded-clock anchor, ≤ 2 s bound), and returns ONLY the bytes +
   replay time — never an absolute address. Same-decoded-clock attestation
   reuses the existing coordinator path.
2. **Session driver** — `scripts/invoke-hp-diffing-session.ps1` runs the
   whole flow: gate → **qualify the victim from the decoded replay**
   (see below — do NOT default to the player's own entity) → print the
   event-bound dump schedule → acquire the bounded series of region dumps
   at replay-clock-labeled times (the GATED seam — the driver exits 3 with
   the contract until the region read lands; pass `-SnapshotsPath` to run
   the verdict against an existing dump file) → `hp-diff
   <snapshots.json> --session <id> --victim <entity> --mode lenient` for
   the verdict (with `-FailOnNoHit` for fail-closed automation). The dump
   schedule is per-hit: the extractor's `--hp-delta` emits a `dump_schedule`
   (a dump just BEFORE and AFTER each damage event, ±0.2 s, so each change
   window captures exactly one hit) plus flat control dumps in the gap
   segments → `RecordChangeBucketer` → `HpDamageCorrelator` (Lenient mode
   first — overkill) → verdict.
3. **Verdict contract** — the top candidate offset with score, matched /
   total damage windows, and the matched window list (replay times + deltas
   vs. the provider's events). A candidate is a HIT when it (a) matches ≥ 2
   damage windows with score 1.0 in Lenient mode, (b) has **flatness 1.0**
   (unchanged in every zero-damage control window — the control dumps are
   load-bearing), and (c) **confirms under Strict**: ≥ 2 windows where the
   drop equals the exact damage sum (excludes magnitude-mismatched decoys
   that Lenient admits — e.g. another victim's HP or a heavy drain that is
   flat in control windows; proven by
   `Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp`),
   AND the matched offsets agree across the two independent replays (the
   Phase-4 repeatability rule).
4. **Evidence + privacy** — record the session under an OD-RECOVERY id,
   keep `publicProcessAddressesOrRawBytes: false` (raw region bytes are
   session evidence, never published), and publish only the offset + chain
   form through the operator gate if the candidate repeats.

#### Victim selection — verified against decoded replay data (2026-08-10)

The session must track an entity that **actually takes damage**. Verified
from `.data/treader.db` (11.19.0 decode runs): **the player's own entity
(`mrkool1138`) took ZERO damage in both 11.19.0 replays** (Oasis Palms 0
events, Dead Rail 0 events — the viewpoint tank survives unhit), so a
session tracking the player would hand the correlator an all-flat series
and zero windows to match. Qualify the victim before the session with one
command:

```
python scripts/python/replay-delta-extractor.py --session <id> --top-victims 8 --window 10
python scripts/python/replay-delta-extractor.py --session <id> --hp-delta --victim-entity <entity_id> --window 10
```

Require **≥ 2 damage windows** (the verdict contract needs ≥ 2 matched
windows); otherwise pick the next victim by hit count. The `--hp-delta`
output's hit-window list is the event-bound dump schedule. (Numbers
cross-checked against direct SQL on 2026-08-10.)

> **2026-08-10 correction — replay-tick unit:** an earlier draft of this
> plan quoted 10×-too-large times (e.g. "900–1680s of a ~2798s replay").
> The decoded DB stores replay ticks as .NET ticks (10⁷/s —
> `position_samples` max tick ≈ `battle_sessions.duration_ticks` and the
> Oasis Palms battle is 279.9s, not 2798s), but the extractor's
> `TICKS_PER_SECOND` was 10⁶. Fixed to 10⁷; all schedules above are in
> real replay seconds, and the hit-window bucketing now uses true 10s
> windows (verified window-by-window against the raw event ticks). The
> movement-proxy participant ranking (a separate dead-code bug — it only
> compared consecutive samples) was also fixed to scan ~1s-apart pairs.

**Oasis Palms** (session `019fdff7-8dcf-7426-8547-9fb8cc3eb07b`, 11.19.0,
battle ≈ 280s) — victim **3760578** is the strongest candidate: 9 events /
4,028 damage, hits at t = 90.4–167.4s, in six ten-second windows
**90–100, 100–110, 130–140, 140–150, 150–160, 160–170s** (window sums
256 / 1278 / 664 / 386 / 933 / 511 — verified against the raw
events: 90.45, 100.93, 107.81, 136.52, 143.42, 150.31, 156.62, 157.23,
167.42s). The dump series concentrates there, plus 2–3 flat-window
control dumps (e.g. ~30s and ~230s) to confirm the field is otherwise
unchanged. Alternative victims in the same replay: 3760571 (7 hits,
118.8–175.4s), 3760574 (6 hits, 114.3–157.5s), 3760575 (6 hits, late,
245.4–274.0s).

**Dead Rail** (session `019fb86c-c8e7-7004-9df6-a574f5a7835b`, 11.19.0,
battle ≈ 271s) — the second independent replay for the Phase-4
repeatability rule: victim **2549399** — 18 events / 4,647 damage,
hits at t = 114.4–152.4s, in five ten-second windows **110–120,
120–130, 130–140, 140–150, 150–160s**. So the two-replay verdict
contract is fully pre-staged: Oasis Palms victim 3760578 + Dead Rail
victim 2549399, both with ≥ 2 damage windows, schedules above.

The walker resolves **any** entity id through `entityLookup` (the
published chain takes the target id per walk and now exposes
`ResolvedEntityAddress`), so a non-viewpoint enemy is resolvable — the
HP harness only needs the entity base + the `[entity+0x3C]` tank-record
region (Ghidra-candidate layout, test-local until live verification); it
does not depend on the live-verified viewpoint ring-record path.

**Mechanism proven end-to-end offline** (2026-08-10,
`Walk_PublishedTable_EntityBase_AnchorsHpRegionDump_CorrelatorFindsHp`):
walk the published `playerPositionX` chain on full-spine synthetic memory
→ the walker exposes the found entity base → deref `[entity+0x3C]` to the
tank record → dump the 0x100-byte region at replay-clock-labeled times
(HP dropping by the exact damage amounts) → `RecordChangeBucketer` →
`HpDamageCorrelator` (Lenient) ranks the HP int32 at `+0x48` first with
score 1.0 across both damage windows. Every stage of the planned session
flow is now proven on the real published table; only the live read
remains.

**Two-replay rehearsal (2026-08-10, offline evidence):** the full session flow
was rehearsed end-to-end on BOTH qualified replays against their real event
timelines — the extractor's `dump_schedule` times, HP at `+0x48` dropping by
the exact cumulative damage at each real hit tick (step function), flat
control dumps, `RecordChangeBucketer` → `HpDamageCorrelator` (Lenient) →
verdict, via `scripts/invoke-hp-diffing-session.ps1 -SnapshotsPath`:

| Replay | Victim | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| Oasis Palms | 3760578 | HIT | `+0x48` | 1.0 / 1.0 | 2/2 |
| Dead Rail | 2549399 | HIT | `+0x48` | 1.0 / 1.0 | 13/13 |

Both verdicts satisfy the contract (score 1.0 + flatness 1.0 + ≥ 2 exact-sum
Strict matches) and **agree on the matched offset `+0x48`** — the Phase-4
two-replay repeatability rule is proven offline in rehearsal; the live
session only replaces the synthetic dumps with the trusted reader's.
Construction note: dumps must bracket the hits at the scheduled ±0.2 s
offsets and NOT land exactly on an event tick — placing a dump at the event
time itself creates a zero-width boundary window whose sum lands in the
wrong bucket (rehearsal hit this; the step-function rebuild fixed it).

**Simulation reading:** the extractor's `--hp-delta` survival simulation
at `target=0` measures the flat-window pass rate (3760578 at 10s windows:
11/17 = 0.65 → survival ≈ 0.12 / 0.01 over 5 / 10 rounds). The honest
reading: a single-target rolling delta campaign sheds the true HP field in
any round whose window contains a hit — the per-window
`HpDamageCorrelator` (window damage sum vs. per-window drop) is the right
tool, not the rolling pilot; this is what the session flow already uses.

All offline halves are proven and green; the approval ask is exactly the
scope above (one gated region-read addition + one session), with the
correlation core and ground-truth provider already in place.

## Damage-dealt track (increment direction) — pre-staged 2026-08-10

The scoreboard damage-dealt counter is the mirror image of HP: it RISES by
the exact damage of each event the target DEALT (attacker-side). The
correlator now supports a `DamageCorrelationDirection` (Decrement/HP,
Increment/damage-dealt); the Increment direction keys the event sum on
`AttackerEntityId` and matches `delta == +Σ` (Strict) / `delta >= +Σ`
(Lenient). Ground truth verified from `.data/treader.db` (11.19.0): **the
player's own stat IS a viable target** — unlike HP, the viewpoint entity
landed hits in both replays:

| Replay | Player entity | Dealt events | Damage | Nonzero 10s windows |
|---|---|---|---|---|
| Oasis Palms | 3760577 | 5 | 2184 | 4 (177.8–274.0s) |
| Dead Rail | 2549401 | 5 | 1569 | 4 (154.5–257.9s) |

Tooling (all offline, verified 2026-08-10):

- Extractor: `--damage-dealt [--attacker-entity <id>]` — the increment
  mirror of `--hp-delta` (default target = the session's viewpoint entity),
  emitting the same `dump_schedule` shape + an `hp-diff` command with
  `--direction increment`. The schedule drops 0/unparseable-damage events
  (a dump pair around them would waste two lease-bounded dumps).
- CLI: `hp-diff --direction increment|decrement` (default decrement —
  existing callers unchanged), echoed in the output envelope.
- Driver: `invoke-hp-diffing-session.ps1 -Track damage-dealt` (default
  `hp`) — qualification, schedule print, and the verdict command all
  switch direction automatically.
- Unit proofs: 6 new `RecordDiffingTests` — increment field ranked first
  (score/flatness 1.0), default direction unchanged (increment-only field
  is NOT a candidate), victim-side events ignored under Increment, Strict
  excludes magnitude-mismatched risers, flatness demotes monotonic risers,
  Lenient admits overcap rises while Strict rejects them. Plus 2 CLI tests
  (end-to-end increment HIT at `+0x48`, unknown `--direction` rejected).

**Two-replay rehearsal (2026-08-10, offline evidence):** the full session
flow rehearsed on both replays' real attacker timelines (synthetic region
with the damage-dealt int32 rising by the exact cumulative damage at each
real hit tick, step function):

| Replay | Target | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| Oasis Palms | 3760577 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |
| Dead Rail | 2549401 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |

Both verdicts satisfy the contract and agree on `+0x48` — the Phase-4
repeatability rule proven for the increment direction too. Construction
note (same trap as HP, caught in rehearsal): trailing control dumps must
carry the step-function value at their time, not the final cumulative —
a control dump after a hit but before the next must show the value as of
that time, or the control window falsely counts as a field change and
flatness drops to 0 (the first Oasis Palms build failed exactly this way).

Same caveat as HP: the rehearsal proves the machinery on the real event
timeline; whether the in-memory damage-dealt counter actually lives at
`+0x48` (or anywhere near the tank record) is exactly what the gated live
region read discovers. Notably, HP and damage-dealt BOTH rehearsed to
`+0x48` — if the live read confirms a scoreboard counter near the HP field,
the tank record layout claim gets a second independent anchor.

## Notes

- Damage events are the highest-value correlation target: HP changes only on
  damage, so measurement windows are event-bound, not continuous (unlike
  position). The event timeline lets discovery pick the replay segment where
  damage happens instead of watching the whole battle.
- Entity-id binding (which entity record is the player/enemies) reuses the
  same join: `CanonicalEvent.EntityId` ↔ `participant.entity_id` ↔ the
  memory resolver's entity ids.
