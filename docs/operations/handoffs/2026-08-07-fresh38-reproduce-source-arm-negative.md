# OD M2 live — FRESH38: hit reproduced; cross-battle source-arm ruled out (2026-08-07)

**Session outcome:** the proven invocation (`-AttachSmokeOnFirstRound
-StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30`)
**reproduced the family-hit** — the second live hit with the durable
module/RVA evidence path (M3 repeatability step 1 on the way). Separately, the
planned "arm the memcpy source addresses next battle" phase is **ruled out by
live evidence**: the `esi` sources captured at hit time are battle-scoped heap
allocations, not process-stable buffers — arming them in a later battle is
invalid for the same reason cross-process arming is (ledger decision register:
do not repeat an approach without a changed hypothesis).

## Repository state

- Worktree branch `freebuff/regain-ccontext-ef8b8a29-...`, HEAD `3cad71e`
  (FRESH37 handoff + file-tree committed).
- Interceptor publish + Host Release build fresh from earlier this session.
- No live game process remains (game exited after battle 2; `-KeepGame` held —
  the process died on its own, not via the autoloop stop).

## Evidence chain

**Phase A (reproduce):** `od-049-autoloop.ps1 -AttachSmokeOnFirstRound
-StageViewpointOnly -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30
-KeepGame`, session `019fdbc6`-family replay (GB08_Churchill_I, viewpoint
2549401, 271.4s).

**M1 correlate:** `verdict=evidence-strong`, `addresses_scored=598`,
`total_samples=...`, 29 strong x survivors, **16 rounds** (fire-by-deadline).
Solo family emitted: `axis=x baseAddress=0x23982E10 score=0.9+`.

**Auto-trace:** `verdict=family-hit hitsTotal=2 hitMembers=1 armedCount=1
windowValuesChanged=true` — 1 member (0x23982E10, x), 1 write site:
`VCRUNTIME140.dll+0xED69` (the same 4-dword memcpy loop as FRESH37; values
98.95 / 18.20 — coherent world coordinates).

## The negative that matters (cross-battle source-arm)

Captured memcpy **source** (`esi`) addresses per run:

| Run | `esi` source addresses (hex) |
|---|---|
| FRESH37 (earlier process) | 0x2C2A9E18 / 0x2C2AB2E0 / 0x2C2AD880 |
| FRESH38 phase A (this process) | 0x2DDBB418 / 0x3EBEB878 |

- Sources differ **per launch** (different address spaces) and **per hit within
  one window** (the two phase-A hits are ~0x110000 apart) — the coordinate
  member is fed by multiple ephemeral copy sources, not one canonical buffer.
- Therefore "capture esi in battle 1, arm it in battle 2 (same process)" cannot
  work: battle 2 reallocates the sources. Same reasoning as cross-process.
- The game process also exited after battle 2 (~11:25:44Z, `onLeaveWorld`
  isPlayer:1), so even a same-process phase B had no live window this round.

**Conclusion:** chasing captured `esi` addresses later is exhausted. The
changed hypothesis for catching the game's real per-frame write is to arm the
**source in the SAME window it is discovered** — i.e. an interceptor behavior
change: on the first hit, read `esi` (the copy source) and dynamically arm that
page for the remainder of the trace window. That is a code change to
`tools/WriteInterceptor/Interceptor.cs` (unit-testable), not another live
round on the current binary.

## Files (this session)

- `.data/od-048-autotrace-20260807-072224.json` + `.family.json` + `.capture.json`
  (family-hit evidence, 135 modules, registers incl. `esi`/`edi`/`ecx`)
- `.data/od-049-autoloop-result.json` (M1 report)
- `.data/od-049-fresh38-phaseA2.log` (driver log)
- `offline/file-tree.md` (refreshed for this handoff)

## Assumptions and unknowns

- Write sites remain CRT memcpy loops (VCRUNTIME140) — the armed member is a
  synchronized copy, never a direct game `movss`. Kind stays `heap-dynamic`.
- The dynamic source-arm interceptor behavior is designed but NOT implemented
  or live-validated; treat this session as reproducing the durable-hit
  mechanism, not yet catching the game's direct write.

## Next steps

1. (Recommended) Implement + unit-test the interceptor source-arm: on first
   hit, arm the page containing `esi` for the rest of the window; republish;
   one live round. This is the changed hypothesis the ledger requires.
2. Alternatively continue M3 repeatability (2 launches × 2 replays) with the
   proven invocation — the second reproducible hit is already recorded.
3. Full `validate.ps1` pending before any milestone commit.
