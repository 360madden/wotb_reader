# Handoff — HP discovery: offline side complete + live plan pre-staged (2026-08-10)

## Summary

The record-diffing discovery playbook for HP is fully implemented and proven
OFFLINE. The only remaining piece is the LIVE trusted reader, which is now
scoped as an approved-session step with a pre-staged plan. The position track
(previous phase) is closed; this phase built the HP-discovery mechanism with
the same discipline: pure Core, synthetic + compose proofs, full gate green.

## What exists now (all green)

1. **Ground truth (query side)** — `IHpGroundTruthProvider` /
   `SqliteHpGroundTruthProvider` expose the Damage/Destroyed `canonical_events`
   for a session (replay time, victim entity id, best-effort damage/attacker
   from `values_json`; fail-closed on unknown sessions). Commit `728c7a6`.
2. **Correlation core** — `RecordChangeBucketer` (time-labeled change windows
   from trusted-reader region dumps; strictly-increasing fail-closed;
   unchanged pairs skipped) and `HpDamageCorrelator` (ranks 4-byte-aligned
   int32 fields whose value drop matches −Σ target damage per window).
   `DamageMatchMode.Strict` (exact) and `.Lenient` (drop ≥ Σ damage — the
   destroying hit's overkill). Commits `0fd9191`, `da71136`.
3. **Proofs** — 13 Core synthetic tests (HP-at-+0x48 ranks first; unrelated
   counters never rank; sparse snapshots sum multi-event windows; other
   entities' damage ignored; no-drop windows yield nothing; Lenient overkill
   match + small-drop rejection + exact subsumption; realistic Damage +
   Destroyed + unrelated mix) + 1 compose test in the Sqlite provider tests
   (real `values_json` extraction → bucketer → correlator finds HP 2/2).

## The pre-staged live step (approved-session gate)

The read surface has no generic region read, so the trusted reader needs ONE
bounded, gated product addition (see `docs/operations/record-diffing-groundwork.md`
"Live session plan"): `EntityRecordRegionReadRequest/Result` mirroring the
existing `EntityPositionReadRequest/Result` (caller supplies entity id +
bounded region ≤ 4 KB; coordinator owns process identity, gate, guarded read,
replay-clock labeling, and privacy — bytes + replay time only, never an
absolute address). Session flow: gate → resolve entity → dumps concentrated
on damage segments → bucket → correlate (Lenient) → verdict (≥ 2 matched
windows, score 1.0, repeated across two replays). `publicProcessAddressesOrRawBytes:
false`; publish only the offset + chain form through the operator gate.

## State

- Position track: closed (published Verified, walkable, all equivalence
  branches proven, docs reconciled).
- HP discovery: offline complete; live reader = approved-session step.
- `playerYaw`: still quarantined. `replayTime`: next anchor per roadmap
  preference order, requires live work (rolling-survivor candidates,
  OD-012..038).

## Gates

`validate.ps1` exit 0 on every commit this phase (Core 120, Sqlite 21, all 12
projects green); `offset_check.py` PASS; links 112/112; tree clean.
