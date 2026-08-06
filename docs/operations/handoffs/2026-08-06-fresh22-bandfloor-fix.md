# FRESH22 — re-derived band floor + span floor (the FRESH21 no-trace postmortem)

Date: 2026-08-06
Status: **fixed + validated offline; FRESH22 = the round that arms the survivor**

## FRESH21 live outcome (session 2 of the re-baselined budget)

`od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly` (50 rounds, adaptive
trace window):

- Launch stack: resize 640×360, click round 1, gate, marker, staging tick 5.2s,
  staged=3000 (full set) — all clean.
- Attach-smoke **passed first try** (`pause=True bpm=yes resume=True`, exit 0).
- 50/50 rounds sampled, 150,000 samples, **zero hard 401s** (retry fix held).
- Correlate: **verdict=evidence-strong, 32 strong survivors** — 29 z matches at
  score 0.92, 46/50, span 275.15 (= the decoded z span), shift 0, non-edge band
  [-19.5, +12] (31.5s).
- **BUT: `families=0` → `no_families_from_survivors` → auto-write-trace SKIPPED.**
  No `odwt-*.bin` produced.

## Root cause (two stale gates, one regression)

1. **The band floor was never re-derived when the sweep widened.** od-048's
   `AutoTraceMaxMemberBandSeconds` and the write-trace's `MaxMemberBandSeconds`
   were both 20s = 1/3 of the **old ±30s (60s) sweep** — the FRESH12 degenerate
   threshold. Commit `888fb58` widened the sweep 30→90 but left the floor at 20s.
   The write-trace's own doc says: *"the floor is absolute, not sweep-relative;
   pair it with the same -MaxTimeShiftSeconds that produced the bands."* FRESH21's
   z bands (31.5s = 17.5% of the ±90 sweep — discriminating, not "matches at any
   shift") failed the stale 20s floor → **every strong survivor was skipped in the
   solo gate** (`soloFamilyEmitted=False`) → no family → no trace.

2. **A widened band floor alone can't catch the degenerate static class.** The
   FRESH10 armed family's failure was a static y@~1.0 (span 4.0 units) whose
   20–60s band fit the old floor's "degenerate" definition because the sweep was
   only 60s. At a 60s floor on a 180s sweep, a 40s band is only 22% — band width
   is no longer the degenerate detector; **movement span** is.

## Fix (evidence-first; both gates agree)

- **Band floor re-derived to 1/3 of the ±90 sweep: 20s → 60s** in both
  `od-048-monitor-correlate-session.ps1` (`AutoTraceMaxMemberBandSeconds`) and
  `x64dbg-write-trace.ps1` (`MaxMemberBandSeconds`). Same ratio as the original
  design, at the sweep that actually produced the bands.
- **New span floor**: `AutoTraceMinMemberSpan` (od-048) / `MinMemberSpan`
  (write-trace), default 10.0 game units, 0 disables. A member whose known span
  is below the floor is a value that never moves — its score on a low-information
  axis is cheap and it must not win a trace window. Unknown span passes on the
  write-trace side (server-family members predate the field); the solo path
  refuses unknown span fail-closed (od-048 has the result's span in memory).
- **Solo members now serialize `span`** so the write-trace's floor can vet them
  (od-048 member synth + report serializer).
- **wtArgs passes `MinMemberSpan` through** so od-048 and the write-trace can
  never disagree on the same report.

## Validation (all green)

| Check | Result |
|---|---|
| Parse pwsh 7 + PS 5.1, both scripts | clean |
| PSSA hygiene gate | PASSED (66 pre-existing advisories, baseline) |
| ASCII | all clean |
| Write-trace probe (tmpwotb-e2e/fresh22-bandfloor-test.ps1) | FRESH21-class family **admitted + armed** (`family_members_armed=1`); FRESH10 degenerate (span 4.0) **refused** (exit 2) |
| Solo simulation on the REAL FRESH21 result | `SOLO_EMITTED 0x23BD2C50 z band=31.5s span=275.2 score=0.92` — the survivor FRESH21 would now arm |

Note: an all-edge family is still admitted by the write-trace's tier-3
direct-investigation fallback *by design* (documented); od-048 gates edge-alignment
upstream (solo path only emits non-edge survivors, the family gate refuses
all-edge families), so no all-edge family can reach the trace in the autoloop flow.

## Budget accounting

FRESH20 + FRESH21 both returned **strong verdicts** (not no-evidence), so the
archive trigger ("2 valid sessions with no strong survivor") did NOT fire. The
remaining work is arming + tracing a survivor — the M1.5→M2 handoff, which needs
one more live round. FRESH22 is that round: with the re-derived floors, the
survivor 0x23BD2C50-class (span 275, non-edge, shift ~0) passes every gate and the
auto-trace should produce the first `odwt-*.bin` hit report before the battle ends.

## FRESH22 live checklist

1. Pre-flight: no stray game/host/debugger; replay present; fresh host publish.
2. Launch `od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly`.
3. Watch: staging tick ~5s → smoke green → `family_solo_emitted axis=z score=0.92`
   (or higher) → `auto_write_trace` invoked → **`odwt-*.bin` hit report** with the
   writer's RIP/RVA, base register, displacement, and nearby-object dump.
4. Failure triage: if the trace exits `STOP_gate=Denied` again (battle end), the
   adaptive window already budgets 15s of slack — check the battle-tail estimate.
