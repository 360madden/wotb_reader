# OD-RECOVERY-052: x64dbg write-BP capture route is conclusively DEAD in this environment

**Date:** 2026-08-06 (battles 00:26:45Z / 00:34:27Z, FRESH32 + FRESH33)
**Milestone:** M2 write-trace capture. Commits `e323827` (FRESH31 fixes) + uncommitted diagnostic work.

## Bottom line

After FRESH15→FRESH33 (10+ live sessions, 6+ probe campaigns), the x64dbg write-breakpoint
capture route is **conclusively non-functional in this environment**, and today's evidence
identifies WHY at the mechanism level. Every zero-hit verdict from FRESH29→FRESH33 has the
same root cause: **the write BPs never armed and never fired.** Continuing to burn live
sessions on x64dbg violates the campaign's own CAP/evidence discipline. Recommendation:
stop x64dbg live runs; preserve the (still valid) M1 address-level evidence; rebuild the
capture in C# (guard-page write interception) as a new milestone.

## The evidence chain (all reproduced in this handoff's probes)

### 1. FRESH32/33 reproduced FRESH29/31 exactly
Both runs: clean launch → correct anchor → attendance-gate staging → 49 clean rounds →
smoke kept attached → evidence-strong x-consensus (score=1, span~108, band=1.5s) → trace
invoked, `reused_attached_debugger`, `values_changed=true` (14.8–17.8 units), but
`hits=0` and **no capture files of any kind** (`$TEMP\od-wt-hits\` empty except a
PowerShell-written raw log).

### 2. UIA log reads throughout the campaign were reading UI chrome, not log text
A full UIA tree dump (`probe-uia-tree.ps1`) proved the log view's text is exposed as
**DataItem elements' Value pattern**, NOT `Name` properties. Every prior
`Read-X64DbgLog` (Name-only filter) returned tab/menu chrome ("Pause", "Script DLL",
"Breakpoints", "Script"). Consequences:
- The smoke's `bpmArmed=yes` and every "ODWT_ARMED" verification were reading noise.
- The FRESH-era "LOSSY log-tab read" comment understated the problem: the read was
  never pointed at the log text at all.
- The one TRUE error signal is a Text element: `LogStatusLabel` = "Error executing command!".

### 3. In-script `bpm` errors in this build
The tree dump shows the trace's script executing:
```
log "ODWT_TREE_MARKER count=1"   ->  output ODWT_TREE_MARKER count=1   (works)
bpm 0x..., 1, w                  ->  LogStatusLabel = Error executing command!
```
The memory-BP command itself fails when run inside a script. With the BP never armed,
every capture channel (setlogfile engine log, SetBreakpointLogFile bp log,
SetMemoryBreakpointCommand savedata) has nothing to fire, so no files ever materialize.

### 4. bpm AND bph never fire, even via the command bar, even on a constantly-writing target
`probe-bp-behavioral.ps1` against the synthetic counter target (writes to a known
address in a tight loop, progress file proves liveness):
- after attach: `state=Running`, progress advancing
- armed `bpm addr, 1, w` / `bpm addr, w` / `bph addr, w` / `bph addr, 1, w`:
  state stays **Running**, progress keeps advancing — **zero breaks on any variant**.

### 5. The counter target has 6 threads
`wt-counter-target.exe` runs 6 threads; the write almost certainly happens on a worker
thread. x64dbg's hardware BPs (DR0–DR3) are set on the main thread, so `bph` cannot
catch worker-thread writes — this explains the bph failure and is almost certainly why
the live game (many threads; DAVA position updates on a non-main thread) showed the
same signature. (It does NOT explain bpm, which is process-wide — bpm's own command
error in §3 is the bpm explanation.)

### 6. Earlier "proofs" of BP capture are unreliable
- `probe-rip-decisive.ps1` (Aug 5): its hits dir contains only the script file — zero
  hit files.
- `probe-membp-final.ps1`: sentinels S1–S5 landed (script-time savedata works) but no
  BP-time log file; the single `static-hit.bin` is ambiguous and unreproducible.
- The "hits landed 3/3" recollection refers to the detach-resume CPU campaign, not BP
  capture.

## What still works (the preserved assets)

- M1 science is solid and reproducible: staging, viewpoint-only sampling, correlate,
  evidence-strong x-consensus verdicts, family emission.
- `savedata` at SCRIPT time works (sentinels).
- The value-liveness + CPU-liveness discriminators correctly report the window.
- The fail-closed gates (FRESH30/31: battle-end watcher, pre-trace skip, bounded
  anchor) are proven and keep every session from burning.

## Recommended pivot (next milestone, buildable offline)

Replace the x64dbg write-BP capture with a **C#-native guard-page write interceptor**:
- `VirtualProtect(PAGE_GUARD)` on the armed page, then act as the debugger
  (`DebugActiveProcess`) or use `SetUnhandledExceptionFilter`-style handling to catch
  `STATUS_GUARD_PAGE_VIOLATION`, read the faulting thread's `GetThreadContext` (RIP =
  write site), single-step, re-arm, record.
- Fits the existing architecture: UltimateScanner/GameIntegration already hold the
  sanctioned Win32-interop allowlist; no new external tools; buildable + testable
  offline against a synthetic target before any live session.
- Interim position (until that lands): declare the address-level evidence achieved and
  stop live runs — no more x64dbg sessions.

## Gates

All new probes parse under PS 7; trace script edits parse PS 5.1 + 7, ASCII clean.

## Artifacts

- Probes: `tmpwotb-e2e/probe-trace-script-capture.ps1`, `probe-uia-tree.ps1`,
  `probe-bpm-variants.ps1`, `probe-bp-single.ps1`, `probe-bp-behavioral.ps1`
- Launchers: `launch-fresh32.ps1`, `launch-fresh33.ps1`
- FRESH32b raw-log instrumentation in `scripts/x64dbg-write-trace.ps1`
