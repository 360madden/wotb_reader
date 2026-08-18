# PN-4 live aim validation — PASS; pen UI prototype ship-ready

**Date:** 2026-08-14 (UTC)
**Roadmap:** Phase 6 (`docs/operations/product-roadmap.md`)
**Feature:** armor-penetration chance badge
**Live session:** medvedkovo, installed-client-compatible 11.19.0 replay; battle session `01a0015b-0005-72b5-beaa-c4d5a1b73157`

## Decision

The minimal penetration UI is **ready to ship as a quantified prototype**. The
badge, static-data lane, mesh raycast, pen math, replay/live frame wiring,
shell selector, aim feed, and validation loop are complete. The decisive live
PN-4 run passed: feeding the CAM-013 aim observations improved both measured
metrics over the same session's center-line baseline.

This is not a claim of game-perfect prediction. The honest product ceiling
remains nominal per-part thickness, a manually selected stock-gun shell, and
Unknown for armor faces whose thickness mapping is not available.

## Live protocol and capture

- The canonical managed launcher was used against the top-level original
  replay, not a staging copy.
- The launcher passed the installed-client version pre-flight, imported the
  artifact, moved/resized the game window, waited for the orange Watch Offline
  control, clicked it, and reached `OfflineReplayVerified`.
- No second monitor was attached for this run; the launcher therefore placed
  the 640x360 game window at the primary work-area top-left.
- The capture waited for the positive gate before starting its replay-time
  deadline. It recorded only frames with `sameDecodedClockProven=true` and a
  resolved CAM-013 pose.
- Capture result: **150 aim samples**, 155 live-frame attempts, replay-clock
  span **9.4–278.9 s**, 5 clock/camera skips, and no fabricated samples.
- The replay-end state was recognized as
  `Denied / evidence.replay_completed`; the capture wrote its output and
  exited successfully instead of treating terminal denials as poll failures.
  A transient 401 during rendezvous turnover and the terminal 400 were
  retained only as safe counters; no token or response body was logged.

## Scorer evidence

The same decoded session was scored twice through
`POST /discover/pen-offline-score/{id}`:

| Input | Total shots | Classified | Skipped | Band accuracy | Ricochet precision | Predicted ricochets |
|---|---:|---:|---:|---:|---:|---:|
| Center-line baseline (`{}`) | 78 | 23 | 8 | 69.565% | 66.667% (4/6) | 6 |
| CAM-013 aim overrides | 78 | 22 | 8 | **72.727%** | **80.000% (4/5)** | 5 |

Two classified rows changed when the live overrides were supplied, including
the center-line predicted-ricochet outlier at approximately 250.4 s. The one
fewer classified row is an honest consequence of the true aim landing on an
armor face whose nominal side/rear thickness is intentionally Unknown; it is
not converted into a guessed verdict.

## Code and hardening completed

- The capture now waits for `OfflineReplayVerified` before spending its
  capture window, resets the transient-failure timer after a successful frame,
  and stops on the durable terminal replay-completion state.
- The pen score endpoint rejects negative replay ticks, non-finite ray values,
  null aim entries, and aim lists above the bounded 100,000-entry limit before
  loading the projection.
- Host.Web tests: 175 passed; Application tests: 96 passed; Release build:
  0 warnings / 0 errors. Full `scripts/validate.ps1` gate passed after the
  handoff/documentation update: 1,093 tracked files scanned, all test suites
  green, Pester 7/7 + 16/16, offline pack 65 files / 118 links / 0 broken,
  and offset chains validated for 8 fields.

## Remaining tradeoffs (not blockers for the prototype)

1. Loaded-shell identity is not present in the replay stream; the UI keeps the
   manual selector for the stock gun's available shells.
2. Armor XML cannot be mapped to every collision face, so nominal front armor
   is scored and unsupported side/rear cases fail closed to Unknown.
3. The deterministic verdict does not model the game's per-shot +/-5%
   penetration RNG; live evidence validates the deterministic band and angle
   source, not exact shot randomness.
4. T1 turret traversal remains a separate discovery lane for exact gun
   lock-on semantics. It is not required for the current replay/live badge,
   which uses the CAM-013 chase-camera aim already verified in the live frame.

## Next step

A second-replay PN smoke check was the planned regression task. It subsequently
passed on savanna/Churchill and is recorded in
`handoffs/2026-08-14-pn4-second-replay-regression.md`; the roadmap and top-10
list now reflect that result. The full repository gate also passed after the
second-replay documentation and capture hardening changes. PN remains
ship-ready prototype work; only owner packaging/review and optional fidelity
research remain.

## Postscript — second-replay and final gate (2026-08-14)

The content-distinct savanna/Churchill regression exercised the same managed
launcher, CAM-013 capture, completion detection, aim feed, and scorer. It
recorded 161 G2-proven samples and improved band accuracy from 38.889% to
46.667%; its six baseline ricochet predictions were all removed by the true
aim, so the true-aim precision denominator was correctly `0/0`. The full
`scripts/validate.ps1` gate passed afterward with 0 build warnings/errors,
all test suites green, Pester 7/7 + 16/16, offline-pack freshness/link checks
clean, and offset-chain validation green.
