# OD-RECOVERY-051: FRESH30 STOP_gate=Denied — battle-end timing model post-mortem

Date: 2026-08-06 (immutable; correct with amendments only)
Status: root cause proven from artifacts; fixes implemented offline (FRESH31 pre-flight)

## Verdict

FRESH30 attempt 2 was **clean through M1** — 49 rounds, 144000 samples, smoke at
round 49 kept the debugger attached (`kept_attached=True pid=0x7ED4`),
x@1.000 consensus emitted (34 results, span 107.7, band 1.5s) — then the
auto-write-trace was **denied at its first gate poll** (`STOP_gate=Denied`,
exit 5) with zero window time. This is the **same denial family as FRESH25**,
but the root cause this time is not the second attach: it is a **battle-end
timing model error** that scheduled the smoke + correlate + trace AFTER the
replay battle was already over.

## Evidence chain (all UTC, blitz-logs_20260806181632.txt = attempt 2)

| Event | Wall clock | Source |
|---|---|---|
| Start replay event (anchor) | 23:16:47 | autoloop marker_found |
| LoadGameScene begins | 23:16:51 | blitz log |
| LoadGameScene ends | 23:16:52 | blitz log |
| staging attempt (elapsed 55.3s) | ~23:17:42 | od-048 log |
| **Battle actually ends (onLeaveWorld)** | **23:18:38–23:19:05** | blitz log |
| Last log line (processInput warning) | 23:19:49 | blitz log |
| Smoke at round 49 (gate elapsed 164.3s) | ~23:19:31 | od-048 log |
| auto_write_trace INVOKING | ~23:20:01 | od-048 log (report invokedUtc 23:20:15.77) |
| evidence.monitor_unhealthy | 23:20:14 | host state query |
| STOP_gate=Denied, exit 5 | ~23:20:15 | trace log |

**The smoke fired 26s after the battle ended; the trace invoked 70s after the
battle ended and ~26s after the game's log went silent.** The host's lifecycle
monitor (GameSessionCoordinator) saw the replay-stop / log-silence and revoked
the evidence; the trace's first gate poll read `Denied`.

## Root cause

od-048 computes the battle-end budget from the **decoded trajectory duration**:

```powershell
$battleStartUtc = $anchorUtc + AttendanceLatencySeconds (50)
$battleEndUtc   = $battleStartUtc + durationTicks/1e7   # 271.4s -> 23:22:33
```

The **actual played battle was only ~88s of live world**
(23:17:37 match-begin -> 23:19:05 onLeaveWorld). The decoded 271.4s is the
full battle timeline, but the launched replay (rolled / accelerated playback)
reaches the end ~3.5 minutes earlier in wall time. Every downstream decision
inherited the wrong end:

1. `monitorExitUtc` (round-loop battle-ended break) never fired in time —
   rounds kept sampling a dead world past 23:19:05.
2. The smoke fired at round 49 (23:19:31) — after the battle was over
   (FRESH27b's last-round placement assumed 50 rounds fit inside the battle;
   they do not when the battle is ~88s of wall).
3. The correlate ran on samples dominated by the live portion (rounds 1–40),
   so it still emitted a strong x consensus — the science is not broken.
4. The trace invoked into a dead world with a silent log → monitor revoked →
   first gate poll `Denied` → exit 5, window burned for nothing.

FRESH29's trace (window 22:59:02Z, battle ended 22:57:30Z) survived only
because the game's window/process observation stayed healthy long enough and
the log had not yet tripped the monitor — a lucky race, not a working design.

## Fixes implemented (offline, fail-closed)

1. **od-048 battle-end watcher**: tail the newest blitz log for the
   battle-end signature (`OnLeaveBattle` / replay-stop / post-battle silence)
   and use it to stop the round loop with `battle-ended` instead of trusting
   the decoded-duration model.
2. **od-048 pre-trace gate recheck**: before invoking the auto-trace, verify
   the gate is still `OfflineReplayVerified` AND the blitz log shows no
   battle-end; otherwise emit a clean `battle-ended-skip` verdict (exit 0)
   instead of burning the window on a guaranteed denial.

## What FRESH30 proved

- Attach-once works: `reused_attached_debugger pid=0x7ED4 (no second attach)`.
- Wire fix works: `read_values ok read=4 mapped=4`.
- The correlate is real: x@1.000, 34 survivors, tight 1.5s band from live
  samples.
- The remaining blocker is purely **scheduling**: the trace must run INSIDE
  the live battle window, and the pipeline must know the real battle end.

## Next step

FRESH31: verify the battle-end watcher + pre-trace recheck offline
(harness/PSSA/DryRun), then one live launch with `-AttachSmokeOnFirstRound
-StageViewpointOnly` watching for `monitor_stop battle-ended` (or a trace that
lands inside the live window) instead of `STOP_gate=Denied`.
