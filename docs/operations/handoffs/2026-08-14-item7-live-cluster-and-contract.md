# Item-7 live cluster + batch witness contract

**Date:** 2026-08-14 (UTC)

**Status:** camera two-replay proof closed; batch timing measured on two
replays; shared batch witness contract applied; post-contract no-tear live
pass remains

## Outcomes

- The launch-marker ACL fix cleared the pre-gate blocker. Both launches
  reached `OfflineReplayVerified` with
  `reason=session.offline_replay_verified`.
- The clean completion-marker path is live-verified end to end: the driver
  observed definitive in-session teardown, persisted the marker, and the same
  replay's chain rerun exited 7 in 335 ms with game count 0 -> 0.
- The live frame consumed the published damage-dealt chain: at replay time
  158.9 s the own row carried `DamageDealt=145`; all 13 decoded roster rows
  present in the frame joined exactly and there were zero join mismatches.
- Camera Branch B is closed at the Phase-4 two-replay standard: 12/12 probes
  resolved across medvedkovo + savanna, every probe module-rooted, all three
  identity gates true, byte-identical, and zero `pose-double-read` failures.
- Batch read-pass timing now exists on both content-distinct replays: six
  validated, clock-attested passes, 62/63 requested entities resolved, and no
  unstable-snapshot exhaustion. The captures preceded the new retry/tear DTO
  fields, so they cannot prove that no transient mismatch retried.

## Live evidence

| Replay / session | Camera witness | Batch witness | Other |
|---|---|---|---|
| medvedkovo / `01a0021f-e7f3-7434-9286-d9ea0a3eaca1` | 6/6 resolved, identity 6/6, module-rooted 6/6, consistent 6/6, failures 0 | 3 passes at 61.86 / 149.29 / 221.62 s; 7/7 resolved each; 8.796 / 7.889 / 13.372 ms; clock lag 0 ms; decoded position cross-check 21/21 | Damage HIT (score 1.0, flatness 1.0); completion marker persisted; live-frame own damage 145 |
| savanna / `01a00228-024c-7e6e-afb0-2dc12e52b061` | 6/6 resolved, identity 6/6, module-rooted 6/6, consistent 6/6, failures 0 | 3 passes at 199.53 / 220.70 / 250.88 s; resolved 14/14, 13/14, 14/14; 24.054 / 21.448 / 23.808 ms; clock lag 0.001 / 0 / 0 ms; unstable exhaustion 0 | Damage HIT (score 1.0, flatness 1.0); completion marker persisted |

## Honest negatives and limits

- medvedkovo mid-battle enumeration was 7/14 (precision 1.0, recall 0.5).
  The fail-fast run stopped before memory reads; the retained run used those
  seven enumerated ids and did not claim full-roster enumeration.
- savanna's first batch schedule lost rendezvous transiently before the third
  target and wrote no artifact. The same live session was retried; only the
  complete three-pass retry is retained.
- savanna position matching was 33/41 inside the existing +/-2 s decoded-clock
  window (one unresolved entity). Eight moving samples fell outside that
  window. This is an honest cross-check negative, separate from the read-pass
  stability witness; no position verdict is promoted from it.
- The live batch responses were captured before `RegionReadAttempts` and the
  tear flags were exposed. Resolved items prove a stable pair was ultimately
  delivered under the existing coordinator implementation, but cannot prove
  zero transient retries. `HardwareAtomicReadProven` stays false.

## Owner-approved contract apply

The pre-existing step-2 proposal was applied after the live cluster:

- stable batch region pairs report `ConsistentDoubleRead=true`;
- `RegionReadAttempts` and `RegionTearObserved` report the primary span;
- `EntityBaseTearObserved` complements existing `EntityBaseAttempts`;
- exhausted items fail closed and keep the flag false;
- the single-read surface, resolver, offsets, and runtime addresses are
  unchanged;
- hardware atomicity remains unclaimed.

Focused validation before the milestone gate:

- GameIntegration `EntityRegionsRead` tests: 11/11 passed;
- Host.Web `EntityRegions` tests: 4/4 passed;
- Release host/game processes: both zero before validation.

Milestone gate:

- `scripts/validate.ps1`: passed;
- Release build: 0 warnings, 0 errors;
- tests: 1,206 passed, 7 local opt-in skips, 0 failed;
- completion/replay-selection/camera/batch PowerShell suites: 14/14, 16/16,
  4/4, 4/4;
- repository scan, script hygiene, offline pack, blocker/ledger consistency,
  and offset schema/chains checks: passed.

## Next

1. Harden the batch driver's transient rendezvous read before another launch.
2. Run one post-contract two-replay batch pass and require every resolved item
   to report `ConsistentDoubleRead=true`, `RegionReadAttempts=1`, and both
   tear flags false.
3. Keep `HardwareAtomicReadProven=false` until that direct wire evidence is
   recorded; do not infer zero retries from the pre-contract captures.
