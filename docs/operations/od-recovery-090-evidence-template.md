# OD-RECOVERY-090 live-run evidence — L3 damage-dealt counter (PRE-STAGED)

**Status: PRE-STAGED (2026-08-11)** — the fill-in contract for the approved
damage-dealt live session (roadmap L3). It correlates the scoreboard
damage-dealt counter — the increment mirror of L1 HP — on the VIEWPOINT
entity (the player's own stat; unlike HP, the viewpoint player landed hits
in both replays). The two-replay Phase-4 rule applies to this field too:
the offset found live on Oasis Palms must agree on Dead Rail.

## What we know (do not assume the offset)

- The counter is an **int32 that RISES by the exact damage of each event
  the target DEALT** (attacker-side; increment direction). Ground truth:
  Oasis viewpoint 3760577 dealt 5 events / 2184 damage (4 nonzero 10 s
  windows, 177.8–274.0 s); Dead Rail viewpoint 2549401 dealt 5 / 1569
  (4 windows, 154.5–257.9 s).
- The counter increments **synchronously with the packets** (no
  memory-apply lag — the driver keeps `--lag-tolerance 0` for this track;
  the HP 12 s default does NOT apply).
- **The rehearsal's `+0x48` is a SYNTHETIC FIXTURE** (same class as the
  yaw +0x2C): the mechanism-proof tests planted an int32 at +0x48 to prove
  the correlator machinery; it is NOT Ghidra-derived and NOT a prediction.
  The 087 lesson applies: per-entity scoreboard stats live on the ENTITY
  BASE record (HP turned out at `[entity+0xB8]`, not the transform). The
  live session DISCOVERS the counter offset empirically — the template's
  "candidate is a DIFFERENT offset / no hit" branch must be honored, and
  the chain field edited only with live proof.
- Dumps anchor on **`entity-base`** (`-RegionAnchor 'entity-base'`,
  `-RegionLength 320` — the driver default, covers the statically-verified
  HP block + headroom; the damage-dealt counter is expected somewhere in
  the same record).
- The correlator's Increment direction keys the event sum on
  `AttackerEntityId` and matches `delta == +Σ` (Strict) / `delta >= +Σ`
  (Lenient); flatness over stationary controls demotes monotonic risers.

## Run (one command; QUALIFY → DUMP → VERDICT)

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-hp-diffing-session.ps1 `
  -SessionId <decoded-session-guid> -Track damage-dealt `
  -VictimEntityId 3760577 -LiveAcquire -ControlTimes 30,230 `
  -SnapshotsPath .data/damage-dealt-snapshots-090.json `
  -DataRoot "$env:LOCALAPPDATA\WotBTreader" -FailOnNoHit
```

Preconditions (same class as 087/088): the launcher reached
`OK OfflineReplayVerified` with a launch-matched host-store session
(`battleSession=` at the G2 anchor moment); `-DataRoot` feeds the QUALIFY
extractor `--db`; no other host/game processes (one guarded lease).
`-VictimEntityId` 0 defaults to the session's viewpoint entity (the
player's own stat).

## Evidence to land in `.data/`

- `damage-dealt-snapshots-090.json` (schema
  `wotbtreader.od.hp-diff.snapshots.v1` family — entity-base dumps, one
  pair per dealt event + flat control dumps), every dump requiring
  `sameDecodedClockProven=true` (fail-closed, G2 bound ≤ 2 s).
- The verdict from `wotbtreader-cli hp-diff <snapshots.json> --session
  <id> --attacker-entity <id> --direction increment`: candidate offset,
  score, matched/total windows, flatness over control dumps, Strict
  exact-sum matches (≥ 2 required for a HIT).

## Known static values (do not change without re-verifying)

| Item | Value |
|---|---|
| Target build | 11.19.0.10 |
| Executable SHA-256 | `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` |
| Replay 1 (THIS session) | Oasis Palms — viewpoint attacker **3760577** (5 dealt, 2184 damage, 4 nonzero windows) |
| Replay 2 (Phase-4 repeat) | Dead Rail — viewpoint attacker **2549401** (5 dealt, 1569 damage, 4 nonzero windows) |
| Region anchor | `entity-base` (the record HP lives on; the counter is expected on the same record) |
| Region length | 320 (covers the statically-verified HP block + headroom) |
| Direction | increment (`hp-diff --direction increment`); event sum keyed on attacker |
| Lag tolerance | 0 (counter rises synchronously with the packets — unlike HP) |
| Rehearsal | synthetic int32 at `+0x48`, 5/5 both replays — **fixture, NOT a prediction** |
| Verdict contract | score 1.0, flatness 1.0, ≥ 2 exact-sum Strict matches, matched windows 5/5 (first hit must be found); Phase-4: same offset on Dead Rail |

## Ledger section — `OD-RECOVERY-090` (fill on completion)

```yaml
sessionId: OD-RECOVERY-090
status: PENDING
track: damage-dealt (increment, viewpoint counter)
replay: Oasis Palms (attacker 3760577)
verdict: <Hit at +0x.. | different offset | no hit>
score: <0..1>
matchedWindows: <n/5>
strictExactSumMatches: <n>
flatness: <0..1>
phase4Repeat: <PENDING on Dead Rail attacker 2549401>
```

## After this session

- On HIT: the counter offset is the L3 chain candidate — the scoreboard
  damage-dealt read for the live frame's scoreboard column becomes
  memory-backed (currently a decode-projection feature). The Phase-4 Dead
  Rail agreement (2549401) still gates any publication.
- HP publication keeps its OWN Phase-4 rule (Dead Rail victim 2549399);
  yaw publication keeps 089. Item 7 (hardware atomicity) stays LAST.
