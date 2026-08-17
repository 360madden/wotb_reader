# Handoff — bug hunt: memory-scan engine and guarded-reader lease lifecycle

Date: 2026-08-17 (UTC) · Status: Committed · Type: bug hunt + hardening

## What this session did

Follow-on to `2026-08-17-pen-capture-bughunt-15-rounds.md`: a round-by-round
review of the discovery/scan pipeline the penetration census source depends
on — `MemoryScanDiscoverer` (value/neighborhood/pointer-chain scans),
`MemoryScanEngine` (snapshot/compare/rolling-baseline), `GuardedMemoryReader`
(`AuthorizedProcessLease`, `AuthorizationReadGate`, the reader factory), the
coordinator's scan-authorization wrappers, `Type10EntityPositionResolver`,
and `NativeMethods`. Every round was verified against the actual code.

## Bug found and fixed

1. **`MemoryScanDiscoverer.ScanNeighborhood` skipped the reference value when
   `WindowSize` was not an exact multiple of the value width.** The float/int
   probe loop strided by 4 and the double loop by 8 starting from offset 0 of
   the window, i.e. from `reference - window`. The reference itself
   (displacement zero) therefore landed in the probe set only when
   `window % 4 == 0` (or `% 8 == 0` for double) — for any other window the
   scan silently omitted the exact address the neighborhood was asked to
   inspect. The probe enumeration is now a small pure helper
   (`EnumerateNeighborhoodProbeOffsets`) that aligns each stride to the
   reference, so displacement zero is always included whenever a full value
   fits. One regression test was added and **proven to fail on the old code**
   (striding from zero fails the reference-inclusion and alignment asserts
   for windows 3, 7, 65, and 100).

## Rounds swept clean (no change)

- **R1 `Scan` value scan:** chunk-overlap/`chunkOnlyLimit` correct — values
  straddling a chunk boundary are re-read in the next chunk and region tails
  are treated as hard boundaries; alignment and truncation accounting correct.
- **R2 `ScanNeighborhood`:** range/overflow validation, window clamp, and the
  page-walking `ReadWindow` all fail closed; no out-of-bounds reads.
- **R3 `ResolvePointerChain`:** depth bounded to four dereferences, cycle
  detection, pointer-width decode, and root/target address validation correct.
- **R4 `MemoryScanEngine.CreateSnapshot`:** byte-budget soft-cap, region
  enumeration/alignment, filter validation, and oldest-session eviction
  correct; concurrent compare is guarded by a reference-equality re-check.
- **R5 `MemoryScanEngine.Compare`:** delta/exact/rolling-baseline semantics,
  retained/truncated counts, and read-failure handling correct; a replaced
  snapshot aborts with `session_changed` rather than overwriting a newer
  baseline.
- **R6 `AuthorizedProcessLease`:** identity revalidation and architecture
  resolution on open; handle disposed on every failure path (no leak).
- **R7 `AuthorizationReadGate`:** revocation is fail-closed for newly admitted
  reads while an already-admitted native read may complete — documented and
  test-pinned.
- **R8 coordinator scan wrappers:** gate re-check before and after every scan,
  discard-on-revocation, and linked cancellation all fail closed.
- **R9 `Type10EntityPositionResolver`:** bounded tree traversal, vtable
  identity re-gates, double-read consistency, non-finite rejection, and
  checked `TryAdd`/`TryMultiply` arithmetic — no reachable bug.
- **R10 `NativeMethods`:** working-set struct is two pointer-sized fields and
  the copy-on-write protection mask reads the correct 11 bits — correct.

## Honest note

- The `ScanNeighborhood` fix is a latent-hardening change, not a reachable
  exploit: the HTTP endpoint clamps `WindowSize` to 64–4096 (always a
  multiple of 4 and 8), so the shipped path already included the reference.
  The fix protects any future non-HTTP caller (e.g. a CLI) that passes a
  non-multiple window, and removes the arbitrary window-start alignment.

## Validation

- `GameIntegration.Tests`: 377 passed, 6 opt-in skips (1 new regression test).
- Regression proven to fail against the old stride-from-zero behavior.
- Build of the affected projects: 0 warnings, 0 errors.
