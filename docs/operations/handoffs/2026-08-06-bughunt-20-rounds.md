# Handoff — 20-round deep-analysis bug hunt (scorer, family gate, anchor, rendezvous)

Date: 2026-08-06 · Status: Committed · Type: bug hunt + hardening
Prior state: FRESH13 shipped (band-width floor in the family gate) at `e73d2cc`.

## What this session did

Two consecutive 10-round deep-analysis bug hunts across the strategy-v4
trajectory-correlation pipeline. Rounds 1–10 hunted the family-selection
layer, the od-048 gate/report, the C# scorer/family builder, the attach-smoke
path, PS-version compat, the e2e wrappers, the wire contract, and x64dbg
automation internals. Rounds 11–20 hunted the family builder + endpoint wire
contract, the live-launch pipeline, the storage ground-truth provider, the
read endpoints + coordinator lease lifecycle, x64dbg arm-plan/rip-parse
internals, the correlate observation pipeline, the rendezvous lifecycle, and
regression coverage of the new behavior.

Every round was verified against the actual code (not just reasoned): each
reviewer finding was either confirmed-and-fixed or confirmed-already-handled
with the code evidence cited.

## Changes shipped (commit after this handoff)

**Real bugs fixed:**

1. **`src/WotBTreader.Core/Discovery/TrajectoryCorrelation.cs` — early-break
   attribution bug (round 3).** The scorer broke out of its candidate scan on
   the first *perfect* match, so a later (entity, axis, sign) candidate that
   TIED on match count with a NARROWER ambiguity band was never evaluated.
   The first-scanned degenerate attribution (a full-sweep band, e.g.
   FRESH10's `y@1.0` flood) always won. Removed both early breaks; the
   tie-break now prefers the narrower band. Regression test
   `PerfectMatchTieBreaksToNarrowerBandAcrossEntities` was **proven to fail
   against the old code** (EntityId 1 instead of 2) and pass with the fix.

2. **`src/WotBTreader.Core/Discovery/TrajectoryFamily.cs` — duplicate-address
   dedup (round 11).** The same address scored twice (duplicated observation
   entries, or two series parsing to the same address) created two members at
   offset 0, inflating member count and corrupting the complete-triple rule
   and the span. Now dedupes per-entity, keeps the higher-scoring copy,
   compares parsed longs (case-insensitive). Cross-entity duplicates are
   deliberately NOT merged (documented in code; the write-trace re-dedups in
   `Get-FamilyArmPlan` before arming). Four tests pin the behavior:
   higher-score-wins, tie keeps first, higher-first-not-replaced,
   mixed-case collapse.

3. **`scripts/od-048-monitor-correlate-session.ps1` — rendezvous expiry
   acceptance (round 18).** A hard-killed host leaves its rendezvous record
   on disk (only graceful shutdown deletes it); `Get-Rendezvous` accepted it
   and the driver POSTed a dead host's capability token until restart,
   wasting rounds on `web.local_capability_required` 401s. Now rejects
   records expired >30s (treated as absent, so `Refresh-Rendezvous` keeps the
   last known good record; the initial read fails fast). Parsing uses
   InvariantCulture per repo convention.

4. **`src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — 1-sample
   series contract mismatch (round 17).** The correlate endpoint accepted
   `samples.Count >= 1` but the scorer silently drops series with <2 valid
   samples after non-finite filtering — a wasted observation slot and an
   `AddressesScored < observations` that looked like a server fault. Floor
   tightened to <2 → 400, matching the scorer's actual minimum and od-048's
   pre-POST skip (line 384).

**Verified clean (no change needed) — the notable confirmations:**

- **Round 12 (live-launch pipeline, 8 items):** dotnet kill is filtered by
  `CommandLine -match 'Host\.Web'` (not broad); replay selection excludes
  32-hex staging names and records sha12; the clicker re-scans for log
  rotation each call (`Get-CurrentBlitzLog` inside `Test-ReplayStartedMarker`);
  play-state is fail-closed (anything but `paused` → "cannot confirm paused");
  window-wait has bounded loops; PS 5.1/pwsh 7 split is documented and
  enforced; `game-now.py` has no date math at all.
- **Round 13 (storage provider):** zero-duration guarded (line 42);
  `participant_id IS NOT NULL` in SQL; downsample keeps first AND last sample;
  `raw_x/y/z REAL NOT NULL` in the schema, so the unguarded `GetDouble` is
  safe.
- **Round 14 (read endpoint + coordinator):** 2000-address cap → 400; hex
  overflow → 400; kind/size validation; the process lease is disposed on all
  paths (try/catch/Dispose in `GuardedMemoryReader`); `"R"` formatting means
  NaN/Inf round-trip as `[double]` in PS and are dropped by the scorer.
- **Round 15 (x64dbg internals):** arm-plan dedup is case-insensitive
  (`ToLowerInvariant`) with the DR0–DR3 cap correct (4 armed max); no
  `bpmwlog` remains; rip-parse uses a case-insensitive
  `ODWT_HIT addr=... rip=...` regex with a `odwt-0x*.bin` savedata fallback;
  `Set-Content -Encoding ascii`.
- **Round 19 (post-fix scorer/family review):** results ordering cannot drop
  candidates (one result per address); `bandWidth == 0` tie is deterministic;
  the dedup mutation of list contents while enumerating `byEntity.Values` is
  safe; edge-aligned computation correctly consumes the narrower-band winner.
- **Rounds 17/18 (observation pipeline / rendezvous):** every sample stamp is
  `'o'` (sub-second) at lines 922/952; series only grow (no trim — but 2s
  interval × 5000-cap is 2.8h, unreachable in a ~271s battle); the overlay
  `RendezvousLocator` already rejects expired records and exited processes;
  the host deletes the rendezvous file on graceful shutdown.

## Evidence links (FRESH series)

- **FRESH10** (`y@1.0` flood): the degenerate full-sweep band this hunt's
  scorer fix targets — see `docs/operations/handoffs/2026-08-05-od-049-fresh*.md`
  and the FRESH12 audit below.
- **FRESH12** (`2026-08-06-fresh12-y-survivor-audit.md`): `0x1FC57238` is real
  evidence (band [−10, −8], interior) while `0x22FC05D8` was armed with a
  40s degenerate band — the exact failure the tie-break fix and band floor
  prevent.
- **FRESH13** (`e73d2cc`): band-width floor in the family gate; this hunt
  hardened the gate's skip-reason reporting around it.

## Tests / validation

- `TrajectoryCorrelationScorerTests`: 19 pass (was 18; +1 tie-break
  regression, verified to fail on old code).
- `TrajectoryFamilyBuilderTests`: 18 pass (was 14; +4 dedup tests).
- `dotnet build WotBTreader.sln -c Release`: 0 warnings.
- Full suite: 12/12 test projects pass (609 + 4 new tests; 2 opt-in skips).
- PSSA gate (`scripts/invoke-scriptanalyzer.ps1`): PASSED (40 advisories, 0
  errors/parse failures — one of the round-1 fixes was a parse-breaking
  multi-line `if` chain in od-048 that the gate caught).

## Assumptions / unknowns

- The scorer tie-break cost is bounded (only perfect-match observations
  continue scanning remaining ground series) but unmeasured on a 15-entity
  battle; the correlate is a one-shot, so acceptable.
- The dedup's higher-score copy could in pathological cases carry a different
  axis than the dropped lower copy (divergent duplicate observations);
  defensive-only since `Get-FamilyArmPlan` re-dedups — documented in code.

## Integration risks

- The endpoint 1-sample floor (<2) is a behavior change: any caller sending
  1-sample series now gets 400. The only production caller (od-048) already
  skips <2, and all 113 Host.Web tests pass unchanged.
- The rendezvous expiry check treats records expired >30s as absent. With a
  5-min lease and 2-min publish cadence a live host's record always has ≥3
  min remaining, so the check can only fire on a dead host. The 30s margin
  covers clock skew.

## Recommended next steps

1. Live-verify the rendezvous expiry handling: start the host, let it
   publish, hard-kill it, confirm `Get-Rendezvous` returns null and the
   overlay still fails clean on the stale file.
2. Close the non-finite sample gap: reject series with <2 FINITE samples at
   the endpoint (fail-closed, before scoring).
3. Third 10-round hunt on unexplored areas: Blazor dashboard + SignalR push,
   overlay ViewModels + PositionPlot transform math, replay decoder
   (pickle/protobuf packet layout), Storage.Sqlite comparison-run repository.
