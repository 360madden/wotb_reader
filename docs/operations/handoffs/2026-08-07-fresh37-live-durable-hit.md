# OD M2 live — FRESH37: first durable module-mapped write-site hit (2026-08-07)

**Session outcome:** the C# guard-page interceptor captured **4 real writes with
durable module/RVA/instruction/register evidence** from a live battle — the
first `family-hit` whose write sites resolve to modules (`VCRUNTIME140.dll`).
This closes the FRESH36 evidence-durability gap the durable-evidence commit
(`163c95e`) prepared for: modules, RVAs, instruction bytes, and registers all
survived the round-trip into `.family.json` + `.capture.json`.

## Repository state

- Branch `freebuff/regain-ccontext-ef8b8a29-...` (worktree), HEAD `473f012`.
- Fresh interceptor publish + fresh Host Release build (stale-build guard
  tripped once: CLI's `AssemblyInfo.cs` regenerated after Host.Web's DLL — fixed
  by rebuilding Host.Web last; the guard scans generated `obj/` files too).
- No live game process remains (autoloop stops the game post-campaign).

## Evidence chain

**Run:** `od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly
-PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30`, session
`019fdbe1-5636-775f-9979-d712496726e5` (GB08_Churchill_I, viewpoint entity
2549401, decoded 271.4s).

**Timeline (UTC):**

| Event | Time |
|---|---|
| Start replay event | 11:00:23 |
| Match begin (model) | 11:01:13 |
| Fire-by deadline | 11:01:35.1 |
| Interceptor window (25s, in-battle) | ~11:01:35–02:00 |
| Report written | 11:01:39 |

**M1 correlate:** `verdict=evidence-strong`, `addresses_scored=646`,
`total_samples=9690`, 32 strong survivors (31 x + 1 z), **16 monitor rounds**
(stopped `fire-by-deadline`). Solo family emitted: `axis=x baseAddress=0x22BA1110
score=0.933 span=43.16 band=55–61.5s edgeAligned=False` — cleared the 0.9 floor.

**Hit report** (`.data/od-048-autotrace-20260807-070139.json.family.json`):
`verdict=family-hit hitsTotal=4 hitMembers=1 armedCount=1
windowValuesChanged=true interceptorGuardEvents=11 interceptorPagesArmed=1`
— one armed address, 4 writes, values moving (`windowValuesChanged=true`, not
the FRESH34 frozen-screen signature).

| Armed address | Axis | Hits | Write-site RIPs (module-resolved) |
|---|---|---|---|
| 0x22BA1110 | x | 4 | `VCRUNTIME140.dll+0xED69` (3×), `VCRUNTIME140.dll+0xE8AE` (1×) |

**Write-site decoding (offline, from instruction bytes):**
- `0x5AF2ED69` = `VCRUNTIME140.dll+0xED69`, bytes
  `89 17 83 C7 04 83 C6 04 83 E9 01 75 F1` = `mov [edi],edx; add edi,4;
  add esi,4; sub ecx,1; jnz` — the classic **4-dword (16-byte) memcpy loop**
  (ecx=4, matching x/y/z + one more float). Values captured: -0.0003, 245.35,
  -124.00 — coherent world coordinates, not garbage.
- `0x5AF2E8AE` = `VCRUNTIME140.dll+0xE8AE`, bytes `F3 A4 ...` = `rep movsb`
  — a byte-wise memcpy path.

**Conclusion:** the armed viewpoint x-coordinate is written by **CRT memcpy
struct copies** (16-byte/4-float stride) — a synchronized multi-copy field
(FRESH23 class), not a direct game `movss` write. The real per-frame write
sites are one level up: the *source* buffers the memcpy reads from (`esi`
registers at hit time: 0x2C2A9E18/0x2C2AB2E0/0x2C2AD880 in the hit samples).
Durable capture has the full 135-module attach-time snapshot (wotblitz.exe
base `0x00C30000`, 71,835,648 bytes) so absolute RIPs are module-mappable
offline — the FRESH36 gap is closed.

## What was fixed this session (tuning, no code change)

Two live attempts before the hit were honest timing negatives:
1. `-PlaybackSpeedEstimate 2.4` alone → **3 rounds, `no-evidence`**: the 55s
   staging gate is a *wall-clock* wait, so at 2.4× the fire-by deadline
   (battle-end − 45s) left no sampling window.
2. Default 2.0 → **15 rounds, `evidence-strong` but 0.857 < 0.9 floor**: no
   solo family emitted, no trace (fail-closed floor held, as designed); FRESH36
   hit the same config at score 1.0 — pure variance.
3. **`-PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30`** → 16 rounds,
   score 0.933 family, trace landed in-battle, hit. At 2.4×, 30 wall-seconds
   ≈ 72 battle-seconds elapsed — still past the 50s attendance threshold, so
   staging selectivity is preserved while the monitor keeps ~14+ rounds.

## Files

- `.data/od-048-autotrace-20260807-070139.json` (auto-trace report)
- `.data/od-048-autotrace-20260807-070139.json.family.json` (family-hit report)
- `.data/od-048-autotrace-20260807-070139.json.capture.json` (**durable raw
  capture**: 135 modules, 4 hits with rva/value/instructionHex/registers)
- `.data/od-049-autoloop-result.json` (M1 report, blitzLog block)
- `.data/od-049-fresh37-run4.log` (driver log)

## Assumptions and unknowns

- `VCRUNTIME140.dll+0xED69/0xE8AE` are library memcpy sites, **not** the game's
  own write instruction — the armed member is a copied struct member. The
  next M2 step is to trace the *source* (register `esi` at hit time) or arm a
  source-buffer page to catch the game's real per-frame write.
- Absolute `0x22BA1110` is heap-dynamic for this process instance; no module
  root yet. Kind stays `heap-dynamic` until a source-buffer or root is proven.
- Single armed member (solo path) — no complete XYZ family; do not promote.

## Integration risks / next steps

- Next live: republish is already fresh; run again with the same invocation to
  (a) reproduce the hit and (b) arm the memcpy **source** addresses
  (from the `esi` samples) for the real write site. Until then no offset is
  runtime-supported — M3 repeatability not met.
- `offline/file-tree.md` refreshed for this handoff; full `validate.ps1` still
  pending before any milestone commit.
