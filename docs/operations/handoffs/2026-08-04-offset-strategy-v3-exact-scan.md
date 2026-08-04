# Handoff: offset-discovery strategy v3 + M0 exact-scan capability

**Date:** 2026-08-04
**Status:** Decision recorded; M0 (exact-scan capability) complete and green
**Campaign:** WoT Blitz PC offset discovery (supersedes the OD-045-first ordering)

## Repository state

- Branch `main`, head `6dc21c0` (`docs(ops): record OD-044 replay-start flake
  root cause and fix`, pushed). The pre-existing unstaged edit to
  `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md` remains
  untouched.
- This session's changes are **uncommitted**: docs (strategy, roadmap, ops
  README index, workflow amendment) + code (engine, contracts, endpoint,
  driver) + tests.

## Changed files and public contracts

- `docs/operations/offset-discovery-strategy-v3.md` (new) — honest progress
  assessment, the exact-value pause-scan pivot, descope stop rules.
- `docs/operations/offset-discovery-roadmap.md` (new) — M0–M4 milestones with
  caps, exit criteria, and the descope gate; M0 marked complete.
- `docs/operations/README.md`, `docs/operations/offset-discovery-workflow.md`
  — index row + next-session amendment pointing at the v3 docs.
- `ultimate-scanner/MemoryScanEngine.cs` — new `exact` compare mode
  (`PassesExact`: current value within tolerance of an absolute target);
  shared `TryDecodeNumber` helper extracted for `PassesDelta`/`PassesExact`.
  Internal API only.
- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` — doc comments
  only (`DeltaTarget`/`DeltaTolerance` now document delta **and** exact).
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — `exact` in the
  allowlist; targeted-mode parameter validation; `discover.invalid_exact_options`
  error code.
- `scripts/roll-replay-time-increased.ps1` — `-CompareMode exact` +
  `-ExactTarget`/`-ExactTolerance`: no Space pulses (replay stays paused),
  1s stability re-read rounds, survivors read from `currentCount`,
  mode-aware log labels.
- Tests: `PassesExact` units (7), exact endpoint validation/forwarding (3),
  engine-level exact validation (2). No public contract changes.

## Strategy decision (recorded)

The offset track has consumed ~78/256 commits, ~55/84 handoffs, 55 ledger rows
with **0 runtime-supported fields and 0 write-trace hits**. v3 pivots from
relative-transition filters to the **exact-value pause scan**: pause the replay
at a known decoded value (replayTime at a given frame) and exact-scan for that
absolute value across the 3–4 unit variants; the two-pause-point fingerprint
(T1 and T2) then identifies the field unambiguously. Stop rules: 2 live
sessions cap for M1; descope on no collapse, empty fingerprint, or 0 hits in
2 write-trace attempts.

## Validation

- Build: `dotnet build -c Release` → 0 errors.
- Tests: full suite green — **558 passed, 0 failed** (2 opt-in skips);
  GameIntegration 241, Host.Web 98.
- Scripts: `roll-replay-time-increased.ps1` PS parser 0 errors.

## Assumptions and unknowns

- The engine-level end-to-end rolling-baseline interaction (exact filter +
  `advanceBaseline` keeps the matched set) is reasoned through but not
  unit-tested — the engine has no fake-reader seam, so existing delta/changed
  modes share the same coverage gap. M1's live campaign is the real test.
- `UInt64` → double decoding loses sub-tick precision above 2^53 (noted in
  code); irrelevant to the Double replayTime campaign.
- M1 invokes the roll driver directly; `od-018-session.ps1` has no
  `-CompareMode`/`-ExactTarget` passthrough yet (add before M3 if the held
  operator window is needed inside the session driver).

## Integration risks

- `exact` reuses the `DeltaTarget`/`DeltaTolerance` wire fields (documented).
  `discover.delta_only_with_delta_mode` still names the shared
  params-on-other-modes rejection; acceptable, not a behavior change.
- The workflow doc's OD-045 delta pilot remains as the fallback filter (M1
  stop rules gate it).

## Recommended next steps

1. Commit + push this milestone (docs + M0 code) after review.
2. **M1 live campaign (OD-047):** pause the replay at a decoded T1, run the
   exact scan across unit variants (`-ExactTarget <T1>,<T1*1000>,<T1*1000000>`
   one invocation each, `-ExactTolerance 0.05`), record per-variant collapse
   from the ~66M baseline.
3. **M2:** repeat at T2 and intersect the survivor sets (two-pause fingerprint).
4. Update the ledger (`OD-047` row) and this roadmap's M1/M2 checkboxes.
