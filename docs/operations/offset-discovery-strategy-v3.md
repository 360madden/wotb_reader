# Offset-discovery strategy v3 — exact-value pause scan

**Date:** 2026-08-04
**Status:** Decision recorded. Execution plan and milestones in
[`offset-discovery-roadmap.md`](offset-discovery-roadmap.md).
**Supersedes:** the implicit v1/v2 ordering (rolling "increased" campaign →
delta pilot). Exact-value pause scan is now the primary pivot; the delta
pilot (OD-045) is the fallback filter.

## Why this document exists

The offset-discovery track has consumed ~78 of 256 repository commits, ~55 of
84 session handoffs, and 55 ledger experiment rows since 2026-07-30. This
record states the outcome honestly, captures what the effort actually produced
(including the teachable negatives), and fixes the terms under which the track
continues — or stops.

## The honest scoreboard

| Metric | Value |
|---|---|
| Ledger experiment rows | 55 (44 dynamic/static OD sessions + tooling/index rows) |
| Live managed game launches | 100+ |
| Effort share of repo history | ~78/256 commits, ~55/84 handoffs |
| Runtime-supported fields | **0** (all eight offsets publish `0`) |
| Write-trace hits (`{rip}` evidence) | **0** — across OD-009…OD-044 |
| Best dynamic convergence | 1 survivor (OD-044, then identified as the kernel clock), ≤10 reached 3× (OD-031 ×2, OD-036) |
| Static roots into gameplay state | **0** — 8 static sessions all landed on infrastructure |

The one candidate that came closest (`playerYaw`) is quarantined as
*ambiguous* — three mutually conflicting representations.

## What the effort actually produced (durable assets)

1. **A working, hardened live-discovery pipeline.** Managed launch → offline
   gate → rolling driver → survivor staging → x32dbg direct attach →
   automated hardware write-BP injection. Mechanically proven end-to-end
   (OD-044), with every operational failure fixed: 401 capability rotation,
   steady-state snapshot gate, candidate-count optimization, harvest retry,
   plateau-stop, the replay-start flake (double-click + SW_RESTORE churn) and
   the mid-battle evidence-lifetime termination (liveness heartbeat,
   2026-08-04). Reusable regardless of the discovery outcome.
2. **Structural negatives that make dead ends non-repeatable.** RTTI walks,
   vtable singletons, AnyFn invoker tables, EH funclet tables: Blitz stores
   gameplay state in heap object graphs, not simple globals. The "increased"
   marker is non-selective (any monotonic ticker survives; the value-bound
   11–17 plateau). The `KUSER_SHARED_DATA` kernel-clock survivor class is
   identified and dropped. Automated CE Windows-debugger write-BPs are ruled
   out.
3. **The replay decoder.** The project decodes replays perfectly — exact
   ground truth for every battle value at every frame (281s of position data,
   events, clocks). This is the asset the new strategy keys on.

## Why the return was poor (the teachable moment)

1. **Marker non-selectivity.** "Increased"/"changed" accept any ticking
   value: frame counters, physics clocks, particle timers, the kernel clock.
   Rolling convergence is a filter problem, not a lease problem.
2. **The conversion step never landed.** A heap-dynamic address is useless
   without write-trace → instruction → register base → member displacement →
   root, and that step produced 0 hits every time (OD-009…OD-044).
3. **The static path systematically lands on infrastructure.** RTTI, AnyFn,
   EH tables — 8 sessions of structural negatives, zero gameplay roots.
4. **~Half the campaign went to pipeline survival.** The flake, 401 rotation,
   lease walls, and staging consumed OD-017…OD-044. Necessary, now fixed —
   but cost, not progress.
5. **The strategy never used the one asset that is exact.** Every scan was a
   transition filter against a *relative* marker. No scan ever asked "which
   address holds exactly this known value at this paused frame?" — despite the
   decoded replay making the expected value known to 6+ significant digits.

## The v3 strategy

Use the decoded replay as the scan key instead of relative transitions:

1. **Pause the replay at a decoded clock value T1** (e.g. 60.000s into the
   battle). The in-memory `replayTime` field freezes at exactly T1 (the
   rolling driver already proved the field only moves on resume pulses).
2. **Exact-value scan** (`CompareMode=exact`): keep addresses whose current
   value is within a tight tolerance of an absolute target. Run the 3–4 known
   unit variants (4.0s / 4000ms / 4,000,000 ticks) as separate scans.
3. **Two-pause fingerprint:** repeat at T2 (e.g. 120.000s). The true field
   must hold T2 as well — the address intersection across the T1 and T2 runs
   eliminates every coincidence match. This is the strongest identifier the
   campaign has ever had.
4. **Fallback filter:** if the exact scan underperforms, run the delta pilot
   (OD-045: `-CompareMode delta -DeltaTarget 4.0 -DeltaTolerance 0.4`).
5. **Conversion unchanged:** stage the small set → x32dbg hardware write-BPs
   → first `{rip}` → member displacement → root hunt → repeatability.

Expected collapse: an exact-value match on a paused 8-byte field is
structurally near-unique (the replayTime double at a specific moment), versus
the 66M → 11–17 plateau of "increased". Paused = clocks frozen, so tickers
cannot contaminate the set.

## Decision terms (stop rules)

- **2 live sessions cap** for the exact-scan campaign (M1).
- **Descope immediately if:** no collapse below ~1% of baseline in 2
  sessions; or the two-pause fingerprint matches no address; or write-trace
  yields 0 hits in 2 attempts on a small clean set.
- **Descope action:** archive the pipeline + evidence, append the closeout to
  the ledger and blocker log, mark the track research-only, and refocus on the
  product — which does not require offsets (the replay decoder serves the
  HUD).

## What success looks like

One correctly classified, reproducible candidate (member displacement or
pointer-chain), stable across 2 launches × 2 replays, published as
`Candidate` per `offset-discovery-workflow.md` Phase 5. Everything short of
that is recorded as a ruled-out hypothesis — which is itself the deliverable
of a research track.
