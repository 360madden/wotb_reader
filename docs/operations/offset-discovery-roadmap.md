# Offset-discovery roadmap v3 — exact-value pause scan

**Date:** 2026-08-04
**Owner:** offset-discovery track
**Parent:** [`offset-discovery-strategy-v3.md`](offset-discovery-strategy-v3.md)

## Goal and definition of done

**Goal:** one runtime-supported offset in preference order `replayTime`,
`playerPositionX/Z`, `playerHP` — with a correctly classified address kind.

**Definition of done:** the candidate is reproducible across **2 launches × 2
replays** with member-displacement or pointer-chain classification, and is
published per `offset-discovery-workflow.md` Phase 5.

**Context:** offsets are **not** product-critical — the replay decoder serves
the HUD. This track is research; the roadmap carries its own budget and
stop rules so it cannot silently consume the product's effort.

## Milestones

### M0 — Exact-scan capability (this session, 2026-08-04) — ✅ complete

| Deliverable | Detail |
|---|---|
| Engine `exact` compare mode | `MemoryScanEngine`: keep candidates whose **current** value is within `tolerance` of an absolute `target` (`PassesExact`); rejected without finite target + non-negative tolerance |
| Wire + CLI passthrough | `OffsetCompareRequest` docs; Host.Web validation; `roll-replay-time-increased.ps1 -CompareMode exact -ExactTarget -ExactTolerance` (no Space pulses — replay stays paused; survivors read from `currentCount`) |
| Tests | `PassesExact` unit tests + endpoint validation tests |

**Exit:** build 0 errors, tests green, driver parses clean, mode rejected
without target/tolerance.

### M1 — Live exact-scan campaign (OD-047) — **CAP: 2 sessions**

1. Operator pauses the replay at a decoded clock value **T1** (e.g. 60.000s
   into the battle); record T1 from the decoded session data.
2. **Pause confirmed by pixel probe** (`scripts/replay-play-state.ps1`, 2026-08-04):
   the driver waits for the bottom-center HUD icon to show `paused` (two bars,
   not the play triangle) before scanning, and per-round probes warn on an
   accidental resume. `-SkipPauseProbe` bypasses (HUD hidden / headless).
3. Run `roll-replay-time-increased.ps1 -CompareMode exact` for each unit
   variant: `-ExactTarget <T1>`, `<T1*1000>`, `<T1*1000000>` with
   `-ExactTolerance 0.05` (Double, 8-byte aligned).
4. Record the per-variant collapse from the ~66M baseline.

**Exit:** at least one variant collapses to ≤ ~1% of baseline with stable
addresses across rounds. If neither session collapses below ~1%, **stop** —
descope per the strategy stop rules.

### M2 — Two-pause fingerprint + staging

1. Repeat the exact scan at **T2** (e.g. 120.000s) for the surviving variant.
2. Intersect the T1 and T2 survivor sets: the true `replayTime` address must
   appear in both. A non-empty intersection is the field identifier.
3. Stage the intersection (≤ 50 addresses) to the default
   `%TEMP%\od-survivors.txt` for the debugger.

**Exit:** non-empty intersection with plausible addresses; else descope.

### M3 — Write-trace conversion (x32dbg) — **CAP: 2 attempts**

1. Pre-arm x32dbg on the managed game (`scripts/pre-arm-debugger.ps1`).
2. Run the automated write-trace (`scripts/x64dbg-write-trace.ps1
   -AutoWriteTrace`) on the staged set during a held green window.
3. First `{rip}`-named evidence file → the writing instruction → member
   displacement.

**Exit:** ≥ 1 write hit with an instruction expressing a member displacement
(e.g. `movss [reg+0x28], xmm0`); else descope.

### M4 — Repeatability and publication

1. Second launch + second distinct replay (BLK-0019): same displacement or
   pointer chain.
2. Publish `Candidate` per workflow Phase 5; update the versioned offset table
   evidence notes.

**Exit:** candidate published; or `Superseded`/`Stale` recorded honestly.

## Descope gate

Trigger on **any** of:

- M1: no collapse below ~1% of baseline in 2 sessions;
- M2: empty fingerprint intersection;
- M3: 0 write-trace hits in 2 attempts on a small clean set.

**Action:** archive the pipeline + evidence, append a closeout entry to the
ledger and blocker log, mark the track research-only, and refocus on the
product. The pipeline and the structural negatives remain durable assets.

## Fallback path (only if the exact scan is blocked, not merely slow)

Run the OD-045 delta pilot (`-CompareMode delta -DeltaTarget 4.0
-DeltaTolerance 0.4`, proven invocation `-SnapshotMaxBytes 402653184
-MaxRounds 40 -HoldAfterRollSeconds 240`) — ranked deterministic by the
OD-045-STATIC simulation. It shares M3–M4.

## Guardrails (do not repeat without a changed hypothesis)

- Absolute-image-only or low32-pointer AOBs on survivor bytes (OD-007/008/009).
- Automated CE Windows-debugger write-BPs (OD-009/010/011, OD-020/021/022).
- `KUSER_SHARED_DATA` survivors as game-field evidence (OD-044 — dropped + WARN).
- Rolling from a load-transition snapshot (OD-025); stale single-capability
  rolls (OD-030); `retainedCount` as survivors (OD-017).
- The unresolved `playerYaw` neighborhood scan (quarantined).
- Treating the 120s lease knobs as hard limits now that the liveness
  heartbeat rolls the authorization (2026-08-04).
