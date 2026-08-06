# OD-RECOVERY-051: FRESH29 isolates the write-BP capture bug; file-based channel added

Date: 2026-08-06 (UTC evening)
Status: fix committed; live re-validation = FRESH30

## The campaign's decisive result

FRESH29 was the first run where the ENTIRE automated stack landed together
(FRESH27b smoke-at-last-round + FRESH26b dispatch + wire fixes), and it
proved the science is real while isolating the last mechanism bug:

- **attach-once worked**: `reused_attached_debugger pid=0xDAE4` — the smoke
  kept ONE debugger (round 49, `keptAttached=True`) and the trace reused it.
  The FRESH25 `STOP_gate=Denied` chain (second-attach freeze) is dead.
- **wire fix worked**: `read_values ok read=4 mapped=4` (both reads) — the
  value-liveness discriminator finally returns real data.
- **consensus class restored**: `family_solo_emitted axis=x members=4
  score=1 span=107.6 band=1.0s` — 20 strong survivors, all x@1.000,
  shift=52, band [52,53] (1-second ambiguity!), vs FRESH27's degraded
  y@0.70 (the FRESH27b smoke-placement fix confirmed).
- **`values_changed=true max_delta=20.94`** — the armed x-field MOVED 20.94
  units during the 25s window. The world was advancing; the field is live.
- **`hits=0`** — but zero write-BP evidence captured.

## The smoking gun: the game paused mid-window

`window_cpu_delta_ms=4375` — only 4.4s of game CPU in a 25s window, vs
FRESH24/26's ~25s (a running game burns ~1 core). The game RAN, moved the
tank 20.94 units, then PAUSED — the exact signature of a memory write-BP
hit breaking the debuggee (break-on-write). The BP **fired**; the evidence
was then lost in harvest.

## Root cause: the capture channels were never reliable

The trace script used two capture mechanisms, both known-flaky:

1. **UIA Log-tab read** (`Read-X64DbgLog`) — the script's own FRESH9-era
   comment calls it "LOSSY (returned 0 lines in clean-rig runs)".
2. **`savedata`** per-address files — the FRESH9 campaign documented `{rip}`
   inside the filename as "NOT reliable across builds"; static filenames
   were the workaround but remained fragile.

The FRESH9 probe (`tmpwotb-e2e/probe-membp-final.ps1`) that PROVED memory
BPs fire used a third, reliable channel the trace never adopted:
**`setlogfile` + `SetBreakpointLogFile <addr>, <file>`** — writing the
`ODWT_HIT addr=0x… rip={rip}` lines to a real file on disk.

## The fix (FRESH29b)

1. **Script generation** now emits `setlogfile "<engine log>"` and
   `SetBreakpointLogFile <addr>, "<bp log>"` per armed address (kept
   alongside `SetMemoryBreakpointLog` + `savedata` as backups).
2. **Harvest** now prefers the file channels: per-BP log file → engine log
   file → lossy UIA tab (last resort), with the ACTUAL source reported
   (`log_harvest_hits=N source=bp-log-file|engine-log-file|uia-tab`).
3. **Shared helper** `Add-OdwtHitLines` (regex parse + dedup) used by both
   the poll loop and the post-window harvest so the three copies can't
   drift. Unit-tested offline: 3 lines → 2 unique hits, dup suppressed.
4. **Stale-file hygiene**: the HitsDir is a fixed shared path — stale
   `odwt-*.bin` / `od-wt-engine.log` / `od-wt-bp.log` from a previous run
   are deleted before each trace so a prior session's hits can never be
   harvested as false positives.

All gates green: PS 5.1 + 7 parse, PSSA hygiene passed, ASCII clean,
DryRun shows the new script shape (`setlogfile … SetBreakpointLogFile …`).

## FRESH30 decision tree

With the file channel live, the trace window finally has a reliable witness:

- `log_harvest_hits>0` → **first `odwt-*.bin` writer report** (RIP/RVA,
  registers, base+displacement) — the M1.5 goal.
- `values_changed=true` but still `hits=0` with file channel armed → the
  address is genuinely never written per-frame while its value changes
  (computed/one-time-copied field) → science question deepens.
- `values_changed=false` → world not advancing in the window (paused/roster)
  → window placement fix.

## Artifacts

- Result: `.data/od-049-fresh29-result.json` (20 strong x@1.000 survivors)
- Interrupted-run evidence: `.data/od-049-fresh28-interrupted-result.json`
  (also x@1.000, shift 54.5 — the same consensus class, twice)
- Trace report: `.data/od-048-autotrace-20260806-185826.json` (+ `.family.json`)
- Smoke report: `.data/od-048-attach-smoke-20260806-185756.json`
- Run log: `.data/od-049-fresh29.log`
