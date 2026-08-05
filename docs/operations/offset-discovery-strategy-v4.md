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
| Pure correlation scorer | `Core/Discovery/TrajectoryCorrelation.cs` | Scores a monitored 1-D address series against every entity axis of the decoded trajectory. Per-axis with sign flips, piecewise-linear tick lookup, whole-second **time-shift sweep** (default ±8s) that absorbs Start-marker anchor error — no precise pause, no OCR. Excludes stationary ground-truth axes and constant observed series. 11 unit tests. |
| Ground-truth provider | `Storage.Sqlite/SqliteTrajectoryGroundTruthProvider.cs` | Reads `position_samples` per participant from the decoded session, downsampled to ≤ 256 samples/entity, plus `duration_ticks` and the `viewpoint_participant_id` (local player). Purely offline. |
| Read primitive | `IGameMemoryScanner.ReadAddressesAsync` + `POST /api/v1/game/discover/read` | Re-reads a staged set of absolute addresses (≤ 2000/call) through the guarded reader; the missing "monitor a fixed candidate set across time" capability. |
| Correlate endpoint | `POST /api/v1/game/discover/correlate` | Loads ground truth, runs the scorer, returns ranked survivors. |
| Trajectory endpoint | `GET /api/v1/game/discover/trajectory/{sessionId}` | Serves the downsampled ground truth for staging and reporting. |
| Session driver | `scripts/od-048-monitor-correlate-session.ps1` | Gate wait → stage (viewpoint + top movers, 3 axis scans each) → monitor loop (re-read every 2s) → correlate → JSON report with verdict. No operator input after launch. |

**Key evidence fact (verified from the decoded session):** the replay clock
runs at **10,000,000 ticks per real second** — the synthetic 120s fixture is
exactly 1,200,000,000 ticks and the real decode puts the HUD 1:00 frame at
599,839,248 ticks ≈ 59.98s. The driver anchors wall time at the replay Start
marker and the scorer's shift sweep absorbs the residual skew.

## Milestones (see `offset-discovery-roadmap.md`)

- **M1 — Live monitor-and-correlate campaign (OD-048).** CAP: 2 sessions.
  Launch the replay, let it play, run `od-048-monitor-correlate-session.ps1`.
  Verdict from the report: strong survivors = score ≥ 0.7 (addresses that
  reproduce ≥ 70% of the movement samples).
- **M2 — Family mapping + write-trace.** For each strong survivor, read the
  ±4-byte neighbors (the other two coordinate components) and confirm they
  correlate; then pre-arm x32dbg and write-trace the surviving family.
- **M3 — Repeatability + publication.** 2 launches × 2 replays, then publish
  per the workflow Phase 5.

## Stop rules (unchanged in spirit)

- M1: no strong survivor (score ≥ 0.7) across 2 sessions → archive, close out
  in the ledger + blocker log, mark the track research-only.
- M2: family correlation fails on the survivors → recheck staging, do not
  burn M3.
- M3: 0 write-trace hits in 2 attempts on a small clean set → descope.

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
