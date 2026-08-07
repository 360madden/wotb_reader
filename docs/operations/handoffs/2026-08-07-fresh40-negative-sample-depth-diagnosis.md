# OD M2 — FRESH40: live source-arm round negative; sample-depth diagnosis → FRESH41 hypothesis (2026-08-07)

**Session outcome:** the first live attempt at the FRESH39 dynamic source-arm
(`-ArmSourceOnFirstHit`) did **not** reach the write-trace. M1 correlate scored
verdict `evidence-strong` with **526 addresses scored (130 viewpoint-only) /
7364 samples / 20 strong survivors**, but the top score was **0.857 (6/7
matches) — below the `AutoTraceMinMemberScore` 0.9 solo floor**, so
`family_mapping_failed no_families_from_survivors` fired and the M2 stop rule
correctly refused the trace. **The source-arm was never armed** (it requires a
family hit first). Clean run otherwise: `launch_exit=0`, game stopped after
campaign, no stale processes.

This is the **4th consecutive sub-0.9 round** on the identical proven invocation
(0.8 / 0.867 / 0.8 / 0.857 today vs the 0.933 FRESH37 run-4 hit this morning).

## Diagnosis (offline, no game — both initial suspects ruled out)

**`measuredPlaybackSpeed: None` is NOT the discriminator.** The speed only
resolves from a log-derived real window (`Get-BlitzRealWindow`) that requires a
definitive battle-end marker (`Stop replay` / `OnLeaveBattle` / player
`onLeaveWorld`) at/after match begin; the auto-loop environment often does not
produce one before the fire-by deadline, so the driver falls back to
`-PlaybackSpeedEstimate`. **FRESH38's 0.933 hit ran the identical estimate-only
path** (no measured speed logged, fire-by-deadline stop at 16 rounds) — so this
is the designed normal path, not a regression.

**Staging tolerance was not widened by attendance latency.** The log's
`staging_tolerance=0` is `ScanTolerance 0.001` rounded — the exact-match
setting, unchanged. The old max-speed × load-latency auto-scale is gone from
od-048's code; the current doc comment confirms exact-match is the empirically
selective setting (~1500 candidates).

**The real cause: correlation score quantization from a thinner sample grid.**

| | FRESH38 (hit) | FRESH40 (miss) |
|---|---|---|
| Top score | **0.933** (14/15) | **0.857** (6/7) |
| Addresses scored | 598 | 526 |
| Total samples | 8970 | 7364 |
| Monitor | 16 rounds, fire-by-deadline | 15 rounds, fire-by-deadline |

The score is a match ratio; fewer samples per address quantize it coarser
(0.857 = 6/7) and widen ambiguity bands. FRESH37's own handoff documents "the
same config hit at score 1.0 — pure variance."

## Changed hypothesis for FRESH41 (ledger rule: no 5th identical run)

Sharpen the sample grid so the ratio quantizes finer (6/7 → 14/16) and bands
tighten:

- `-ReadIntervalSeconds 1.0` (was 2.0 — doubles samples per address in the same
  battle window)
- `-MaxReadRounds 120` (was 50 in od-049's default; the monitor already stops at
  fire-by-deadline, so this only matters if the window permits more rounds)

**Driver change (landed):** `od-049-autoloop.ps1` now exposes
`-ReadIntervalSeconds` (default 2.0) and splats it through to od-048's
`[double]` param — verified binding, parse-clean, PSSA-gate-clean. The FRESH41
standby launcher (`.data/launch-fresh41-sourcearm.ps1`) carries the fix; it has
**not** been run — launching requires operator approval.

## Also fixed (pre-existing, found while validating)

`tmpwotb-e2e/test-solo-emission.ps1` was failing on a **PowerShell
scalar-vs-array bug**, not a product regression: `$families | Where-Object {
$_.solo }` with exactly one match returns a scalar `PSCustomObject` whose
`.Count` is `$null`, so `$emitted.Count -ne 1` threw "got " (empty) even though
the block emitted correctly. Array-wrapped with `@(...)`; both cases now pass
(0x1FC57238 emitted solo; degenerate 40s-band y@1.0 refused).

## Ledger

Recorded as `OD-RECOVERY-048` (index row + result section; offline pack gate
green, 29 result sections / 43 index rows).
