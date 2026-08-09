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

### M1.5 — Viewpoint-first discovery pivot (2026-08-06) — ✅ implemented offline, ✅ strong live verdicts (FRESH20–25), ❌ x64dbg write-trace capture route CLOSED (FRESH26–33, root-caused — see M2), ✅ **C# interceptor lands the FIRST WRITE-SITE HIT REPORT (FRESH36, 2026-08-07)**

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

**Live campaign status (FRESH15 → FRESH25, 2026-08-06):** eleven live rounds,
each surfacing and fixing one real bug — six infrastructure defects, then six
consecutive strong scientific verdicts (FRESH20–25), the first completed
armed trace (FRESH22, no-hit → selection fix), and the frozen/attach
mechanism questions settled: FRESH24 ruled out a frozen window
(liveness=running) and FRESH25 found the real killer — the trace's SECOND
attach froze the game and the monitor denied the gate → **attach-once fix**
(smoke keeps one debugger, trace reuses it). The pipeline is mechanically
reliable and the arming machinery is proven end-to-end (all fixes committed):

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
| FRESH22 | **FIRST ARMED TRACE COMPLETED** — `family_solo_emitted axis=z score=0.94` (the FRESH22 gate fix held); trace ran end-to-end (BP armed, script injected, 25s live window) → **`family-no-hit` — zero writes** while the decoded replay proves the tank was moving through the whole window | **score-desc tiebreak armed a partial-window copy** (span 75.5) over the consensus class (~20 z @ span exactly 275.4, shift 0) → **span-first selection** (span desc, score desc, band asc) + **arm top-4 consensus addresses** (`-AutoTraceMaxSoloMembers` = 4 DR0-DR3) (`6f36067`) |
| FRESH23 | **consensus class armed exactly as designed** — `family_solo_emitted axis=z members=4 span=275.4`; trace ran end-to-end → **`family-no-hit` AGAIN** (3/3 traces, zero hits, tank moving) | **frozen-window hypothesis** (the trace never verifies the game resumed after attach; the smoke does) → **CPU-liveness discriminator** in the write-trace: `window_liveness=running|frozen` from the game's TotalProcessorTime across the window (`x64dbg-write-trace.ps1`) |
| FRESH24 | **frozen-window hypothesis DEAD** — `window_cpu_delta_ms=25672 liveness=running` (game consumed 25.7s of CPU in the 25s window; fully executing) yet **`family-no-hit`** again → the no-hits are REAL for the correlated class | new leading hypothesis: **the battle world isn't advancing during the window** (paused viewer / roster screen / packet-gap renders but writes nothing; the pixel play-state probe read `unknown`) → **value-liveness discriminator**: re-read the armed addresses' float values at window start/end (`window_values_changed` + `window_max_value_delta`) (`1c6690d`) |
| FRESH25 | **value-liveness never got its window** — M1 strong again (`family_solo_emitted axis=z members=4 span=275.2`), but the auto-trace **re-pre-armed a SECOND x32dbg (pid 48272) and attached at ~21:32:52** → WOW64 attach-freeze (operator saw "not responding") → host monitor denied terminally (`evidence.monitor_unhealthy` at 21:32:53) → first gate poll read `Denied` → exit 5 before the window opened | **attach-once fix**: smoke keeps ONE debugger attached (scriptrun-resume, `-KeepAttached`) through the campaign and the trace **reuses it** (`-ReuseAttached`, skips its own attach; fallback to fresh attach if the smoke's debugger died). Eliminates the second attach, the freeze, the denial, and the wrapper latency (`x64dbg-write-trace.ps1` + od-048 wiring) |

**FRESH22 no-hit root cause (2026-08-06, `6f36067`):** the trace machinery
completed perfectly (gate → liveness → attach → `scriptload+scriptrun` → BP
on `0x228B9050` → clean 25s window) but got zero writes. The decoded replay
disposes of timing artifacts: the viewpoint tank is **alive and moving the
entire battle** (last movement t=266s of 279s; z 23.3→−10.1→−28.3 at t≈190–240s
= the trace window) — a per-frame position field MUST be written while the
tank drives. The armed address was a **partial-window copy**: the solo
tiebreak was score-desc, so span-75.5 @ 0.94 beat the **consensus class — ~20
z addresses at span EXACTLY 275.4, score 0.92, shift 0** (the synchronized-copy
signature; same as FRESH21's 29). A partial copy correlates well inside its
active window but is not written every frame. Fix (`6f36067`): solo
candidates sort by **(span desc, score desc, band asc)** — a full-trajectory
field carries the axis's full span, while static values, partial copies, and
low-information y's lose — and the top-**4** consensus addresses are armed at
once (DR0-DR3), since the per-frame writer is one of MANY synchronized copies.
Validated: selection simulation on the real FRESH22 result arms the span-275.4
class (the armed partial drops to rank 41); a 4-member DryRun shows
`family_members_armed=4` with 4× `bpm addr,1,w` BPs.

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

**Live outcome FRESH26–33 (2026-08-06) — x64dbg capture route CLOSED.**
FRESH26–33 (attach-once + every fix) ran clean end-to-end every time —
anchor, staging, rounds, smoke-kept-debugger, evidence-strong x-consensus,
`reused_attached_debugger`, `values_changed=true` (14.8–22.5 units, world
advancing), `m1_exit=0` — and produced **zero capture on every channel**
(no engine log, no BP log, no savedata hit file). The FRESH32/33 diagnostic
probes root-caused it at the mechanism level (full chain:
[`handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md`](handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md)):
(1) every UIA log read was reading UI chrome (`Name`), not log text
(`DataItem` `Value`) — so `bpmArmed`/`ODWT_ARMED` verifications read noise;
(2) in-script `bpm 0x…,1,w` fails with `Error executing command!` — the
memory BP never arms, so nothing can ever capture; (3) `bpm` AND `bph` never
fire even via the command bar on a synthetic target writing to a known
address in a tight loop (all 4 syntax variants); (4) the counter target runs
6 threads — worker-thread writes escape main-thread DR hardware BPs, and the
game's position writes almost certainly do the same. Earlier "proofs"
(`static-hit.bin`, "hits landed 3/3") are ambiguous or from the resume
campaign, not BP capture. **Decision (CAP discipline): stop x64dbg live
runs; the M2 successor is a C#-native guard-page write interceptor (below).
The M1 address-level evidence stands as the interim result.**

**Live outcome FRESH34–36 (2026-08-06/07) — C# interceptor route LANDS THE FIRST WRITE-SITE HIT (2026-08-07, `da48c92`).**
FRESH34 (interceptor wired, 2026-08-06): 40 real guard traps at a stable
write-site rip (`0x00379554` ×39) but **0 hits — `changed=False`** — the
trace fired ~40s AFTER the real battle end (result screen, world frozen): the
battle-end model assumed 1× playback + 50s attendance while the game plays
~2× (FRESH34: 271.4s decoded = 134s wall, scene-end 02:31:34 → deaths
02:33:48). Root cause 2: `Test-BlitzBattleEnded`'s log lines parsed with
*local* dates, so during the run (local Aug 6 22:xx, log UTC Aug 7) all 764
lines compared `<` the Aug-7 anchor and were skipped — the watcher never
fired (the offline-vs-live date-rollover trap). Fixes (`38f5e91`, then the
bug-hunt round `b5849a5` and the adversarial fresh-eyes pass `da48c92`):
`Get-BlitzRealWindow` (two-pass UTC-dated log parse: real match begin =
last `LoadGameScene ends`, end markers gated to it, log-silence as end
evidence, playback-speed derivation, midnight-crossing-safe horizon),
fire-by deadline refreshed every round, `-PlaybackSpeedEstimate` default 2.0
(measured 2.01–2.03×), monotonic-forward + activity + >20s-silence guards on
silence-derived ends, staging-tick anchor kept at marker+attendance (the
FRESH15j contract — the log scene-end is the *recording* start, not the
decoded tick-0 instant), `blitzLog` evidence block in the report
(realMatchBegin/End, measured speed, fire-by), fail-closed capture parse.

| Round | Outcome | Bug found → fix (commit) |
|---|---|---|
| FRESH34 | C# interceptor mechanism PROVEN live (40 guard traps at a stable rip, no freeze, clean detach) but **0 hits — `changed=False`**: trace fired 40s after the REAL battle end (result screen) because the battle-end model assumed 1×+50s attendance while the game plays ~2×; the log watcher also never fired (date-rollover: local Aug-6 dates vs the Aug-7 UTC anchor) | log-derived real battle window + fire-by deadline + UTC-date parse (`38f5e91`) |
| FRESH35 | offline fix round (no live run): horizon/rejection, degenerate-speed clamp, live-window guards, playback estimate 2.0, loop end-correction, harness AST-extraction false-confidence fix | bug-hunt round (`b5849a5`) + adversarial fresh-eyes: staging-anchor regression, `blitzLog` report evidence, fail-closed capture parse (`da48c92`) |
| FRESH36 | **FIRST REAL WRITE-SITE HIT REPORT FROM THE GAME** — `family_solo_emitted axis=x members=4 score=1 span=77.3 band=1.5s`; interceptor armed 4 addresses (0x3D525BE8/0x3D525CC0/0x3D525C98/0x3D525C20, one 4KB page) against pid 21924 for 25s → **`family-hit` hits=51 hit_members=4 values_changed=true liveness=running** — 4 distinct write-site RIPs write ALL 4 armed addresses (0x01005F19, 0x01331878, 0x01B62D9D, 0x0239E856); game log proves the trace window (05:13:05–30Z) sat INSIDE the live battle (scene-end 05:11:32Z, deaths 05:13:20–22Z); `stoppedReason=fire-by-deadline` after 14 rounds; 554 addresses scored / 7202 samples; measured playback this session ~2.47× (271.4/110s) vs the 2.0 estimate — the fire-by still landed inside the battle because the estimate errs early-safe | none (all fixes held); **next: resolve which module owns the 4 RIPs (module-relative RVAs) → the writing instruction → the position/transform object base** |
| FRESH36 offline tail | **Durable write-site evidence path (no live run):** interceptor now serializes attach-time `modules[]` + per-hit `instructionHex`; wrapper copies TEMP capture → `ResultPath.capture.json` and promotes `modules` / `writeSites` / member `rvas` into `.family.json` (FRESH36 lost rva/registers because capture was ephemeral). Pure `Core/Discovery/WriteSiteAnalysis` resolves RIP→module+RVA, ranks object-base candidates from registers, builds sibling-read plans, and classifies resolver kind (evidence-first). FRESH36 historical module map remains unrecoverable offline. | **FRESH37 live:** republish interceptor; same autoloop flags + `-PlaybackSpeedEstimate 2.4`; require durable `.capture.json` with modules covering the write-site RIPs (or honest `jit`) |

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

> **M2 pivot — x64dbg write-BP route CLOSED (2026-08-06, `882227b`).** The
> x64dbg write-breakpoint capture is conclusively non-functional in this
> environment (see M1.5 live outcome above + the FRESH32/33 handoff). The
> write-trace script remains as a documented tool but no live session may be
> spent on it. **Successor: C#-native guard-page write interceptor** —
> `VirtualProtect(PAGE_GUARD)` on the armed page + debug-event handling
> (`DebugActiveProcess`/`DebugActiveProcessStop` + `WaitForDebugEvent`,
> catching `STATUS_GUARD_PAGE_VIOLATION`) + `GetThreadContext` on the
> faulting thread → RIP = write site → single-step past + re-arm + record.
> Lives inside the sanctioned UltimateScanner/GameIntegration Win32
> allowlist; buildable and testable offline against a synthetic target
> before any live session (the original M2 mandate).

**Exit:** ≥ 1 write hit with an instruction expressing a member displacement
(e.g. `movss [reg+0x28], xmm0`) captured by the C# guard-page interceptor;
else descope per the archive action.

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

### M2.5 — Instruction-first object snapshot (closed render-transform branch, 2026-08-08)

FRESH45 closed the candidate-minus-displacement hypothesis: four transient
correlation candidates produced 12 readable floats but zero complete XYZ
matches. Do not repeat that scan-first path or the delayed write trace.

The active implementation starts at the FRESH43 game-code fill instruction:

- exact target: `11.19.0.10` executable hash, `wotblitz.exe+0x7C39AB`, bytes
  `8B83A0000000` (`MOV EAX,[EBX+0xA0]`);
- capture: hardware execute breakpoint, EBX from the held event, one 12-byte
  read from EBX+`0x90` for the composed world-matrix translation;
  `+0x1C/+0x20/+0x24` is live-proven scale and `+0x10/+0x14/+0x18` is the
  local translation copied by `FUN_00d1a0f0`;
- authorization: exact coordinator-owned managed offline child and current
  authorization generation; separate helper with no raw-PID mode; actual
  parent bound to build-pinned Host.Web EXE+DLL hashes;
- bounds: at most 5 seconds, 64 accepted samples, 256 threads, 64 KiB result;
- output: opaque per-capture object keys plus values/timestamps, never heap
  addresses on the Host/GameHarness surface;
- semantics: register/displacement provenance may be proven; viewpoint
  identity, decoded-clock identity, hardware atomicity, and stable root remain
  false until separate evidence establishes them.

Synthetic validation and the live breakpoint/cleanup mechanism are complete.
OD-RECOVERY-066 consumed the one allowed world-translation capture: seven
finite samples from one opaque object, exact fingerprint and cleanup proven.
The best coherent clock-aligned comparison across every participant, 48
axis/sign mappings, 0.5x-8x playback, and scene-marker uncertainty missed by
mean 10.850 / max 12.556 units, with 0/7 within 1 unit. A free constant-offset
fit required a 250.832-unit origin shift and 6.26x playback. The matrix row is
statically real, but it is not identified as the decoded player trajectory.
Do not repeat or widen this render-transform branch.

### M2.6 — Type-10 position-application path (static anchor found, 2026-08-08)

The decoder already proves the 49-byte type-10 position packet end-to-end:
entity, space, vehicle, XYZ, velocity, and flags reproduce the immutable
decoded ground truth. The shortest remaining route to a reliable player
location is to identify where the game consumes or applies that known XYZ,
rather than infer semantics from a render transform.

`OD-RECOVERY-069` completed the offline/static phase:

1. `ReplayPlayer` dispatch index 10 resolves to handler RVA `0x00FE31C0`,
   whose nine reads reproduce the exact `4,4,4,12,12,4,4,4,1` byte layout.
2. The handler reaches `BWEntities::handleEntityMoveWithError`; entity
   resolution compares the packet ID to `[entity+0x1C]`.
3. Entity application RVA `0x022FA780` reaches RVA `0x022FA78D`, bytes
   `F30F7E00`, with the resolved entity in `ESI` and the packet-derived XYZ
   pointer in `EAX`.
4. `BW::AvatarFilterHelper` independently stores the same vector in an
   8-entry, `0x38`-stride movement ring at record `+0x18`. That buffer is
   transient and is not a stable offset.

`OD-RECOVERY-070` completed the implementation/synthetic phase. The fixed
contract reads the int32 replay entity ID at `[ESI+0x1C]` and contiguous XYZ
at `EAX+0/+4/+8` in one held debug event. The exact
hash/RVA/bytes/registers remain server-controlled. The synthetic x86 target
captured entity ID `4242` and four changing finite XYZ samples; fingerprint,
hit bound, non-Host parent rejection, cleanup, detach, and address-free Host
projection passed. The two reads share one suspended debug event but are not
hardware-atomic; same-decoded-clock and local-player identity remain unproven.

`OD-RECOVERY-071` completed the first live equality proof: 49 valid hits, seven
decoded vehicle entity/XYZ triples matched exactly at Float32 precision, and
one exact match was the replay viewpoint entity. The unmatched eighth object
was a zero vector with no decoded trajectory. This proves event-based
player-position identity for one replay/static window. It does not prove motion
freshness, same decoded clock, cross-replay repeatability, or a stable root.

`OD-RECOVERY-072` is the next bounded live gate: use the unchanged target for
five seconds during verified movement on the other content-distinct replay.
Require a changing viewpoint series and same-entity decoded matches. Stop after
the result; no target/displacement changes or scans belong in the session.

`OD-RECOVERY-072` passed. The capture reached the 64-hit bound with every ID and
XYZ read valid. Twelve decoded entity IDs matched; the replay viewpoint had six
distinct captured positions, including two exact matches retained by the
downsampled decoded trajectory. This closes motion freshness and cross-replay
repeatability for event-based player-position reading.

Next phase: no more unchanged live event captures. Offline/static, derive a
stable viewpoint-entity resolver from the proven type-10 resolver/container and
the movement-filter path. The strongest polling-family clue is the entity's
`+0x38` filter/helper and its 8-entry `0x38`-stride ring (current index
`+0x1C8`, position at record `+0x18`). Freeze a bounded stable-read plan before
requesting another live session.

`OD-RECOVERY-073/074` corrected and narrowed the frozen polling plan.
Hash-bound Ghidra evidence passes 67/67 checks for the GameCore module root,
`GameCore -> AppController -> SessionController -> AccountController ->
PlaybackController -> replay connection -> BWEntities` ownership chain,
cache/three-tree entity lookup, the exact filter family, and the known helper
ring. The Core resolver is bounded to
1,024 tree nodes and three attempts, double-collects the full record, and
revalidates the root/entity/filter/helper chain. The guarded adapter keeps one
read-only identity-bound process lease and the live offline authorization token
for every native read.

Live narrowing reached the replay-owned primary entity map for 24/24 reads on
both corrected-root runs. It first stopped at the filter vtable and, after the
exact filter-family change, at an unverified vehicle-helper vtable.
The request surface accepts only a decoded replay entity ID; PID, module base,
root, pointers, vtables, and displacements are server-owned exact-build policy.
Offline reference tracing now names `WGVehicleFilterHelper::vftable` at RVA
`0x0325658C`, its constructor store at RVA `0x010139F1`, and the factory's
assignment to `filter+0x08`. Next: prove whether its position-store slot and
ring layout match the known helper family. Do not run again by merely
broadening the allowlist. Do not
update the offset table before a successful unchanged cross-replay repeat and
the remaining publication review.

No live budget exists for static exploration. Offset publication still
requires a stable module-relative resolver/root and the M3 evidence gates.

First-pass result: `Partial / no direct consumer anchor`. Three hash-bound
Ghidra scripts scanned 526,935 executable functions and initialized data. The
position/framed-displacement heuristic returned 3,457/190 noisy candidates;
manual decompilation refuted the top match as matrix/grid code. No function
directly checked a common record base for both length `49` and type `10`.
Eight apparent `{10,49,code-pointer}` table rows were MSVC exception metadata,
identified by the surrounding `0x19930522` FuncInfo structures. The next step
is therefore data-flow-first: locate the generic replay event reader/framer
through replay/file entry points and trace its payload dispatch into an entity
or physics setter. Do not repeat displacement or literal scans unchanged.

Community-family result (`OD-RECOVERY-068`): the historical
`VehicleGameLogic +0x04 -> entity +0x68/+0x6C/+0x70` layout does not reproduce
as player position in this exact build. The root `0x03E91978` remains refuted.
The current `VehicleGameLogic` vtable is named at RVA `0x0327DA50`, and slot
`+0x04` resolves to getter RVA `0x0031B560`, which returns `[this+0x04]`. That
getter is the useful clue. Across 79 virtual methods, 17 getter users access 23
returned-entity offsets, but none uses the claimed position triple. A full
generic scan found one exact chained triple, and decompilation refuted it as a
larger matrix/pose copy. OD-RECOVERY-069 used the naming relationship as a
bridge and proved returned-entity `+0x1C` is the packet entity ID; it did not
revive the stale position offsets.

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
handoff, FRESH23), which is M2's own live requirement rather than an
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
