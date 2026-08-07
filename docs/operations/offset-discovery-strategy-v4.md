# Offset-discovery strategy v4 — replay-guided trajectory correlation

**Date:** 2026-08-04
**Owner:** offset-discovery track
**Supersedes:** [`offset-discovery-strategy-v3.md`](offset-discovery-strategy-v3.md)

## Why v4 exists (the honest lesson of v3)

Strategy v3 ("exact-value pause scan") required the operator to pause the
replay at a **precise decoded clock value** (T1 = 60.000s ± 0.05s) so the
engine's `PassesExact` filter could keep addresses whose frozen value matched
the known target. That precision is machine precision, not human precision —
asking a human to hit a 50 ms window is a broken workflow, and the requirement
existed only because the pipeline had no live read of the value it was
hunting. That is a design defect, not an operator failure, and it is recorded
here as the teachable moment for this track.

The original strategy (restated and confirmed in the shared design review) was
never the exact-pause scan. It is:

1. The decoded replay is a **known, complete time-series** — coordinates,
   heading, HP over time. The replay itself is the marker; no pause is needed.
2. At one replay timestamp, scan memory for **approximate XYZ values** →
   stage the candidate addresses.
3. **Monitor those addresses while the replay plays** — don't pause, don't
   hunt. Re-read the fixed set repeatedly and score each address's value
   series against the known replay trajectory.
4. Very few unrelated memory locations reproduce a movement sequence with
   direction/speed changes → that is the field.
5. Multiple synchronized copies (replay buffer + active entity) are a
   **success**, not a failure — classify each by pause/speed/seek behavior
   later.
6. Once one coordinate component is found, the candidate family (the other
   two components at ±4-byte neighbors, then rotation/velocity/HP/entity ID
   pointers) maps fast.

v4 is that strategy, implemented. The exact-pause machinery (v3 M0, the
`exact` compare mode, `od-047-exact-scan-session.ps1`) remains in the repo as
a documented fallback for a value that is genuinely static (e.g. a HUD read
while paused) — it is no longer the primary path and no live session may be
spent on it first.

## What v4 builds (2026-08-04)

| Piece | Where | What it does |
|---|---|---|
| Pure correlation scorer | `Core/Discovery/TrajectoryCorrelation.cs` | Scores a monitored 1-D address series against every entity axis of the decoded trajectory. Per-axis with sign flips, piecewise-linear tick lookup, **sub-second (0.5s-step) time-shift sweep** (driver default ±30s) that absorbs Start-marker anchor error AND load latency — no precise pause, no OCR. Reports the winning `shiftSeconds` (the anchor error) AND the ambiguity band `shiftMinSeconds`/`shiftMaxSeconds` (all shifts achieving the same match count) for audit — the band edges expose sweep-edge riding that the closest-to-zero reported shift can mask. Excludes stationary ground-truth axes and constant observed series. 16 unit tests. |
| Ground-truth provider | `Storage.Sqlite/SqliteTrajectoryGroundTruthProvider.cs` | Reads `position_samples` per participant from the decoded session, downsampled to ≤ 256 samples/entity, plus `duration_ticks` and the `viewpoint_participant_id` (local player). Purely offline. |
| Read primitive | `IGameMemoryScanner.ReadAddressesAsync` + `POST /api/v1/game/discover/read` | Re-reads a staged set of absolute addresses (≤ 2000/call) through the guarded reader; the missing "monitor a fixed candidate set across time" capability. |
| Correlate endpoint | `POST /api/v1/game/discover/correlate` | Loads ground truth, runs the scorer, returns ranked survivors plus the M2 `families` section. |
| Family builder | `Core/Discovery/TrajectoryFamily.cs` | Pure M2 grouping (no live access): scored addresses inside one base-relative 16-byte window reproducing the SAME entity's axes become a family, with member offsets, axes covered, and `Complete` = the clean x/y/z triple at distinct offsets with no edge-aligned member (multi-copy families are reported but flagged incomplete). 12 unit tests. |
| Trajectory endpoint | `GET /api/v1/game/discover/trajectory/{sessionId}` | Serves the downsampled ground truth for staging and reporting. |
| Session driver | `scripts/od-048-monitor-correlate-session.ps1` | Gate wait → load-settle delay → stage (viewpoint + top movers; scans target the ground-truth sample nearest the expected current tick, tolerance auto-scaled from max entity speed × load-latency bound, retried until the battle is loaded; **battle-time budget**: staging deadline = decoded battle duration − 30s monitor minimum, so slow scans cannot consume the whole battle) → monitor loop (re-read every 2s; early-exits `battle-ended`; at round 10 a provisional correlate re-stages the ±16-byte neighbors of the top non-edge-aligned survivors) → correlate (family-neighbor series kept first under the 2000 server cap; response `families` section) → JSON report with verdict (upgraded to `family-complete` for a clean triple). No operator input after launch. |

**Key evidence fact (verified from the decoded session):** the replay clock
runs at **10,000,000 ticks per real second** — the synthetic 120s fixture is
exactly 1,200,000,000 ticks and the real decode puts the HUD 1:00 frame at
599,839,248 ticks ≈ 59.98s. The driver anchors wall time at the replay Start
marker and the scorer's shift sweep absorbs the residual skew AND the load
latency between the Start marker and battle start (observed ~5–30s).

**Bug-fix pass (2026-08-04):** the deep-analysis review of the v4 build
caught and fixed four real defects: (1) the ground-truth `Downsample`
overflowed for every battle > 256 samples and silently returned an EMPTY
ground truth (regression test added); (2) the whole-second shift sweep
rejected fast movers (a 0.5s residual × 17 m/s = 8.5 units > 6-unit
tolerance) — the sweep is now sub-second; (3) a default/epoch wall anchor
produced silent garbage evidence — now rejected; (4) the staging scans
(targeting the tick-0 position) ran before the battle entities existed or
after the tank had moved — staging now waits for load, targets the
nearest-tick sample, and scales tolerance from entity speed. See the handoff
amendment.

## Milestones (see `offset-discovery-roadmap.md`)

- **M1 — Live monitor-and-correlate campaign (OD-048).** CAP: 2 sessions.
  Launch the replay, let it play, run `od-048-monitor-correlate-session.ps1`.
  Verdict from the report: strong survivors = score ≥ 0.7 (addresses that
  reproduce ≥ 70% of the movement samples).
- **M1.5 — Viewpoint-first pivot (2026-08-06).** Stage ONLY the viewpoint
  player and trace the first strong survivor immediately; alternate-entity
  decoys are excluded before the shift audit (`od-048 -StageViewpointOnly`,
  see the roadmap). The FRESH15→19 live campaign (six rounds) hardened the
  infrastructure end-to-end — every round surfaced one real bug, all now
  fixed and committed: attach-freeze auto-relaunch, battle-started staging
  gate, attendance-latency correction, capability-401 retry (host rotates the
  token every ≥15s publish), the 30→90s shift sweep, the FRESH19
  zero-viewpoint `$null.Count` crash (caller-side `@()` on
  `Select-ViewpointResults`), and fresh-marker polling on campaign relaunch.
  **FRESH20 and FRESH21 (the re-baselined budget) both returned
  `verdict=evidence-strong`** — non-edge span-275 z survivors at score
  ≥ 0.857/0.92, confirming the offline dry-run's prediction in live shape.
  FRESH20 fired the trace but the battle ended mid-window (fixed via the
  adaptive trace window + rounds 70→50); FRESH21 skipped it because a stale
  20s band floor (1/3 of the old ±30s sweep, never re-derived when the sweep
  widened to ±90) refused every strong survivor in the solo gate. Fixed
  (`7c02f7d`): band floor re-derived to **60s** (= 1/3 of ±90) and a new
  **span floor** (default 10 units) catches the FRESH10 static-degenerate
  class the widened floor alone can't. **FRESH22 completed the first armed
  trace end-to-end** (BP armed, script injected, clean 25s window) but got
  `family-no-hit`: the decoded replay proves the tank was moving through the
  whole window, so the armed span-75.5 partial copy was not the per-frame
  field — the score-desc tiebreak beat the span-275.4 consensus class (~20
  synchronized z copies). Fixed (`6f36067`): **span-first selection** (span
  desc, score desc, band asc) + **arm the top-4 consensus addresses**
  (DR0-DR3). The remaining gate is **FRESH23** — arming the consensus class,
  producing the first real `odwt-*.bin` hit report (writer RIP/RVA, base
  register, displacement, nearby-object dump).
- **M2 — Family mapping + write-trace.** Read-side BUILT (2026-08-05): the
  driver re-stages the ±16-byte neighbors of the top provisional survivors
  mid-battle, and the correlate response's `families` section groups the
  scored addresses into coordinate families (same entity, one byte window;
  `complete` = the clean x/y/z triple at distinct offsets with no edge-aligned
  member). One session maps all three coordinate components, and the verdict
  upgrades to `family-complete`. Write-side BUILT (2026-08-05, offline
  validated): `x64dbg-write-trace.ps1 -FamilyFile <od-048 report>
  -AutoWriteTrace` pre-arms x32dbg, gate-prechecks, requires the replay
  play-state `playing` (a paused replay writes nothing — fail-closed exit 7),
  re-reads the family addresses for liveness in the current process
  (fail-closed exit 8 on a stale family from a fresh launch), arms 4-byte
  write breakpoints on the member addresses, holds the trace window, and
  writes a per-member hit report (`<ResultPath>.family.json`) with a
  `family-hit`/`family-no-hit` verdict.

  **M2 pivot (2026-08-06, `882227b`): the x64dbg write-BP route is CLOSED.**
  FRESH26–33 ran the full fixed stack cleanly every time (evidence-strong
  consensus, reused debugger, values_changed=true, m1_exit=0) with zero
  capture on every channel; the FRESH32/33 probes root-caused it: in-script
  `bpm` errors (`Error executing command!`), `bpm`/`bph` never fire even via
  the command bar on a constantly-writing synthetic target, worker-thread
  writes escape main-thread DR hardware BPs, and every UIA log read was
  reading chrome names, not log text (full chain:
  [`handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md`](handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md)).
  No live session may be spent on x64dbg. **Successor: a C#-native
  guard-page write interceptor** (PAGE_GUARD + debug-event handling +
  GetThreadContext RIP) inside the UltimateScanner/GameIntegration Win32
  allowlist, buildable + testable offline before any live session. The M1
  address-level evidence (strong correlate + value-liveness on a moving
  world) stands as the interim result.

  **Same-launch constraint (2026-08-05):** the DAVA viewer has **no rewind**
  (seek-forward-only) and no replay hot-swap, and `roll-replay-time-increased.ps1`
  is a memory-scan roll, not a replay rewind. The write-trace window is the
  **tail of the same playback**: the M1 final correlate fires with battle time
  remaining and the write-trace starts immediately on the verdict. Full
  operator sequence + timing budget:
  [`offset-discovery-m1-m2-choreography.md`](offset-discovery-m1-m2-choreography.md).
- **M3 — Repeatability + publication.** 2 launches × 2 replays, then publish
  per the workflow Phase 5.

## Stop rules (unchanged in spirit)

- M1: no strong survivor (score ≥ 0.7) across 2 sessions → archive, close out
  in the ledger + blocker log, mark the track research-only.
- M2: family correlation fails on the survivors → recheck staging, do not
  burn M3.
- M3: 0 write-trace hits in 2 attempts on a small clean set → descope.

**M1 cap re-baseline (decision, 2026-08-06):** the M1.5 pivot re-baselines
M1's 2-session cap. The FRESH15→19 campaign produced zero valid scientific
tests of the current pipeline — every round failed on a distinct, now-fixed
defect, and the offline dry-run scores the corrected anchor at 1.000 @ shift 0
through the real scorer. Budget: FRESH20 + at most FRESH21 (a session counts
only when valid: staging gate post-match-begin, smoke green, no crash,
correlate completed). Hard archive trigger: 2 valid sessions with no strong
survivor → archive regardless of sunk cost. **Outcome: both budget sessions
returned strong verdicts** — the archive trigger did not fire; the remaining
live need is arming + tracing the survivor (FRESH23, M2's own live
requirement, not a budget extension). See the roadmap Descope gate.

## Guardrails added by v4

- **Never ask a human for machine precision again.** Any campaign design whose
  evidence depends on an operator landing a value within tolerance of a
  target is rejected at design review.
- Do not spend a live session on the exact-pause path (v3 M1) first; the
  correlation campaign is the primary path.
- The staging scan tolerance stays loose (default 8 world units) — absolute
  coordinate bands are rare in memory, so a loose tolerance still yields a
  small staged set; tightening it first reintroduces the timing-precision
  problem.
- Same session/launch identity discipline as v3: the gate must be
  `OfflineReplayVerified` for every memory op and the report records the
  staged/monitored/correlated counts so a partial session is distinguishable
  from a negative result.
