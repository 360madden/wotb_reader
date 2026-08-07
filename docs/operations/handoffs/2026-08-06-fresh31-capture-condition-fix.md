# OD-RECOVERY-051: FRESH31 - fail-closed path proven, capture gap isolated to the missing condition line

**Date:** 2026-08-06 (battle 00:16:51Z–00:21:22Z, replay `a9aed0467d7843efb06bb3319bb52ded.wotbreplay`)
**Milestone:** M1→M2 pipeline, `929a66a` + FRESH31 fixes (uncommitted at write time: anchor-date + condition-0)

## What FRESH31 was supposed to prove

With the FRESH30 fail-closed fixes in tree (battle-end log watcher + pre-trace skip), the
outcome must be EITHER a real trace inside the live window OR a clean `battle-ended-skip`
verdict (exit 0) — **never** a `STOP_gate=Denied` burn (exit 5).

## Result: the fail-closed guarantee held; the capture gap was isolated to one missing line

| Stage | Result |
|---|---|
| Launch | resize 640x360, orange click (2154px, centroid 267,196), gate `OfflineReplayVerified`, watch exit 0 |
| Anchor | **attempt 1 burned**: `staging_budget_exhausted staged=0` with `elapsed_s=86408.2` — see root cause below |
| Anchor (fixed) | attempt 2: `marker_found -> 2026-08-07T00:16:51Z`, staging `elapsed_s=55.8` |
| Staging | `staging_match_begin_gate elapsed_s=8.7 waiting_s=46.3 min=55s` (attendance pause handled) |
| Rounds | 49 clean rounds, 3000-series, 120000+ samples — zero under the debugger |
| Smoke | round 49, `kept_attached=True pid=0x4278`, pause_compensation 7s |
| Correlate | `verdict=evidence-strong strong_survivors=18 families=1`, x-consensus `score=1 span=107.7 band=1.5s` |
| Trace | `reused_attached_debugger` (attach-once), 4 members armed, **`values_changed=true max_delta=22.46`**, `window_cpu_delta_ms=4906` (paused mid-window), **`log_harvest_hits=0 source=uia-tab`** |
| Exit | **`m1_exit=0`** — no STOP_gate=Denied, no battle-ended-skip (battle was correctly still live; the trace ran inside the window) |

The fail-closed path is proven: even a full-race run cannot burn a session anymore. The
remaining gap is pure capture mechanism.

## Root cause A: 24h-stale anchor (attempt 1) — game-local filename date vs OS timezone

`Get-LogAnchorDateUtc` converted the log FILENAME date (`blitz-logs_20260806191246.txt` —
written in the GAME's local time, offset `-5` per the log line `19:13:01 -5`) to UTC using
the **OS timezone** (UTC-4). The 1-hour mismatch crossed the UTC midnight boundary:
filename Aug 6 19:12 local (game, -5) = Aug 7 00:12Z, but interpreted as OS-local (-4) =
Aug 6 23:12Z → date Aug 6. The marker `00:13:01Z` (real UTC) became **Aug 6 00:13:01Z**,
24h stale → `elapsed_s=86408.2` → `staging_budget_exhausted` → `FAILED_staging_too_small`
→ exit 2 (session burned at attempt 1). Same failure class as the FRESH10 local-date bug,
now crossing the UTC-midnight boundary.

**Fix (autoloop):** anchor date now comes from the log file's **LastWriteTime UTC date**
(OS-correct AND current — the game writes the file continuously); the filename date is only
a fallback. The future-rollback is bounded to >60s (a sub-minute extraction race is clock
skew, not a yesterday marker), and the primary path now rejects markers older than 120s
(fail-closed `FAILED_no_marker` on a failed click instead of anchoring a previous session).
Verified against the real log: anchor = `2026-08-07T00:13:01Z` (elapsed 185s), live run
confirms.

## Root cause B: zero capture with the file channel — missing `SetMemoryBreakpointCondition addr, 0`

FRESH31 reproduced the FRESH29 signature exactly: game paused mid-window
(`window_cpu_delta_ms=4906` in 25s; `values_changed=true max_delta=22.46` — the world
advanced) yet **zero evidence on every channel** — the FRESH29b file channel
(`setlogfile` + `SetBreakpointLogFile`) never even created its log files
(`$TEMP/od-wt-hits/` empty, only the stale Aug-4 dir).

Diffing the FRESH9-era probe that PROVED capture (`probe-membp-final.ps1`, sentinels
S1–S5 all landed + `static-hit.bin` produced) against the trace's generated script found
**one missing line per armed address**: `SetMemoryBreakpointCondition {addr}, 0`. In x64dbg
memory-BP semantics, condition `0` = break always; without an explicit condition the BP
can swallow the write (single-step past) without firing the log/command callbacks. The
probe's engine log also never existed — the **proven** channel is savedata + condition-0,
not the engine log (the FRESH29b comment over-credited the engine-log channel).

**Fix (x64dbg-write-trace.ps1):** per armed address, after `SetBreakpointLogFile`, emit
`SetMemoryBreakpointCondition {addr}, 0` — the exact proven recipe. Generated-script
dry-run against the real FRESH31 family file confirms all 4 addresses now arm with the
condition line (bpm → log → logfile → **condition 0** → savedata).

## Gates

PS 5.1 + 7 parse OK, ASCII clean, PSSA no new findings (only pre-existing advisory
PSAvoidUsingEmptyCatchBlock at unrelated lines).

## Next

FRESH32: same launcher flags, expects the first real `ODWT_HIT addr=... rip=...` line in
`od-wt-bp.log` / first `odwt-*.bin` savedata hit file on a live write to the armed x-family
— the RIP is the write-site evidence that names the position-object writer.
