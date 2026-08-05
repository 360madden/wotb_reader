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
