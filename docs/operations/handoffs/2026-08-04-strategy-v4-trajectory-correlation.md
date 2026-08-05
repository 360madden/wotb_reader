# Handoff — strategy v4: replay-guided trajectory correlation (2026-08-04)

**Track:** offset-discovery
**Prior state:** strategy v3 (exact-value pause scan) — built but blocked by a
design defect: the operator had to pause at a decoded clock value within
~50ms, which is machine precision. The pipeline cannot read the very value it
hunts, so no automation could replace the human.
**This session:** pivoted to the original strategy — the replay itself is the
marker. Built the full monitor-and-correlate layer and pushed it.

## What shipped (commit `HEAD` after this handoff)

- **`src/WotBTreader.Core/Discovery/TrajectoryCorrelation.cs`** — pure scorer:
  per-axis (x/y/z) with sign flips, piecewise-linear tick lookup, ±8s
  time-shift sweep that finds ONE consistent shift per (entity, axis, sign)
  (review-hardened: per-sample independent shifts would be weak evidence).
  Replay clock rate pinned at **10,000,000 ticks/s** (verified: synthetic
  120s = exactly 1.2e9 ticks; real decode 599,839,248 ticks ≈ 59.98s).
- **`src/WotBTreader.Storage.Sqlite/SqliteTrajectoryGroundTruthProvider.cs`** —
  per-entity downsampled trajectories (≤ 256 samples/entity), `duration_ticks`
  and the viewpoint (local player) participant.
- **Read primitive:** `IGameMemoryScanner.ReadAddressesAsync` +
  `POST /api/v1/game/discover/read` (≤ 2000 addresses/call, guarded reader;
  one process lease per batch, not one handle per address).
- **Endpoints:** `GET /api/v1/game/discover/trajectory/{sessionId}`,
  `POST /api/v1/game/discover/correlate`.
- **`scripts/od-048-monitor-correlate-session.ps1`** — the M1 driver:
  gate wait → stage (viewpoint + top movers, 3 axis scans each) → monitor
  loop → correlate → JSON report with `strongSurvivors` (score ≥ 0.7).
- Docs: `offset-discovery-strategy-v4.md` (new), roadmap rewritten to v4,
  ledger OD-048 entry, this handoff.

## Tests

- 11 scorer unit tests (`TrajectoryCorrelationScorerTests`) — reproduction,
  sign flip, axis selection, decoy exclusion, stationary-axis skip, shift
  absorption, shift-bound, multi-entity, interpolation, non-finite handling.
- 6 new Host.Web endpoint tests (read validation ×4, trajectory mapping/404,
  correlate scoring/sign-flip/validation).
- PSSA gate: 0 warnings on 22 tracked scripts; both hosts parse the driver;
  preflight fails closed (exit 1) with no host.

## Next: the live OD-048 campaign (M1, cap 2 sessions)

Requires the game + host. No operator input after launch:

```powershell
scripts/launch-offline-replay-for-od.ps1        # canonical launch
# optionally pin the decoded session:
#   scripts/od-048-monitor-correlate-session.ps1 -SessionId <guid>
scripts/od-048-monitor-correlate-session.ps1    # stage -> monitor -> correlate
# read the verdict:
#   .data\od-048-<timestamp>.json  -> strongSurvivors (score >= 0.7)
```

Dead Rail session `019fb86c-c8e7-7004-9df6-a574f5a7835b` (duration_ticks
2,713,761,600 ≈ 271s) is the ground-truth source; the driver auto-picks the
most recent decoded session when `-SessionId` is empty.

**Exit criteria:** ≥ 1 strong survivor (score ≥ 0.7). Two sessions with none
→ descope per the strategy stop rules.

## After M1

- **M2:** family mapping (±4-byte neighbor components correlate) then x32dbg
  write-trace on the surviving family.
- **M3:** repeatability (2 launches × 2 replays) + publication per workflow
  Phase 5.

## Notes for the next operator

- **Start the driver BEFORE the replay reaches battle start.** The wall
  anchor is captured when the gate flips verified; a driver started
  mid-battle prints a WARNING and needs `-ReplayStartWallTimeUtc` from the
  Start marker, otherwise every sample clamps outside the ground-truth
  window and the run silently finds nothing.
- The staging scan uses `FloatTolerance` 8 — deliberately loose. Absolute
  coordinate bands are rare in memory, so the staged set stays small; a
  tighter tolerance would reintroduce the timing-precision problem.
- `-MaxReadRounds` default 90 (≈ 3 min at 2s). Dead Rail is ~271s; raise it
  for longer battles (`-MaxReadRounds 150`).
- The correlate request carries `replayStartWallTimeUtc` = the moment the
  gate flipped verified; the ±8s sweep absorbs the residual skew.
- The OD-047 exact-pause driver remains as a documented fallback for static
  values only — no live session is spent on it first.

## Amendment (2026-08-04) — deep-analysis bug-fix pass

A three-round deep-analysis hunt (reviewer + operator analysis) on the v4
build caught four real defects. All fixed, all regression-tested:

1. **Downsample overflow (critical).** `SqliteTrajectoryGroundTruthProvider`
   seeded `lastKeptTick` with `long.MinValue`; `tick - long.MinValue` wraps
   NEGATIVE for every non-negative tick, so any battle with > 256 samples
   (any battle > ~25s at 10 Hz) produced an EMPTY ground truth and the
   campaign could never correlate a real battle. Synthetic fixtures (< 256
   samples) stayed green and hid it. Fixed with a `first` flag; regression
   test `SqliteTrajectoryGroundTruthProviderTests` commits a 300-sample
   battle and asserts non-empty, monotone, edge-preserving trajectories.
2. **Whole-second shift sweep rejected fast movers.** A consistent integer
   shift leaves up to 0.5s residual = a CONSTANT position offset (speed ×
   residual) on every sample: at 17 m/s that is 8.5 units > the 6-unit
   tolerance, so a true field scored ~0. The sweep is now 0.5s-step; the
   winning shift is reported as `ShiftSeconds` for audit. Regression tests
   prove the same fast mover scores 1.0 at 0.5s steps and 0 at 1.0s steps.
3. **Unvalidated wall anchor.** A default/epoch `ReplayStartWallTimeUtc`
   silently clamped every sample to the last ground-truth value. The scorer
   throws and the correlate endpoint returns `discover.invalid_options`.
4. **Staging could not catch a moving battle.** The scans targeted the tick-0
   position band, which the game holds only for a sub-second window at battle
   start (or not at all while loading). Staging now: waits
   `-StageDelaySeconds` (15s) for load, targets the ground-truth sample
   NEAREST the expected current tick, scales tolerance from max entity speed
   × (delay + 25s), and retries (3 attempts). The sweep default is ±30s to
   absorb load latency between the Start marker and battle start.

Also fixed: `ReadBatchAsync` now isolates per-address failures (one throwing
read no longer blanks a whole 2000-address round); the correlate endpoint
validates series addresses as hex, uses the scorer's
`MaximumTimeShiftSeconds` constant, and reports `TotalSamples` from scored
samples only; the coordinator guards value-kind/width consistency.

**Corrected operational guidance (replaces the notes above):**
- The wall anchor is still captured at gate-verify; the sweep now covers
  ~30s of load latency + anchor skew, so starting the driver before battle
  start is still required, but a few seconds of load jitter no longer kills
  the run. Pass `-ReplayStartWallTimeUtc` (Start marker wall time) for the
  tightest anchor.
- Staging tolerance is auto-scaled; `-ScanTolerance` remains the floor.
- Read the `shiftSeconds` field of survivors: ≈ -load_latency validates the
  anchor; a large outlier flags a bad anchor.

## Amendment 2 (2026-08-04) — shiftSeconds audit before the live run

M1 prep hardening: `od-048-monitor-correlate-session.ps1` now audits the
winning shift of every scored address before forming the verdict. A survivor
whose |shiftSeconds| is within 2s of the sweep edge (`-MaxTimeShiftSeconds`)
means the true alignment is probably beyond the sweep — the classic
bad-anchor false positive (anchor captured mid-battle, or load latency
exceeded the 30s bound). Such survivors are demoted from "strong" to
"suspect": the verdict becomes `evidence-edge-aligned` when only
edge-aligned survivors exist, and the report gains `suspectEdgeAligned` plus
a `correlate.shiftAudit` section (threshold + suspect count). A clean strong
survivor must now be score ≥ 0.7 AND not edge-aligned.

## Amendment 3 (2026-08-04) — second bug-fix pass (shift band, staging staleness)

Round-2 deep-analysis found two more real defects; both fixed with tests:

1. **Shift-band masking of edge alignment (scorer + audit).** The
   closest-to-zero tie-break pulls the reported `ShiftSeconds` toward zero by
   up to (tolerance / local slope) seconds, so a true alignment riding the
   sweep edge could be reported as a benign interior shift and escape the
   edge audit. `CountMatches` now also tracks the AMBIGUITY BAND [min, max]
   of shifts achieving the best count; `TrajectoryCorrelationResult` and
   `CorrelateResultItemResponse` expose `ShiftMinSeconds`/`ShiftMaxSeconds`;
   the driver's audit flags when EITHER band edge touches the sweep boundary
   (method `band-edges` in `correlate.shiftAudit`). Regression test
   `AmbiguityBandIsReportedAndCanMaskEdgeAlignment` proves a slow-mover
   aligned at -25s reports shift -22 but band [-28, -22].
2. **Stale staging tick estimate during long scans (driver).** The estimate
   was computed once per attempt, but the 9 full-memory scans take tens of
   seconds each, so the later bands trailed the tank by scan-duration x speed.
   The estimate is now recomputed PER AXIS immediately before each scan
   (`staging entity=<id> tick_est=<n>` log lines).

Also: the anchor is now parsed with InvariantCulture +
AssumeUniversal|AdjustToUniversal (bare-UTC, Z-suffixed, and explicit-offset
ISO strings all normalize correctly); `SqliteTrajectoryGroundTruthProvider`
treats NULL/zero `duration_ticks` as not-found instead of returning a
degenerate ground truth. Full suite 588 passed / 0 failed; PSSA gate 0
warnings.

## Round-3 deep-analysis amendment (2026-08-04): battle-time budget + integration seams

Round-3 hunted the previously unexamined integration seams instead of re-reviewing
scorer math. All wire contracts verified correct against the C# sources (scan
request shape and caps, session-list DTO, gate-state DTO, rendezvous
`baseUri`/`capability`) and the coordinate-space assumption validated from real
decoded data (raw coords are small world units: x −255..258, y 0..62, z
−266..251 — exactly the float magnitudes that populate memory; real sessions
carry 27–28k samples, so the downsample bug would have hit every one).

Two real defects fixed in the driver:

1. **Nullable-session crash (auto session pick).** `SessionSummaryResponse`
   items carry a nullable `session`; a decode run with no battle session
   serializes a null entry, and `$page.items[0].session.id` under StrictMode
   would crash instead of exiting cleanly. The driver now guards
   `items[0].session` and exits `FAILED_newest_session_null` (code 2).
2. **Staging could consume the entire battle (structural).** Staging is up to
   3 attempts × 3 entities × 3 axes = 27 full-memory scans at tens of seconds
   each, vs. real battles ~250s. Unguarded, a slow first attempt plus retries
   leaves the monitor an empty world. The driver now derives a hard staging
   deadline from the decoded `duration_ticks` (battle end − 30s monitor
   minimum), checks it before every axis scan (`staging_budget_exhausted`),
   clamps the retry sleep so it never sleeps past the deadline, and the monitor
   early-exits `stoppedReason=battle-ended` once the decoded duration + 10s
   trailing window elapses. All comparisons use explicit UTC
   (`[datetime]::UtcNow`) — PS DateTime comparison ignores Kind, so mixing
   local/UTC would compare wall clocks, not instants.

Budget logic simulation (mock timelines): 271s battle + fast scans keeps all
scans and a 141s window; slow scans stop at the deadline with a 31s window;
90s battle preserves the 30s minimum; retry sleep clamps to 0 past deadline.
PSSA gate 0 warnings on 22 scripts; ASCII-clean; smoke fails closed.

Round-3 review pass (same session) caught three more real issues in the budget
implementation, all fixed:

1. **Monitor early-exit fired on the NOMINAL battle end.** The battle starts at
   tick 0 *after* the Start marker by load latency (absorbed up to
   `MaxTimeShiftSeconds` = 30s by the shift sweep), so wall-time battle end is
   `anchor + duration + load latency`. The exit used `anchor + duration + 10s`
   and could stop up to ~20s before the battle actually ended, dropping exactly
   the tail observations the correlate wants. The monitor now exits on the
   UPPER bound `anchor + duration + MaxTimeShiftSeconds + 10s`
   (`$monitorExitUtc`); the staging deadline correctly stays at the nominal
   end (stopping staging early is safe).
2. **`stagingS` mislabeled in the report.** It was computed at report-write
   time, so it measured staging + monitor + correlate. Now captured as
   `$stagingEndUtc` immediately after the staging loop.
3. **Staged-entity report was per-axis and could list unscanned entities.**
   The report entry was appended inside the axis loop (3 duplicates per
   entity — a pre-existing bug) and, after the budget break, entities whose
   scans never ran still appeared. The entry is now one per entity, after an
   entity-level `if ($budgetExhausted) { break }`, and the loop structure
   closes the axis/entity/attempt levels explicitly.

## Round-4 deep-analysis amendment (2026-08-04): correlate cap, HTTP timeouts, diagnostics

Round-4 hunted the previously unexamined pure functions, HTTP plumbing, and the
monitor/correlate data flow. Verified clean (no changes needed): `Convert-ToFloatHex`
is bit-exact little-endian matching the C# endpoint's `BitConverter.ToSingle`
(unit fixture `0000C842` = 100.0f confirms the wire format); `valueSummary` from
the read path is round-trip `"R"` invariant floats the driver's double.Parse
handles (NaN/Inf caught by Test-FiniteDouble); the correlate DTO's
`ShiftStepSeconds` has a C# default of 0.5 so omitting it in the body is valid;
scan and read responses both emit uppercase `0x{:X}` hex so the case-sensitive
series Dictionary keying is consistent; the report-write path already has its
exit-5 try/catch.

Three real defects fixed in the driver:

1. **Correlate observations cap mismatch (latent campaign-killer).** The C#
   endpoint rejects `Observations.Count > 2000`, but staging can yield up to
   `MaxStaged` (3000) addresses and the driver sent every series with >=2
   samples with no truncation -- and the round-2 auto-scaled staging tolerance
   (up to +/-800 units) makes >2000 staged addresses plausible. A 400 from
   correlate was swallowed into `FAILED_correlate` (exit 4) with zero
   evidence. The driver now truncates to the server cap, keeping the
   most-observed series first, logs the truncation, and records
   `observationsSent`/`observationsTotal`/`observationsCap` in the report.
2. **No HTTP timeouts.** `Invoke-Api`/`Get-GateState` used Invoke-RestMethod
   defaults: pwsh 7's 100s would abort a slow staging scan mid-scan; PS 5.1's
   indefinite timeout hangs forever on a dead host. `Invoke-Api` now takes an
   explicit `TimeoutSec` (300 default, generous for full-memory scans);
   `Get-GateState` polls with 10s so a hung host fails fast.
3. **Silent error swallowing + hard exit on transient scan failure.** `Invoke-Api`
   returned $null with no diagnostics, and a single failed staging scan exited
   the whole run (exit 2), wasting the session on a host hiccup. It now logs
   `api_failed method=... path=... status=... body=...` (error codes only, no
   sensitive data), and a failed scan sets `$scanFailed`, breaks the current
   attempt, and falls through to the retry loop (bounded by attempts + budget)
   -- after attempts are exhausted it exits `FAILED_staging_scan (all attempts
   failed)`. A corrected-loop simulation proved attempt-1-only failure now
   recovers and proceeds.

PSSA gate 0 warnings on 22 scripts; ASCII-clean; parse OK; smoke fails closed;
the api_failed diagnostics path verified against a closed port; truncation and
retry-logic simulations verified (2500 observations -> 2000 kept sorted
descending; fail-attempt-1 -> proceed on attempt 2).

## Amendment 4 — M2 family-mapping read-side (2026-08-05)

M2's read side is built and testable without a live run:

- **Server.** `TrajectoryFamilyBuilder` (`Core/Discovery/TrajectoryFamily.cs`)
  groups correlated results into coordinate families: base-relative 16-byte
  window, same-entity constraint (EntityId + ParticipantId; null==null),
  member offsets relative to the lowest address, canonical axes, and
  `Complete` = exactly three members (x/y/z) at distinct offsets with no
  edge-aligned member. Edge-aligned mirrors the driver audit (a band edge
  within 2s of the sweep bound). Multi-copy families are reported but flagged
  incomplete. `CorrelateResponse` gains `families`.
- **Driver.** At round `FamilyRefineAfterRounds` (10) a provisional correlate
  picks the top non-edge-aligned survivors (score >= 0.7, cap 25); their 8
  neighbor addresses (every 4-byte step in ±16) are staged for the remaining
  rounds. The final correlate sends family-neighbor series FIRST (they carry
  fewer samples and would otherwise be truncated away under the 2000 server
  cap), then the most-sampled rest. The verdict upgrades to `family-complete`
  when a clean triple exists; a strong-survivor run with no families logs the
  M2 stop-rule warning. `New-CorrelateBody`/`Get-CorrelateObservations` were
  factored out of the correlate section (fixing a PSSA PSReviewUnusedParameter
  finding by passing the previously-script-scoped values explicitly).
- **Verified:** 12 family-builder unit tests + endpoint serialization test
  (Core 35 / Host.Web 110 green); PSSA gate 0 warnings on 22 scripts; PS 5.1
  parse + ASCII clean; 16-check simulation of the REAL helper functions
  (neighbor math incl. dedup/pre-existing guard, survivor score/edge filter,
  family-priority selection, correlate body shape); smoke fails closed.
- **Remaining live step:** the M1 live run that produces survivors, then the
  M2 write-trace (pre-arm + x32dbg) on a surviving family.
**Review pass (same day):** the reviewer confirmed the builder + driver
sound, and three low-severity items were fixed: (1) grouping is now done per
ENTITY first, so interleaved foreign-entity addresses (entity 1 at 0x1000 and
0x1008 with entity 2 at 0x1004 between them) no longer split the legitimate
same-entity pair into singletons — regression test added; (2) the driver's
refinement comment now documents that a transient API failure retries on a
later round (recovery) while a no-scored-series pass marks it done; (3) the
report's `staged.union`/`staged.capped` now use a scan-only snapshot taken
before the family expansion, so the flag reflects the scan cap, not neighbor
staging.
## Amendment 5 — M2 write-trace driver (2026-08-05)

The M2 write side is built and offline-validated; the live step remains the
operator-run trace on a surviving family.

**`scripts/x64dbg-write-trace.ps1`** now supports two input modes and a
driver mode:

- **Family mode (M2):** `-FamilyFile <path>` accepts an od-048 correlate
  report JSON (its `families` array) or a bare family JSON (with `members`).
  The driver selects the best family deterministically — a `complete` x/y/z
  triple wins, tie-broken by summed member score — and arms hardware WRITE
  breakpoints on the member addresses at 4-byte width (`bph <addr>,w,4`,
  Float32 at 4-byte offsets). Members are armed in base-relative offset
  order, deduplicated, capped at DR0-DR3 (4); the rest are reported unarmed.
  Legacy survivor-file input (`-SurvivorFile`, no `-FamilyFile`) keeps its
  8-byte Double width (`w,8`) unchanged.
- **Driver mode (`-AutoWriteTrace`, the roadmap's M2 invocation):**
  1. Pre-arms the debugger when missing (`pre-arm-debugger.ps1 -AutoAttach`
     via `Invoke-AutoPreArm`; also `-AutoPreArm` alone).
  2. Gate-prechecks `OfflineReplayVerified` before injecting (exit 5).
  3. Play-state awareness: requires the replay HUD icon to read `playing`
     (probe `replay-play-state.ps1`); a confirmed `paused` replay writes no
     position fields, so the driver waits up to `-PlayProbeTimeoutSeconds`
     (default 60s) for the operator to resume, then fails closed (exit 7).
     `unknown` (HUD hidden) proceeds — the probe is advisory, never a
     hard gate. A periodic mid-window probe WARNs if the replay pauses
     during the trace. `-SkipPlayProbe` bypasses.
  4. Liveness: re-reads the armed family addresses through the guarded Host
     read API (`/api/v1/game/discover/read`, Float/4) to confirm they are
     live in the CURRENT process; a family from a previous launch fails fast
     (exit 8) instead of burning a window on dead addresses.
     `-SkipLivenessCheck` bypasses.
- **Evidence/report:** hits stay `<HitsDir>\odwt-0x<addr>-0x<rip>.bin`
  (the automatable `{rip}`-named channel) and the flat addr→rip lines go to
  `-ResultPath` as before. Family mode additionally writes a per-member
  report to `<ResultPath>.family.json`: each member's axis/offset/score and
  its captured rips, plus `hitsTotal`, `hitMembers`, and a
  `family-hit`/`family-no-hit` verdict.

New exit codes: 7 (replay confirmed paused and never resumed in time) and
8 (family liveness re-read failed — stale addresses).

**Offline validation (no game, no debugger):** PS 5.1 parse OK, ASCII clean,
PSSA gate 0 warnings (22 scripts), a 13-check simulation of the REAL
extracted helpers (complete-family selection over a higher-scored decoy,
DR0-DR3 cap at 4 with 2 unarmed, bare-family-without-score input, dedup +
invalid-address skipping, offset-ordered arming), and DryRun smoke in both
modes (family → `w,4` on the 3 members ordered 0x…3000/3004/3008; survivor →
`w,8` unchanged). Exit codes verified under real Windows PowerShell 5.1
(missing file → 2, no families → 2, success → 0).
**Review pass (same day):** the reviewer confirmed the driver sound and
flagged three items, all fixed:

1. **Liveness vs `-SkipGateCheck` misattribution.** The family liveness
   check now also requires `-not $SkipGateCheck` — it POSTs to the
   gate-gated read API, so with playback-only mode a failure was exit 8
   ("stale addresses") when the real cause was gate/host absence.
2. **Empty armed set fail-fast.** A family whose member addresses all fail
   to resolve now exits 2 (`FAILED_family_no_armed_members`) instead of
   arming zero BPs and reporting a misleading "clean no-hit window".
3. **Pre-arm fail-fast without the game.** `Invoke-AutoPreArm` now checks
   for a wotblitz process with a window before invoking
   `pre-arm-debugger.ps1 -AutoAttach` (which exits 0 even when it skips
   attach), avoiding a full 15s stall when there is nothing to attach to.
   Also documented that a bare top-level JSON array of families is not
   accepted as -FamilyFile input.
