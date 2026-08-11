# OD-RECOVERY-090 live-run evidence — L3 damage-dealt counter (2026-08-11)

**Status: HONEST-NEGATIVE (2026-08-11) — no hit in the 320-byte entity-base
region; recorded, nothing promoted.** Session `019ff250-…` (relaunch after
launch-1's `EntityNotFound` abort at 269 s, per the 088/089 one-relaunch
precedent) captured all 6 event windows + controls, and the verdict was
`hit=False` — top candidate `+0x3C` at score 0.833 but **flatness 0.091**
(demoted). `+0x3C` is the ALREADY-MEASURED entity-base position-copy x
float (turret survey: position copy at `+0x3C/40/44`): as an int32 it
varies by millions as the tank drives, and the lenient `delta >= +Σ` rule
coincidentally matched 5/6 windows; the flat control dumps (which require
an INACTIVE counter) correctly demoted it. The verdict confirms the
flatness control works exactly as designed. The template below remains the
fill-in contract for any future re-attempt (wider region / sibling
record), and the damage totals are CORRECTED (see below).

## Result (session `019ff250-abb8-7435-a7bb-c6f7c97ec0e1`)

| Evidence | Value |
|---|---|
| QUALIFY (current decode) | **6 dealt events / 752 damage** (NOT 5/2184 — the pre-staged numbers were stale; corrected below) |
| windows | 6 event pairs (177.8/245.4/253.2/260.3/267.1/274.0 s) + controls 30/230 s; 50 region dumps, 320 B, entity-base anchor |
| sameDecodedClockProven | true per dump (G2 bound) |
| verdict | `hit=False` — `reason='top candidate score 0.833 < 1.0'` |
| top candidate | `+0x3C` — score 0.833, matched 5/6, **flatness 0.091** |
| candidate identity | entity-base position-copy x float (measured in the turret survey); as int32 = millions-scale varying value |
| other offsets | no offset reached score 1.0 with flatness 1.0 in the 320-byte region |
| launch 1 | aborted at 269.3 s `EntityNotFound` (memory-walk flake on the flipped CAM-003 phase; tank never destroyed — decoded alive to 281 s) — no snapshots written (`-FailOnNoHit`); relaunched once per precedent |

**Interpretation (honest, no guess):** the increment-correlator found NO
int32 in the 320-byte entity-base region that rises exactly by the dealt
damage and stays flat otherwise. The counter either lives beyond `+0x140`
in the entity base, on a sibling record (the HP +0xB8 block is inside this
window, so the scoreboard stat block may not be), or is not a plain
per-event int32 on this record. No offset was edited; the read surface and
offset table are untouched.

**Next step (if re-attempted):** widen the region (the batch surface cap
allows ~0x1000) and/or scan sibling records; apply the SAME
increment-correlator with strict-exact-sum ≥ 2 and flatness 1.0. Dead Rail
repeat (attacker 2549401) remains the Phase-4 gate for any future hit.

## What we know (do not assume the offset)

- The counter is an **int32 that RISES by the exact damage of each event
  the target DEALT** (attacker-side; increment direction). Ground truth
  (CURRENT decode, re-measured 2026-08-11): Oasis viewpoint 3760577 dealt
  **6 events / 752 damage** (134+152+144+151+170+1 at 177.8/245.4/253.2/
  260.3/267.1/274.0 s — the pre-staged "5/2184" was stale and is
  superseded); Dead Rail viewpoint 2549401 dealt 5 / 1569 (4 windows,
  154.5–257.9 s).
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
| Replay 1 (THIS session) | Oasis Palms — viewpoint attacker **3760577** (**6 dealt, 752 damage** — corrected 2026-08-11) |
| Replay 2 (Phase-4 repeat) | Dead Rail — viewpoint attacker **2549401** (5 dealt, 1569 damage, 4 nonzero windows) |
| Region anchor | `entity-base` (the record HP lives on; the counter is expected on the same record) |
| Region length | 320 (covers the statically-verified HP block + headroom) |
| Direction | increment (`hp-diff --direction increment`); event sum keyed on attacker |
| Lag tolerance | 0 (counter rises synchronously with the packets — unlike HP) |
| Rehearsal | synthetic int32 at `+0x48`, 5/5 both replays — **fixture, NOT a prediction** |
| Verdict contract | score 1.0, flatness 1.0, ≥ 2 exact-sum Strict matches, matched windows 5/5 (first hit must be found); Phase-4: same offset on Dead Rail |

## Ledger section — `OD-RECOVERY-090` (2026-08-11 result)

```yaml
sessionId: OD-RECOVERY-090
status: DONE (honest-negative)
track: damage-dealt (increment, viewpoint counter)
replay: Oasis Palms (attacker 3760577, 6 dealt / 752 dmg)
verdict: no hit in 320-byte entity-base region
score: 0.833 (top candidate +0x3C — the position-copy float, demoted)
matchedWindows: 5/6 (lenient coincidence on a moving float)
strictExactSumMatches: 0
flatness: 0.091 (control-demoted — counter must be flat when inactive)
phase4Repeat: N/A (no hit to repeat; Dead Rail 2549401 still gates any future hit)
```

## After this session

- On HIT: the counter offset is the L3 chain candidate — the scoreboard
  damage-dealt read for the live frame's scoreboard column becomes
  memory-backed (currently a decode-projection feature). The Phase-4 Dead
  Rail agreement (2549401) still gates any publication.
- HP publication keeps its OWN Phase-4 rule (Dead Rail victim 2549399);
  yaw publication keeps 089. Item 7 (hardware atomicity) stays LAST.

## After this session

- On HIT: the counter offset is the L3 chain candidate — the scoreboard
  damage-dealt read for the live frame's scoreboard column becomes
  memory-backed (currently a decode-projection feature). The Phase-4 Dead
  Rail agreement (2549401) still gates any publication.
- HP publication keeps its OWN Phase-4 rule (Dead Rail victim 2549399);
  yaw publication keeps 089. Item 7 (hardware atomicity) stays LAST.
