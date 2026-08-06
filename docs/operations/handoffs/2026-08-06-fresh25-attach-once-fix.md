# Handoff: FRESH25 — attach-once fix (second-attach freeze was the STOP_gate=Denied root cause)

**Date:** 2026-08-06 (UTC-4)
**Status:** FRESH25 run analyzed; attach-once fix implemented + validated offline; FRESH26 will verify live.

## FRESH25 outcome

The value-liveness question (FRESH24's discriminator: `window_values_changed=true|false`) did **not** get
answered — the trace window never opened. M1 delivered its usual strong verdict, but the auto-trace
failed with `STOP_gate=Denied` (exit 5) **before** the value-liveness snapshot could run.

Timeline (all times UTC):

| Time | Event |
|------|-------|
| 21:29:42 | Battle started (medvedkovo), 271.4s duration → ends ~21:34:13 |
| 21:30:38–54 | Attach-smoke round 2: attach → pause → bpm → detach → **resume verified** (smoke green) |
| 21:32:49 | M1 done: `verdict=evidence-strong`, `family_solo_emitted axis=z members=4 score=0.92 span=275.2 band=31.5s` |
| ~21:32:50 | Auto-trace invoked; **pre-arm launched a SECOND x32dbg (pid 48272)** and attached |
| **21:32:53** | **Host monitor: `Denied`, reason `evidence.monitor_unhealthy`** (observedAtUtc) |
| ~21:33:35 | scriptload+scriptrun injected |
| ~21:33:36 | Trace first gate poll → `STOP_gate=Denied` → `hits=0` → exit 5 |
| 21:33:44 | Sparse trace report written (`od-048-autotrace-20260806-173249.json`) |

## Root cause (evidence-backed)

The trace performed a **second x64dbg attach** mid-battle (the smoke had already attached and
**detached**). That second attach triggered the known WOW64 attach-freeze class — the operator saw
the game window "not responding", consistent with every prior freeze report. The host's lifecycle
monitor (polling every 500ms) observed the frozen game, hit a terminal window-observation/failure
path, and **denied the evidence terminally** at 21:32:53. The trace's first gate poll then read
`Denied` and aborted before the window opened.

Notable: the precheck passed (`gate=OfflineReplayVerified`) because it ran ~1-2s *before* the
denial; the denial landed during the attach. This was **not** the FRESH20 battle-ended case (battle
still had ~80s to run), and not a science failure — the value-liveness discriminator never got a
window because the mechanism killed it first.

This is the same class flagged since FRESH20 ("we attach the debugger twice; keeping one debugger
attached from smoke → trace shrinks the wrapper to ~0") — finally fixed.

## The fix: attach-once (FRESH26)

One debugger, attached once at the **safe** point (smoke, battle-start, pause/resume verified,
relaunchable), kept attached through the campaign, and **reused** by the trace — no second attach,
no freeze, no denial, and the wrapper latency disappears.

Changes:

1. **`scripts/x64dbg-write-trace.ps1`**
   - `Invoke-AttachSmoke` gains `-KeepAttached`: after the bpm probe, instead of detaching, the
     smoke **scriptrun-resumes** the game (`Resume-AttachedAndKeep`: writes a minimal resume
     script, scriptload+scriptrun, polls CPU up to 30s — the proven resume path; command-bar `run`
     never resumes this WOW64 combo, scriptrun does) and **leaves the debugger attached**.
     Report gains `keptAttached=true|false` (fail-closed: only true when resume verified; otherwise
     falls through to the old detach path).
   - Trace window gains `-ReuseAttached`: when set, the trace **skips its own `attach`** command
     (the debugger already owns the process) and goes straight to `pause` + scriptload + scriptrun.
     If the smoke's debugger is gone by trace time, it degrades to a fresh pre-arm + attach (safe —
     the freeze only bites when a *second* debugger grabs an already-attached process).
   - DRYRUN text updated for both modes.

2. **`scripts/od-048-monitor-correlate-session.ps1`**
   - Smoke invocation passes `KeepAttached=$true`.
   - Smoke report block records `keptAttached`; the auto-trace `wtArgs` passes
     `ReuseAttached=$smokeKeptAttached` so the two sides can never disagree about debugger
     ownership.

## Validation (offline)

- Parse: PS 5.1 + PS 7 both OK for both scripts.
- PSSA gate: passed (68 baseline warnings, no new findings on the edited files).
- ASCII: both files clean.
- DryRun: smoke `-KeepAttached` prints the keep-attached plan; trace `-ReuseAttached -DryRun`
  parses the real FRESH25 family, arms 4 members, emits the script, exits OK.

## FRESH26 expected lines

```
attach_smoke keptAttached=True pid=0x... (trace will reuse this debugger)
reused_attached_debugger pid=0x... (no second attach)
family_liveness_ok armed=4
window_cpu_delta_ms=... liveness=running
window_values_changed=true|false   ← the FRESH24 discriminator finally gets its window
family_verdict=family-hit hit_members=N   ← first odwt-*.bin hit report, or
family_verdict=family-no-hit              ← decisive real no-write on a LIVE moving world
```

## Budget note

FRESH20/21/22/23/24/25 have all returned strong verdicts (M1 stable); the archive trigger (2
sessions without a strong survivor) never fired. The remaining gate is arming + tracing the
survivor — FRESH26 with attach-once is the round where the first `odwt-*.bin` should land.
