# Damage-dealt discovery track pre-staged (increment direction) — 2026-08-10

Status: PRE-STAGED (no live session, no product read-surface change). The
HP track and replayTime plan remain the two approval-gated live options;
this adds a third candidate whose offline half is now fully rehearsed.

## What was done

1. **Correlator generalization** (`src/WotBTreader.Core/Discovery/RecordDiffing.cs`):
   new `DamageCorrelationDirection { Decrement, Increment }` on
   `HpDamageCorrelator.Correlate` (default `Decrement` — every existing
   caller unchanged). Increment keys the event sum on `AttackerEntityId`
   (the scoreboard counter's increments) and matches `delta == +Σ`
   (Strict) / `delta >= +Σ` (Lenient), with direction-aware explanation
   text and doc comments. `HpDamageEvent` already carried `AttackerEntityId`
   and `Damage` — no storage change needed.

2. **Ground-truth verification**: the player's own entity (viewpoint) dealt
   damage in BOTH 11.19.0 replays — savanna 3760577 (5 events, 2184,
   4 windows at 177.8–274.0s), medvedkovo 2549401 (5 events, 1569, 4 windows
   at 154.5–257.9s). Unlike HP (where the viewpoint took zero damage), the
   player's own stat IS a viable correlation target.

3. **CLI** (`CliInvocation.cs`, `CliCommandRouter.cs`): `hp-diff
   --direction increment|decrement` (default decrement), echoed in the
   output envelope; unknown values rejected with `cli.hp-diff.direction`.

4. **Extractor** (`scripts/python/replay-delta-extractor.py`): `--damage-dealt
   [--attacker-entity <id>]` — the increment mirror of `--hp-delta`
   (default target = viewpoint entity), emitting the event-bound
   `dump_schedule` + an `hp-diff` command with `--direction increment`.
   The schedule drops 0/unparseable-damage events (medvedkovo had one — a
   dump pair around it would waste two lease-bounded dumps).

5. **Driver** (`scripts/invoke-hp-diffing-session.ps1`): new `-Track
   damage-dealt` (default `hp`); qualification, schedule print, and verdict
   all switch direction automatically. `-VictimEntityId` is now optional
   (0 = extractor defaults to the viewpoint entity) but still required for
   `-Track hp`.

6. **Tests**: 6 new `RecordDiffingTests` (increment field ranked first,
   default direction unchanged, victim-side events ignored under Increment,
   Strict excludes magnitude-mismatched risers, flatness demotes monotonic
   risers, Lenient admits overcap rises while Strict rejects) and 2 new
   CLI tests (end-to-end increment HIT at `+0x48`, unknown direction
   rejected). Core 21/21, CLI hp-diff 5/5.

7. **Docs**: `docs/operations/record-diffing-groundwork.md` — new
   "Damage-dealt track" section (ground-truth table, tooling, rehearsal,
   caveats).

## Rehearsal (offline evidence, both replays)

Full session flow via `invoke-hp-diffing-session.ps1 -Track damage-dealt
-SnapshotsPath` on synthetic regions whose damage-dealt int32 rises by the
exact cumulative damage at each real hit tick:

| Replay | Target | Verdict | Offset | Score / Flatness | Matched |
|---|---|---|---|---|---|
| savanna | 3760577 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |
| medvedkovo | 2549401 | HIT | `+0x48` | 1.0 / 1.0 | 5/5 |

Both agree on `+0x48` — the Phase-4 two-replay rule proven for the
increment direction. Note: HP ALSO rehearsed to `+0x48`, so a live
confirmation would give the tank-record layout a second anchor.

**Construction trap caught in rehearsal**: my first savanna build put
the FINAL cumulative value (2184) into a trailing control dump at 240s
(before the 245.42s hit), so the (178.02, 240] control window showed the
field changing 511 → 2184 → flatness 0 → honest no-HIT. Fixed to the step
function (value at any dump time = Σ damage of events ≤ t); both verdicts
then HIT. Same lesson as the HP rehearsal's boundary-window bug, on the
control side.

## Validation

- `dotnet test` (Core + CLI hp-diff): all green (21/21, 5/5).
- Extractor `--self-test`: pass.
- Driver parses clean; both rehearsals exit 0 with `-FailOnNoHit`.
- PSSA/gate: pending full run.

## Next (approval-gated)

One approved live session with the `EntityRecordRegionReadRequest` addition
(the same single bounded product change the HP plan needs) dumping the
player's entity region at the damage-dealt schedule times, verdict via
`-Track damage-dealt`. The offline half is exhausted and rehearsed; the
remaining input is the live read or a new research direction.
