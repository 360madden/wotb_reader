# OD-RECOVERY-050: FRESH26 attach-once run — dispatch bug found, value-read wire bug found

Date: 2026-08-06 (UTC evening)
Status: fixes committed; live re-validation = FRESH27

## What FRESH26 was supposed to prove

FRESH26 was the first trace under the attach-once fix (`fc128ca`): the smoke
keeps ONE x32dbg attached (`-KeepAttached` → `keptAttached=true`), and the
trace reuses it (`-ReuseAttached` → `reused_attached_debugger` in the log,
no second attach), eliminating the FRESH25 `STOP_gate=Denied` chain (second
attach → WOW64 attach-freeze → `evidence.monitor_unhealthy` → terminal Deny).

## What actually happened

The run was clean through M1 — **7th strong verdict** (`family_solo_emitted
axis=z members=4 score=0.92 span=275.2`), staging tick 5.3s, attendance gate
held correctly, smoke passed first try. But:

- `keptAttached=False` in the smoke report — **the smoke ran the OLD detach
  path despite od-048 passing `KeepAttached = $true`.**
- The trace therefore did a SECOND attach (the FRESH25 denial chain) — yet
  this time the gate held (`gate=OfflineReplayVerified` throughout, no
  STOP_gate), the window completed: `window_cpu_delta_ms=25203 liveness=running`
  → **`family-no-hit hit_members=0`** again.
- `values_changed=unknown` — the FRESH24/25 value-liveness discriminator
  produced no verdict, so world-advance remained unanswered.

## Root cause 1 (dispatch): `-KeepAttached` never reached the smoke function

`Invoke-AttachSmoke` (the smoke function) declares `[switch]$KeepAttached`,
and the script's top-level param block declares it too — but the dispatch at
the bottom of the script was:

```powershell
if ($AttachSmoke) {
    exit (Invoke-AttachSmoke -ProbeAddress $SmokeProbeAddress -ResultPath $SmokeResultPath)
}
```

`-KeepAttached` was **not forwarded** — the switch died at the top level and
the smoke always took the detach branch. The FRESH26 evidence is
unambiguous: `attach_smoke resume_settle_rounds=1 verified=True` (the detach
path's post-detach poll) with zero `keep_attached resume_settle_rounds` lines.

**Fix (one line):** pass `-KeepAttached:$KeepAttached` in the dispatch.
Also normalized the DryRun report field to a plain bool (`[bool]$KeepAttached`)
so the report shape is identical in both paths.

## Root cause 2 (wire shape): `Read-FamilyValues` read fields that don't exist

The value-liveness discriminator reads the armed addresses through the Host
read API (`/api/v1/game/discover/read`). The C# contract serializes
`OffsetReadItem` as `absoluteAddress` / `readOk` / `observedValueHex` /
`valueSummary` — but `Read-FamilyValues` read `$r.address` and `$r.value`:

- every mapped key collapsed to `''` (no `address` property) → the
  `ContainsKey` delta loop never matched → `values_changed=false` would have
  been a LIE even on success;
- on any failure it returned `$null` silently → `values_changed=unknown`.

So FRESH26's `unknown` was the function swallowing a read failure (game
paused under the debugger at window start → module-base resolve fails →
endpoint 400s → catch → null). The discriminator was dead on arrival.

**Fix:** `Read-FamilyValues` now parses the real wire fields and converts
`observedValueHex` (little-endian) back to a float via `BitConverter.ToSingle`,
keys on `absoluteAddress`, and logs `read_values ok read=N mapped=N` or
`read_values FAILED <reason>` instead of failing silently. Round-trip tested
offline: `6666E2C1` → −28.3 exactly.

## What still needs a live run (FRESH27)

1. `keptAttached=True` in the smoke report (dispatch fix).
2. `reused_attached_debugger pid=0x…` in the trace log (no second attach).
3. `read_values ok read=4 mapped=4` at window start/end.
4. A real `window_values_changed=true|false` verdict with `max_delta`:
   - `true` + hit → first `odwt-*.bin` writer report;
   - `true` + no-hit → the armed consensus class is genuinely never written
     per-frame (science question deepens — derived/one-time-copied field);
   - `false` → the replay world is not advancing during the window
     (paused/roster/packet-gap) → window placement / play-state fix.

## Artifacts

- Run log: `.data/od-049-fresh26.log`
- Result: `.data/od-049-fresh26-result.json` (7th strong verdict)
- Trace report: `.data/od-048-autotrace-20260806-181637.json` (+ `.family.json`)
- Smoke report: `.data/od-048-attach-smoke-20260806-181422.json`
- Host log: `$TEMP/od-launch-host.log`
