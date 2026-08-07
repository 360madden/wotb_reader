# OD M2 — FRESH39: dynamic source-arm implemented + offline-validated; live rounds scored below the solo floor (2026-08-07)

**Session outcome:** the changed hypothesis from FRESH38 (arm the page holding
the `esi` copy-source pointer **in the same trace window it is discovered**) is
now **implemented in `tools/WriteInterceptor` and proven offline end-to-end**.
The live validation rounds (3 attempts on the primary replay, 1 on a second
replay) did **not** reach the write-trace this session — the M1 correlate scored
top survivors 0.8–0.867, all below the `AutoTraceMinMemberScore` 0.9 solo floor,
so the M2 stop rule correctly refused to burn a trace. That is the fail-closed
discipline working; the source-arm code path is shipped, tested, and waiting for
a family ≥ 0.9 to fire live.

## What changed (code)

- **`tools/WriteInterceptor/Interceptor.cs`**: new `-ArmSourceOnFirstHit` mode.
  On the first captured member write, the interceptor reads the captured `esi`
  (the CRT memcpy copy source), snapshots up to 4 floats at that address, arms
  the page (cap 8 source pages, fail-open on unreadable/uncommitted pages), and
  then scans those addresses on every subsequent guard event as **`source`-kind
  hits** — catching the game's own fill write one level above VCRUNTIME.
- **`tools/WriteInterceptor/CounterMode.cs`**: rewritten to faithfully mimic the
  game — the armed destination is written by a **real CRT memcpy**
  (`msvcrt!memcpy` via P/Invoke; the 16-byte `Buffer.BlockCopy` was JIT-inlined
  and lost the esi/edi ABI) from a **native page-aligned source buffer**
  (`VirtualAlloc`; adjacent `AllocHGlobal` allocations shared a 4KB page, so the
  memcpy READ of the source consumed the guard before the write landed — the
  write discriminator saw no change). The synthetic counter now faults inside
  memcpy with `esi`=source/`edi`=dest exactly like the game's VCRUNTIME copy.
- **`tmpwotb-e2e/test-guard-interceptor.ps1`**: new source-arm scenario — runs
  the interceptor with `-ArmSourceOnFirstHit`, asserts `sourcePagesArmed ≥ 1`,
  ≥1 source-kind hit, and that the source hit's RIP is **distinct** from the
  member (copy-loop) RIP.
- **Wiring**: `-ArmSourceOnFirstHit` passed through `Program.cs` →
  `invoke-csharp-write-trace.ps1` → `od-048` (`$wtArgs` splat, csharp engine
  only) → `od-049` (`$m1Args` splat).
- **Family report**: `interceptorSourcePagesArmed` + `sourceHits` fields;
  write-site entries carry a `kind` (member/source).

**Review fix (code review):** source snapshots are now committed to the tracked
set only AFTER a successful page arm. Earlier, a failed arm (uncommitted page,
NOACCESS, VirtualProtectEx error) left the addresses tracked-but-unarmed, and
because the game's source buffer drifts every frame, the scan would emit false
`source`-kind hits attributed to the member copy-loop RIP. The fix keeps an
unarmed page out of the discriminator entirely; the smoke test still passes
with `sourcearm_pages=1`, 1 source-kind hit, distinct fill RIP.

## Offline validation (all green)

- `WriteSiteAnalysisTests` 7/7 (unchanged Core surface).
- `test-guard-interceptor.ps1` full run: member capture PASS (182 hits, RIP
  resolves to `msvcrt.dll+0x8DD34` — the real CRT memcpy loop), **source-arm
  PASS** (1 source page armed from captured `esi`, source-kind hit at a distinct
  JIT fill-site RIP with the source value advancing 113.5→114.0), negative
  control PASS (bogus pid fails closed).

## Offline analysis of the durable captures (real Core code)

Throwaway harness (`.data/wsan-harness/`, gitignored) fed both family-hit
`.capture.json` files through `WriteSiteAnalysis`:

- **FRESH37 run-4** (`od-048-autotrace-20260807-070139`): 4 hits, 2 write sites
  (VCRUNTIME+0xE8AE `rep movsb`, +0xED69 copy loop), resolver
  `unknown/ambiguous` with **object-base candidates `ebx+0x70` and `edi+0xB0`
  (support 2 each)** — the armed coordinate sits at `edi+0xB0` inside the memcpy
  destination struct.
- **Phase A2** (`od-048-autotrace-20260807-072224`): 2 hits, 1 write site
  (+0xED69), resolver `heap-dynamic` (single member, no consensus base).

Both confirm the multi-copy class: the write is a CRT struct copy, and the next
target is the copy source held in `esi` — exactly what the source-arm change
arms dynamically.

## Live rounds (3 + 1 attempts)

| Attempt | Config | Outcome |
|---|---|---|
| R1 (12:12) | proven + `-ArmSourceOnFirstHit` | `evidence-strong`, 20 strong survivors, top 0.867 → no family → no trace (correct) |
| R1 retry (12:16) | same | `evidence-strong`, 13 survivors, top 0.867 → no family (correct) |
| R1 attempt 3 (12:28) | same | `evidence-strong`, 20 survivors, top 0.8 → no family (correct) |
| R2 (12:20) | same, **second replay** `66703f50…` | **launch-path negative**: replay is **11.18.0** → game opened ReplayList browser (`Controller activated: ReplayList`), no direct playback, `watch_exit=3` |

- The primary replay's correlation scores today (0.8–0.867) sit just under the
  0.9 floor; FRESH37 run-4 scored 0.933 with the identical invocation, so this
  is run-to-run variance, not a config regression. Per the choreography tuning
  rule we retried; per the ledger's do-not-repeat rule we stopped at 3.
- **BLK-0019 (`independentReplays`)**: cannot be advanced with local content —
  the only 11.19.0 replay available is the primary (`a9aed046…`); the other
  three fixtures are 11.18.0 synthetic stubs. A second distinct 11.19.0 replay
  must be imported before `independentReplays` can move.

## Repository state

- Ledger: `OD-RECOVERY-046` (FRESH37) + `OD-RECOVERY-047` (FRESH38) result
  sections + index rows appended; "Last updated" header refreshed; offline pack
  gate passes (28 result sections / 42 index rows, BLK contiguous).
- `scripts/validate.ps1` full gate: **PASSED** (build 0 errors, 633 tests passed,
  2 opt-in skips, repo scan + PSSA + offline checks green).
- Uncommitted: interceptor source-arm + counter + harness + wiring, ledger,
  handoff. Worktree branch `freebuff/regain-ccontext-ef8b8a29-…`.

## Next

1. **Live source-arm validation** (one round): the code is ready and offline-
   proven; it needs one M1 correlate that scores ≥ 0.9 (FRESH37 run-4 hit 0.933
   with this invocation — variance will eventually roll a family). Invocation:
   `od-049-autoloop.ps1 -AttachSmokeOnFirstRound -StageViewpointOnly
   -PlaybackSpeedEstimate 2.4 -StageMinBattleSeconds 30 -ArmSourceOnFirstHit`.
2. **Import a second distinct 11.19.0 replay** for BLK-0019 `independentReplays`.
3. When a source-arm hit lands, resolve the fill-site RIP in Ghidra and check
   whether it is game code (not VCRUNTIME) — that would be the real write site.
