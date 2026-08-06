# Handoff — FRESH19 live outcome + crash root-cause + fixes (2026-08-06)

**Campaign:** FRESH19 live (`od-049-autoloop -AttachSmokeOnFirstRound -StageViewpointOnly`),
HEAD before this handoff: `4abb42c`.

## What the live run showed

| Step | Result |
|---|---|
| Attempt 1 launch | resize/click/gate/marker/staging ✅ (tick 5.4s) |
| Attempt 1 smoke | ❌ `pause=True resume=False` (known x64dbg/WOW64 permanent freeze ~1/3) |
| **Campaign retry (self-heal)** | ✅ auto-relaunched (attempt 2), fresh game+host |
| Attempt 2 smoke | ✅ `resume=True`, compensation 6s |
| Attempt 2 sampling | ✅ 70 rounds, **zero hard 401s** (the FRESH19 retry fix held; 0 `api_failed` in the whole log) |
| Attempt 2 staging | ⚠️ degraded — `tick_est=91s` (was 5s), `staged=806` (was 3000) |
| **Attempt 2 correlate** | 💥 **CRASH** — `The property 'Count' cannot be found on this object` — campaign died, no verdict, no report |

## Bug 1 (crash, root-caused + fixed): zero-viewpoint `$null.Count` under StrictMode

**Symptom:** od-048 died right after the correlate with a PropertyNotFoundException
on 'Count', killing the whole pwsh (the autoloop's `&` call propagated the
terminating error). Reproduced twice offline (3000-address and 3000-address
re-runs) with the identical message.

**Root cause:** `$results = Select-ViewpointResults -Results $results …` — the
function returns `@($Results | Where-Object …)`, but PowerShell **unwraps the
function's pipeline output on return**. Zero matches → `$null`; `$null.Count`
throws PropertyNotFoundException under StrictMode (single match unwraps to a
scalar whose unified `.Count` happens to work — only the zero case crashes).
The sharper 90s sweep can produce **zero** viewpoint matches (every address
scored as an alternate-entity decoy), which FRESH18's 173-result array never
exercised.

**Fix (od-048):** caller-side `@(Select-ViewpointResults …)` re-collects the
pipeline into a real array (empty or not).

**Proof:** unit probe reproduces the exact error without the fix
(`ZERO_NOFIX_CRASHES: The property 'Count' cannot be found…`) and passes with
it (`ZERO_FIXED_COUNT=0`, `ONE_FIXED_COUNT=1`); full 8-round campaign now
completes (`NO_CRASH`, report written, `verdict=no-evidence`).

## Bug 2 (degradation, fixed): stale marker on campaign relaunch

**Symptom:** attempt 2 anchored at attempt 1's marker
(`marker_found(relaunch) … utc=15:09:59`, same old log file) → staging computed
`tick_est=91s` (true ~5s) → the scan missed the field and staged only 806
candidates.

**Root cause:** the relaunch marker search iterated the STALE `$logs` list
(enumerated before attempt 1) and accepted the previous attempt's marker — the
new game's log + marker hadn't been written yet when the search ran.

**Fix (autoloop):** on relaunch, re-enumerate logs and poll up to ~40s for a
marker **newer than the relaunch start** (with a 5s slack); stale markers are
rejected; fail-closed `FAILED_no_marker(relaunch)` if none appears.

## FRESH19 verdict for the roadmap

- The launch stack, the 401-retry, and the campaign self-heal all worked as
  designed (attempt 1's smoke freeze was auto-recovered).
- The crash bug is the biggest find: it would have killed EVERY sharp-sweep
  run that produces zero viewpoint matches — now it fails gracefully with
  `verdict=no-evidence`.
- Remaining live-only gap: the smoke still fails ~1/3 of the time (known
  x64dbg/WOW64 resume limitation) — the retry loop absorbs it at ~4 min/session.

## Files changed

- `scripts/od-048-monitor-correlate-session.ps1` — caller-side `@()` on
  `Select-ViewpointResults` (crash fix)
- `tmpwotb-e2e/od-049-autoloop.ps1` — fresh-marker polling on relaunch
- `tmpwotb-e2e/repro-crash-line.ps1` — new: crash repro wrapper (short campaign,
  dumps ScriptLineNumber + stack)

**Validated:** parse pwsh 7.6 + PS 5.1 ✅, PSSA at baseline (0 findings in
edited files) ✅, ASCII ✅, 8-round campaign completes end-to-end ✅. Leftover
looping game killed (`GAME_STOPPED`); host left up per runbook.
