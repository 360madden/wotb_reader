# Handoff: FRESH9 chunked investigation — write-trace root causes fixed (2026-08-05)

## Why this handoff exists

FRESH9 (the live M1→M2 run) completed every mechanical step of the
auto-write-trace (gate → monitor → correlate → auto-trace invocation →
window-wait → scriptload/scriptrun) but produced **zero hits** for the
armed x/z pair. The follow-up was run as a **chunked test** (per the
user's request) so each unknown was isolated and validated offline
before any live FRESH10: each chunk had its own pass/fail, and a session
crash could never lose more than one chunk.

## Chunk results

| # | Chunk | Result |
|---|-------|--------|
| 1 | Memory-BP flow on the counter rig | Mechanical pieces proven (hex attach, pause, script exec); BP capture **proven with the final shape** |
| 2 | UIA command channel | **Proven** (ValuePattern + PostMessage ENTER, markers 3/3, log tab readable) |
| 3 | Product-script fixes | Applied (below); PSSA gate passed, parse OK both engines, ASCII clean |
| 4 | Offline integration | **probe-integration.ps1: proof file `odwt-0x<addr>.bin` (4 bytes) captured via the product's exact step-5 flow** |
| 5 | Static gate | PSSA PASSED (4 advisory warnings pre-existing), parse OK, ASCII clean |
| 6 | FRESH10 live | **Not yet run** — needs user approval + hands-off window |

## Root causes (all source-verified against x64dbg development branch)

1. **Decimal-pid attach (the FRESH9 zero-hit killer).** x64dbg parses
   *every integer literal as hex*, so `attach 42284` targets pid `0x42284`
   (a nonexistent process). The old pre-arm launched `x32dbg.exe -p <decimal>`
   — provably broken. Fix: `attach 0x<hexpid>` via the command bar.
2. **SendKeys broken by IME.** Literal `strref 0, 0, 1` landed in the
   command bar instead of the intended command. Fix: UIA ValuePattern set +
   focus + PostMessage ENTER (immune to focus/IME).
3. **`bph` arms only the active thread.** `cmd-breakpoint-control.cpp`:
   `//TODO: hwbp in multiple threads TEST`. After attach the active thread is
   the break-in/loader thread, not the writing thread. Fix: memory
   breakpoints `bpm <addr>, 1, w` (guard page, fires on any thread).
4. **`bpmwlog` is not a script command** (aborts scripts). Use
   `SetMemoryBreakpointLog` (validated) or nothing.
5. **fast-resume + condition-0 skips log+command.** `debugger.cpp`
   `cbGenericBreakpoint`: `if(bp.fastResume && breakCondition == 0) return;`.
   Empirically `SetMemoryBreakpointCondition 0` suppressed the command
   entirely in this build (three clean-rig runs, zero captures). Fix: no
   condition, no fast-resume — default breakCondition=1 runs log+command
   then breaks (one capture per address, then the debuggee pauses; the
   driver sends `detach` afterward to release it).

## Probe matrix (clean rig: static-field counter address, no leaked processes)

| Shape | Capture | Debuggee |
|-------|---------|----------|
| bpm + static savedata (no condition) | **✅ proof file** | freezes after 1st hit (released via detach) |
| + `SetMemoryBreakpointCondition 0` | ❌ | frozen |
| + `; run` self-resume suffix | ❌ | frozen |
| `{rip}`-named savedata + `rip 64` | ❌ | frozen (command error → force break) |
| `bphwlog` (old product line) | script aborts | — |

Earlier zero-hit runs on the pre-fix rig were ALSO confounded by a leaked
duplicate counter process from a prior probe still writing the addr file —
probes may have armed breakpoints at a stale process's address. Clean-rig
runs (probe-integration.ps1) capture reliably.

## Fixes applied (committed in this handoff)

- **scripts/pre-arm-debugger.ps1** — launches the x32dbg window ONLY
  (no `-p <decimal>` attach, which was broken and would conflict with the
  write-trace's own attach). Marker gains an `attachNote`.
- **scripts/x64dbg-write-trace.ps1**
  - Script generation: `bph`/`bphwlog`/`SetHardwareBreakpoint*`/fast-resume
    → `bpm <addr>, 1, w` + `SetMemoryBreakpointLog` + static per-address
    savedata (`savedata <hits>\odwt-0x<addr>.bin, <addr>, 4`).
  - Injection: SendKeys → UIA channel (new `Find-X64DbgCommandBar`,
    `Send-X64DbgCommand`, `Read-X64DbgLog`; new `WtX64Ui` PostMessage C#).
  - Step 5: self-attach `attach 0x<hex>` + `pause` before scriptload/scriptrun.
  - Step 6b: `detach` release after the window (the first hit pauses the game).
  - Step 7: log-tab harvest of `ODWT_HIT addr=... rip={rip}` lines
    (stringformatinline) + static proof-file scan; poll loop live-counts
    proof files.
- **tmpwotb-e2e/probe-*.ps1** — the investigation record: `probe-control.ps1`
  (attach-overhead + `bl` dump), `probe-rip-decisive.ps1` (the {rip}
  matrix), `probe-integration.ps1` (product step-5 flow, THE capture
  proof), `wt-counter-target.cs` (static-field counter; the stackalloc
  version confounded memory-BP behavior).

## Known limitation (honest)

- One capture per armed address per trace (the first hit pauses the game;
  condition-0/self-resume paths are dead ends in this x32dbg build).
  That is still the **first real write-evidence ever produced** — a proof
  file per armed family member.
- The `ODWT_HIT` log harvest returned 0 in all clean-rig runs (UIA log-tab
  read limitation), so the write-site RIP may be missing from FRESH10's
  report; the proof files are the primary evidence. Follow-up: drive the
  engine log to a file (`setlogfile` did not take via the command bar) or
  read the Log tab with the tab active from the start.

## FRESH10 checklist (the live run)

1. Fresh publish of the Host (stale-publish blocker rule).
2. `od-048-monitor-correlate-session.ps1 -MaxReadRounds 70` → verdict +
   `-AutoWriteTraceOnVerdict` fires in-process.
3. Expect in the log: `x64dbg_pid=...`, `attached pid=0x...`,
   `injected scriptload+scriptrun`, `released_detach`,
   `hits=N` with N ≥ armed count, `family_verdict=family-hit`,
   and `odwt-0x<addr>.bin` proof files + the `.family.json` report.
4. ~5 min hands-off window; x32dbg window flashes during the trace.
