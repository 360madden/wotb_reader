# Deep bug hunt — coordinated six-area sweep (2026-08-10)

Status: DONE. Six areas hunted in parallel (agent-style coordinated sweeps);
three real bugs found and fixed, all offline-proven, gate green, pushed
(`5dd8713`).

## Bugs found and fixed

### 1. CRITICAL — interceptor write discriminator misses every Double write (`5dd8713`)

The guard-page interceptor (`tools/WriteInterceptor/Interceptor.cs`) detected
writes by snapshotting **4 bytes as a float** and comparing with a `0.0001f`
epsilon. For the OD-044 replayTime plan — an **8-byte Double** — this fails
two ways:

- The low dword of a monotonic Double reinterpreted as float is a tiny
  denormal: 60.0 = `0x404E000000000000` (low dword `0x00000000` = float 0.0)
  → 60.016 = `0x404E04189374BC6A` (low dword `0x9374BC6A` = float ~−3.5e-38).
  Delta ~3.5e-38 ≪ 0.0001f → **every replayTime write reads as unchanged**.
- When the low dword happens to be NaN/Infinity as float, `JsonSerializer`
  **threw** (`unexpected_error:ArgumentException`) and the capture died
  without writing a report — no evidence at all.

Fix: the discriminator is now **byte-exact** on the tracked bytes with a new
`-ValueSize 4|8` (default 4 keeps the position path identical). Each hit
carries `valueHex` (the exact tracked bytes); the legacy `value` float is
emitted only when finite. The OD-044 driver now passes `-ValueSize 8` and the
plan's verdict contract documents the load-bearing requirement.

**Proof:** new `--counter --double` mode publishes an 8-byte replayTime-mimic
(Double advancing 0.016 s/frame). Armed with `-ValueSize 8` it captured
**126/126 distinct 8-byte patterns**, monotonic. The legacy float path is
unchanged — mechanism test still **189/189** with exact 0.5-progression, zero
hits in the suspended no-write window, 100% module-RVA+registers attribution.
Both phases are now permanently pinned in
`scripts/test-offline-write-observation.ps1`.

### 2. OD-044 driver missing its own play-state gate

The driver's plan mandates fail-closed on a paused replay ("a paused replay
writes no clock field; window not spent") but the driver never probed it —
`invoke-csharp-write-trace.ps1` has the probe (exit 7); my driver did not.
Added the `replay-play-state.ps1` probe with exit 7, matching the proven
pattern.

### 3. `replay-delta-extractor.py` unit-label regression

After the 10× tick-unit fix, the docstring still claimed "raw ticks (1e6
ticks/sec)" and the JSON emitted `replay_time_delta_unit_variants.ticks_1e6`
**carrying a 1e7-derived value** — the field name lied about its own units
(an operator dividing by 1e6 again would re-create the bug). Renamed to
`ticks_1e7` and corrected the docstring; verified the emitted
`tick_rate: 10000000` and `ticks_1e7: 40000000` for a 4 s window.

## Areas swept clean (no bugs)

- **Walker/published chains** (`OffsetChainWalker` + `OffsetTableReader` +
  `offset_check.py`): fail-closed validation on both the reader and walker
  sides; hop kinds, entity-lookup bounds, ring index, traversal budget all
  coherent; JSON shape matches the model.
- **Storage + CLI**: `SqliteHpGroundTruthProvider` (ticks, damage parsing,
  fail-closed zero duration), `CliInvocation` (value-taking options,
  duplicate rejection, `Enum.TryParse` rejects undefined numeric values),
  `hp-diff` verdict contract.
- **Rolling campaign scripts**: two-phase pulse, 401 refresh, KUSER drop,
  plateau handling all consistent under the corrected 1e7 unit.
- **`absoluteAddress` format**: hex-prefixed `0x…` as the driver expects —
  not a decimal-vs-hex bug (checked the wire shape in the harness examples).

## State

- `validate.ps1` exit 0; offset validator PASS; PSSA gate 0 violations;
  tree clean; `HEAD` == `origin/main` (`5dd8713`).
- No live session, no product code changed — the interceptor is the
  sanctioned research tool; its fix is what makes the pre-staged OD-044
  replayTime session actually capable of capturing the target field.

Next: the pre-staged live sessions remain the two approval-gated options —
replayTime (OD-044, now with a working Double discriminator) or HP (savanna
Palms victim 3760578 + medvedkovo 2549399).
