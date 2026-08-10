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
- `HpDamageCorrelator.Correlate(windows, damageEvents, targetEntityId)` —
  for each window, sums the target entity's damage events whose replay time
  falls in (From, To]; a candidate is a 4-byte-aligned int32 whose value
  drop equals −Σ damage (strict match). Ranked by score (matched / damage
  windows), then precision (matched / changed damage-windows), then offset.

Proven by synthetic fixtures: HP-at-+0x48 ranks first across three damage
windows; an unrelated changing counter is never a candidate; sparse
snapshots sum multiple events in one window; other entities' damage is
ignored; a damage window with no HP drop yields no candidates. **v1
limitations (documented, not bugs):** strict equality only — overkill
(HP drop > Σ damage, e.g. a killing blow) and healing/multi-source splits
that don't sum exactly do not match; events outside the observed snapshot
span are observation gaps and do not inflate the denominator.

### Remaining: the live trusted reader (approved-session step)

The correlation core needs only the memory side fed in: a trusted reader
dumping the entity record around the avatar spine (via the walkable
position chain / `entityLookup`) at replay-clock-labeled times, bucketed by
the core above and matched against the damage events this provider returns
(`victim entity id → HP-drop event at replay time T`). No live session has
run; the reader is the next approved-session step.

## Notes

- Damage events are the highest-value correlation target: HP changes only on
  damage, so measurement windows are event-bound, not continuous (unlike
  position). The event timeline lets discovery pick the replay segment where
  damage happens instead of watching the whole battle.
- Entity-id binding (which entity record is the player/enemies) reuses the
  same join: `CanonicalEvent.EntityId` ↔ `participant.entity_id` ↔ the
  memory resolver's entity ids.
