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
   - **Stage:** fetch the decoded session trajectory; wait -StageDelaySeconds
     (15s) for the battle to load after the Start marker; for the viewpoint
     entity (plus the top movers) scan the game for Float values near the
     ground-truth sample NEAREST the expected current replay tick (3
     scans/entity). The tolerance is auto-scaled from max entity speed ×
     (delay + 25s) so the band covers the live position despite unknown load
     latency; scans retry (up to 3 attempts) until candidates are found.
   - **Monitor:** re-read the staged set every 2s via
     `POST /api/v1/game/discover/read` while the replay plays.
   - **Correlate:** `POST /api/v1/game/discover/correlate` scores each
     address's value series against every entity axis (sign flips, 0.5s-step
     ±30s time-shift sweep, reports the winning `shiftSeconds` plus the
     ambiguity band `shiftMinSeconds`/`shiftMaxSeconds` for audit) and ranks
     the survivors; the driver demotes edge-riding survivors (band touching
     the sweep boundary) to suspect (`evidence-edge-aligned` verdict).
   - **Report:** `.data\od-048-<timestamp>.json` with staged/monitored/
     correlated counts, results, `strongSurvivors` (score ≥ 0.7) and a
     verdict.
3. Read the report; a **strong survivor** (score ≥ 0.7 = reproduces ≥ 70% of
   the movement samples) is the field evidence.

**Prep (2026-08-04):** scorer + read primitive + trajectory/correlate
endpoints + `od-048` driver built and unit-tested (15 scorer tests incl.
fast-mover sub-second-shift regression; provider downsample regression;
endpoint hardening tests; PSSA gate 0 warnings on 22 scripts). Bug-fix pass
same day: downsample overflow (empty ground truth for battles > 256 samples),
whole-second shift sweep rejecting fast movers, unvalidated wall anchor,
staging timing — all fixed with regression tests; see the strategy-v4 doc and
handoff amendment. Replay clock verified at
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

### M1.5 — Viewpoint-first discovery pivot (2026-08-06) — ✅ implemented offline, ✅ TWO strong live verdicts (FRESH20/21), ⏳ arm + trace the survivor (FRESH22)

**Pivot:** find ONE highly discriminating live coordinate of the **viewpoint
player** by correlating its observed value history against the decoded
viewpoint trajectory — then trace its writer immediately. See
[`specs/2026-08-06-viewpoint-first-discovery.md`](../superpowers/specs/2026-08-06-viewpoint-first-discovery.md).

**Why:** the server scores each address against the BEST-matching entity, so
decoy addresses tracking teammates surface as alternate-entity matches; the
strongest artifact produced so far was a lone viewpoint survivor that family
assembly never grouped. The pivot stages only the viewpoint, requires only
one axis, and refuses to delay tracing for XYZ neighbors.

**Implemented (`-StageViewpointOnly` in od-048 + autoloop pass-through):**

- Stage ONLY the `IsViewpoint=true` entity (hard exit 2 if none).
- Skip mid-battle family refinement (no XYZ neighbor assembly, no wasted
  correlate calls).
- Restrict correlate results to the viewpoint `entityId` before the shift
  audit (`Select-ViewpointResults`) — alternate-entity decoys excluded even
  at higher score.
- Restrict families to members whose addresses belong to the viewpoint
  results (`Test-FamilyAllViewpoint`).
- Solo path arms the first strong viewpoint survivor; report gains
  `viewpointOnly`/`viewpointEntityId`.

**Offline-validated:** `tmpwotb-e2e/test-viewpoint-filter.ps1` (AST-extracted
functions + verbatim staging block: entity filter, family filter, staging
parity, no-viewpoint exit 2), parse PS5.1+pwsh7, PSSA baseline, no-game
DryRun, autoloop splat probe.

**Live campaign status (FRESH15 → FRESH21, 2026-08-06):** eight live rounds
have each surfaced and fixed one real bug — six infrastructure defects, then
two strong scientific verdicts (FRESH20/21); the pipeline is now mechanically
reliable and the verdict stage is complete (all fixes committed):

| Round | Outcome | Bug found → fix (commit) |
|---|---|---|
| FRESH15 | attach-smoke left the game permanently frozen ~1/3 of runs (x64dbg/WOW64 resume limitation) | campaign retry loop (auto-relaunch, max 3) + detach-then-poll recovery + battle-started smoke gate |
| FRESH15h | smoke green, 0 strong survivors | pause-compensation so the smoke pause doesn't warp the sample series |
| FRESH15i/j | staging ran during attendance (elapsed 8.6s) → staged decoys | `-StageMinBattleSeconds` gate; then the attendance-latency correction — stage/correlate at match-begin (marker + latency), not the marker (`c918694`) |
| FRESH17 | click registration flaky (blind double-click) | single verified click + SendInput + message-click channel + animation settle + 640×360 window resize (`885a537`, `14633a0`) |
| FRESH18 | 3 mid-run 401s (host rotates the capability token on every ≥15s publish); z axis rode the −30s sweep edge (true shift beyond it) | 401-retry in `Invoke-Api` (re-read rendezvous + retry ≤5×2s, non-401 fails closed); `MaxTimeShiftSeconds` 30→90 (`888fb58`) |
| FRESH19 | **zero-viewpoint `$null.Count` crash** killed the campaign after the correlate; relaunch re-used attempt 1's stale marker (staged 806 vs 3000) | caller-side `@(Select-ViewpointResults …)` (`540c6bc`); fresh-marker polling on relaunch |
| FRESH20 | **FIRST STRONG VERDICT** — 5 non-edge viewpoint survivors; 3 z candidates at span ~270 (= decoded z span), shift −27.5…−31.5, 60–62/70 matches; trace fired but battle ended 11s into the 25s window (`STOP_gate=Denied`) | adaptive trace window (budgets `TraceSeconds` from the battle tail) + rounds 70→50 (`256984e`) |
| FRESH21 | **SECOND STRONG VERDICT, cleanest run** — 32 strong survivors (29 z @ score 0.92, span 275.15 = decoded z span, shift 0, non-edge band [−19.5,+12]); **but families=0 → trace SKIPPED** (`no_families_from_survivors`) | **stale 20s band floor** (1/3 of the old ±30s sweep, never re-derived when the sweep widened to ±90) refused every survivor in the solo gate → band floor re-derived to 60s + new span floor (`7c02f7d`) |

**FRESH21 no-trace root cause (2026-08-06, `7c02f7d`):** the band floor
(`AutoTraceMaxMemberBandSeconds` / `MaxMemberBandSeconds` = 20s) was 1/3 of the
**old ±30s sweep** and was never re-derived when commit `888fb58` widened the
sweep to ±90s — the write-trace's own doc says the floor must be paired with
the sweep that produced the bands. FRESH21's z bands (31.5s = 17.5% of the
±90 sweep — discriminating, not degenerate) failed the stale 20s floor, so the
solo gate skipped every strong survivor (`soloFamilyEmitted=False`) and the
trace never fired. Fix: band floor 20s → **60s** (= 1/3 of ±90, the original
ratio at the real sweep) plus a **new span floor** (`MinMemberSpan` /
`AutoTraceMinMemberSpan`, default 10 game units): a value that never moves
matches a low-information axis at any shift (the FRESH10 static y@span 4.0
class the widened band floor alone can no longer catch). Solo members now
serialize `span`; od-048 passes the floor through `wtArgs` so the two gates
never disagree. Validated offline: write-trace probe admits the FRESH21-class
family (armed) and refuses the FRESH10 degenerate; a solo simulation on the
real FRESH21 result emits `0x23BD2C50` (z, band 31.5s, span 275.2, score 0.92).

**FRESH19 crash root cause (2026-08-06):** `Select-ViewpointResults` returns
`@(…)` but PowerShell **unwraps function pipeline output on return** — zero
matches become `$null`, and `$null.Count` throws PropertyNotFoundException
under StrictMode. The sharper 90s sweep can produce zero viewpoint matches
(every address scored as an alternate-entity decoy). Fixed with a caller-side
`@()`; a unit probe reproduces the exact error without the fix and passes with
it; an 8-round campaign now completes (`NO_CRASH`, report written). This bug
would have crashed EVERY sharp-sweep run — FRESH18's 173-result array masked
it.

**Remaining live gate (FRESH22):** the verdict question is RESOLVED — FRESH20
and FRESH21 both returned `verdict=evidence-strong` under the fully-corrected
pipeline (non-edge, span-275 z survivors at score ≥ 0.857; the offline
dry-run's 1.000 @ shift-0 prediction holds in live shape). The remaining gate
is **arming + tracing the survivor**: FRESH20 fired the trace but the battle
ended mid-window (fixed via the adaptive window); FRESH21 skipped it because
the stale band floor refused every survivor (fixed via the 60s re-derivation +
span floor). FRESH22 — the first round where a strong survivor passes every
gate — should produce the first `odwt-*.bin` hit report (writer RIP/RVA, base
register, displacement, nearby-object dump). Open science question if the
armed trace still yields nothing: tick-rate mismatch (a constant shift can't
fix a rate error → a rate dimension in the scorer).

**Pending after a strong survivor:** writer evidence → object base →
sibling-coordinate local read → resolver classification (pointer path /
object relationship / code signature).

### M2 — Family mapping + write-trace — **CAP: 2 attempts**

1. **Family mapping (READ-SIDE BUILT 2026-08-05).** At monitor round 10 the
   driver runs a provisional correlate, takes the top non-edge-aligned
   survivors (score ≥ 0.7, cap 25), and re-stages their ±16-byte neighbors (8
   addresses each: every 4-byte step in ±16) so the remaining rounds record
   the sibling x/y/z components. The final correlate keeps the family-neighbor
   series FIRST under the 2000 server cap (they carry fewer samples than the
   originals and would otherwise be truncated) and its `families` section
   groups the scored addresses into coordinate families (same entity, one
   base-relative 16-byte window; member offsets; axes covered). A `complete`
   family — exactly x/y/z at distinct offsets, none edge-aligned — upgrades
   the verdict to `family-complete`: one session maps all three components
   (the survivor may be the middle component; the family base is the lowest
   address). Verified by 12 family-builder unit tests + endpoint
   serialization test + a 16-check simulation of the real driver functions.
2. **Write-trace driver (BUILT 2026-08-05, offline validated).**
   `scripts/x64dbg-write-trace.ps1 -FamilyFile <od-048 report> -AutoWriteTrace`
   pre-arms x32dbg when missing (pre-arm-debugger.ps1), gate-prechecks
   `OfflineReplayVerified`, requires the replay HUD icon `playing` (a paused
   replay writes no position fields — fail-closed exit 7, advisory mid-window
   probe warns on a mid-window pause), re-reads the armed family addresses
   through the guarded Host read API to confirm they are live in the CURRENT
   process (exit 8 on a stale family from a fresh launch), arms 4-byte
   hardware write breakpoints (`bph <addr>,w,4`) on the member addresses
   (Float32 at 4-byte offsets; legacy survivor-file input stays `w,8`
   Double), holds the trace window, and writes a per-member hit report to
   `<ResultPath>.family.json` with a `family-hit`/`family-no-hit` verdict.
   Validated: PS 5.1 parse, ASCII, PSSA gate 0 warnings, 13-check
   simulation of the real extracted helpers (complete-family selection,
   DR0-DR3 cap, bare-family input, dedup), DryRun smoke in family + survivor
   modes.
3. First `{rip}`-named evidence file → the writing instruction → member
   displacement.

**Exit:** ≥ 1 write hit with an instruction expressing a member displacement
(e.g. `movss [reg+0x28], xmm0`); else descope.

> **Same-launch choreography (2026-08-05):** the DAVA viewer has **no rewind**
> and no replay hot-swap (seek-forward-only; selecting a replay reinitializes
> the scenario), and `roll-replay-time-increased.ps1` is a memory-scan roll,
> not a replay rewind. M2 therefore runs in the **tail of the SAME playback**:
> with `-MaxReadRounds 90` on the 271s Dead Rail session the final correlate
> fires ~200s in with ~60s of battle left, and the write-trace is started
> IMMEDIATELY on the verdict with `-TraceSeconds` budgeted under battle end.
> Full operator sequence, timing table, and edge-case guards:
> [`offset-discovery-m1-m2-choreography.md`](offset-discovery-m1-m2-choreography.md).

> **Gating decision before the next live round — the solo-survivor path
> (RESOLVED 2026-08-06).** FRESH12 proved the pipeline's strongest artifact
> (`0x1FC57238`, y@1.000, tight interior ambiguity band [−10,−7.5] = 2.5s,
> non-edge) was **structurally un-armable**: its ±16-byte neighbors scored
> below the family-seed floor, so it was never grouped into a family, and
> both gates required ≥2 members. The FRESH14 session plan
> ([`handoffs/2026-08-06-fresh14-session-plan.md`](handoffs/2026-08-06-fresh14-session-plan.md))
> made building the solo path the Chunk 3 decision gate **before** spending
> a live session. That path is now BUILT and offline-validated: the
> write-trace has a `-SoloAddress` mode (single-member family run through the
> same score + band floors) and `Test-UsableFamily` accepts ≥1 member; od-048
> emits a single-member `solo` family from the best lone tight-band non-edge
> survivor and serializes `solo`/`soloFamilyEmitted` in the report; the
> auto-trace gate accepts ≥1 member. Validated: tight-band solo fixture arms
> (exit 0), degenerate 40s-band y@1.0 refused (exit 2), real FRESH10 report
> still refused, emitted shape round-trips, AST-extraction harness passes,
> PS 5.1/pwsh 7 parse + PSSA gate green. **Remaining gate: the live round
> itself** (`od-049-autoloop.ps1` with `-AttachSmokeOnFirstRound`; offline
> chunks 0–2+4 pre-flight complete).

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

### M1 cap re-baseline (decision, 2026-08-06, before FRESH20)

**Decision: the M1.5 pivot re-baselines the M1 session cap.** The FRESH15→19
campaign (six sessions) produced **zero valid scientific tests of the current
pipeline** — every round failed on a distinct, now-fixed defect (staging
before match-begin; marker anchor without attendance correction; 3 mid-run
401 holes; 30s sweep too narrow for the true shift; the zero-viewpoint
`$null.Count` crash; the stale-marker relaunch). A stop rule only means
something when it tests the pipeline in production; archiving now would
record an infrastructure failure as a scientific negative, and the offline
dry-run already scores the corrected anchor at 1.000 @ shift 0 through the
real scorer.

Budget and triggers (no further extension):

- **FRESH20 + at most FRESH21 = the re-baselined 2-session budget.**
- A session **counts only when valid**: staging gate fired post-match-begin,
  smoke green, no crash, correlate completed. An infra-failed run is retried
  within the budget, not charged.
- **Hard archive trigger:** 2 valid sessions under the fully-corrected
  pipeline with no strong survivor (score ≥ 0.7, non-edge) → execute the
  archive action above regardless of sunk cost.
- **Pre-FRESH20 gate:** the tick-rate probe (offline) must classify what a
  weak verdict means — wide band at the true shift = scorer cannot express a
  ~1% tick-rate error (scorer limitation → archive-worthy) vs a capture
  error (fixable → retry within budget).

**Budget outcome (2026-08-06, after FRESH21):** both budget sessions returned
`verdict=evidence-strong` — the archive trigger (2 valid sessions with no
strong survivor) did **not** fire. The verdict-stage budget is complete; the
remaining live need is **arming + tracing the survivor** (the M1.5→M2
handoff, FRESH22), which is M2's own live requirement rather than an
extension of the verdict budget. The tick-rate probe remains the classifier
if the armed trace still yields no writer evidence.

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
