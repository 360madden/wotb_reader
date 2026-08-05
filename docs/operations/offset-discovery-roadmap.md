# Offset-discovery roadmap v4 — replay-guided trajectory correlation

**Date:** 2026-08-04
**Owner:** offset-discovery track
**Parent:** [`offset-discovery-strategy-v4.md`](offset-discovery-strategy-v4.md)

## Goal and definition of done

**Goal:** one runtime-supported offset in preference order `replayTime`,
`playerPositionX/Z`, `playerHP` — with a correctly classified address kind.

**Definition of done:** the candidate is reproducible across **2 launches × 2
replays** with member-displacement or pointer-chain classification, and is
published per `offset-discovery-workflow.md` Phase 5.

**Context:** offsets are **not** product-critical — the replay decoder serves
the HUD. This track is research; the roadmap carries its own budget and stop
rules so it cannot silently consume the product's effort.

**v4 pivot (2026-08-04):** the exact-pause scan (v3 M1) required human
precision (pause at 60.000s ± 0.05s) — a design defect, since the pipeline
cannot read the very value it hunts. The replay is itself a complete
time-series: **stage candidate addresses from a scan, re-read them while the
replay plays, and correlate each address's value series against the known
trajectory**. No pause, no OCR, no human precision. See the strategy doc.

## Milestones

### M0 — Exact-scan capability (2026-08-04) — ✅ complete (fallback tool)

| Deliverable | Detail |
|---|---|
| Engine `exact` compare mode | `MemoryScanEngine`: keep candidates whose **current** value is within `tolerance` of an absolute `target` (`PassesExact`) |
| Wire + CLI passthrough | `roll-replay-time-increased.ps1 -CompareMode exact -ExactTarget -ExactTolerance` |
| Session driver | `scripts/od-047-exact-scan-session.ps1` (gate wait → 3 unit variants → JSON report → optional `-RunT2` fingerprint) |

Retained as a documented fallback for genuinely static values. **No live
session is spent on it first** (v4 guardrail).

### M1 — Live monitor-and-correlate campaign (OD-048) — **CAP: 2 sessions**

The replay plays at 1x. No operator input after launch.

1. Launch the offline replay via the canonical pipeline
   (`scripts/launch-offline-replay-for-od.ps1`); the gate verifies on the
   Start marker.
2. Run `scripts/od-048-monitor-correlate-session.ps1`:
   - **Stage:** fetch the decoded session trajectory; take the viewpoint
     entity's first position sample (plus the top movers); scan the game for
     Float values near each axis (3 scans/entity, `FloatTolerance` 8); union
     the candidates.
   - **Monitor:** re-read the staged set every 2s via
     `POST /api/v1/game/discover/read` while the replay plays.
   - **Correlate:** `POST /api/v1/game/discover/correlate` scores each
     address's value series against every entity axis (sign flips, ±8s
     time-shift sweep) and ranks the survivors.
   - **Report:** `.data\od-048-<timestamp>.json` with staged/monitored/
     correlated counts, results, `strongSurvivors` (score ≥ 0.7) and a
     verdict.
3. Read the report; a **strong survivor** (score ≥ 0.7 = reproduces ≥ 70% of
   the movement samples) is the field evidence.

**Prep (2026-08-04):** scorer + read primitive + trajectory/correlate
endpoints + `od-048` driver built and unit-tested (11 scorer tests; 6 new
endpoint tests; PSSA gate 0 warnings on 22 scripts). Replay clock verified at
**10,000,000 ticks/s** (synthetic fixture exactly 1.2e9 ticks / 120s; real
decode 599,839,248 ticks ≈ 59.98s). Dead Rail session
`019fb86c-c8e7-7004-9df6-a574f5a7835b` (`duration_ticks` 2,713,761,600 ≈
271s) is the ground-truth source.

**Live-run sequence (operator-present, one launch):**

```powershell
# 1. Launch the offline replay (canonical pipeline).
scripts/launch-offline-replay-for-od.ps1

# 2. As soon as the game is verified (or with -SessionId <guid> to pin the
#    decoded session), run the campaign. It needs no further input.
scripts/od-048-monitor-correlate-session.ps1

# 3. Read the verdict:
#    .data\od-048-<timestamp>.json -> strongSurvivors (score >= 0.7)
```

**Exit:** ≥ 1 strong survivor. If neither of the 2 sessions produces one,
**stop** — descope per the strategy stop rules.

### M2 — Family mapping + write-trace — **CAP: 2 attempts**

1. For each strong survivor, read the ±4-byte neighbors and correlate them
   too (the sibling x/y/z components; the "candidate family maps fast" step).
2. Pre-arm x32dbg (`scripts/pre-arm-debugger.ps1`) and run the automated
   write-trace (`scripts/x64dbg-write-trace.ps1 -AutoWriteTrace`) on the
   surviving family during a held green window.
3. First `{rip}`-named evidence file → the writing instruction → member
   displacement.

**Exit:** ≥ 1 write hit with an instruction expressing a member displacement
(e.g. `movss [reg+0x28], xmm0`); else descope.

### M3 — Repeatability and publication

1. Second launch + second distinct replay (BLK-0019): same displacement or
   pointer chain.
2. Publish `Candidate` per workflow Phase 5; update the versioned offset table
   evidence notes.

**Exit:** candidate published; or `Superseded`/`Stale` recorded honestly.

## Descope gate

Trigger on **any** of:

- M1: no strong survivor (score ≥ 0.7) across 2 sessions;
- M2: family correlation fails on the survivors;
- M3: 0 write-trace hits in 2 attempts on a small clean set.

**Action:** archive the pipeline + evidence, append a closeout entry to the
ledger and blocker log, mark the track research-only, and refocus on the
product. The pipeline and the structural negatives remain durable assets.

## Fallback paths (only if correlation is blocked, not merely slow)

- **Exact-pause scan** (v3 M1): `scripts/od-047-exact-scan-session.ps1` — for
  a genuinely static value only; requires an operator pause and the pixel
  pause probe.
- **Delta pilot** (OD-045): `roll-replay-time-increased.ps1 -CompareMode
  delta -DeltaTarget 4.0 -DeltaTolerance 0.4` — ranked deterministic by the
  OD-045-STATIC simulation.

Both share M2–M3.

## Guardrails (do not repeat without a changed hypothesis)

- **Never design a campaign that needs human precision** (v3 M1's exact
  pause; rejected at review from now on).
- Absolute-image-only or low32-pointer AOBs on survivor bytes (OD-007/008/009).
- Automated CE Windows-debugger write-BPs (OD-009/010/011, OD-020/021/022).
- `KUSER_SHARED_DATA` survivors as game-field evidence (OD-044 — dropped + WARN).
- Rolling from a load-transition snapshot (OD-025); stale single-capability
  rolls (OD-030); `retainedCount` as survivors (OD-017).
- The unresolved `playerYaw` neighborhood scan (quarantined).
- Treating the 120s lease knobs as hard limits now that the liveness
  heartbeat rolls the authorization (2026-08-04).
