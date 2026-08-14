# Item-7 Branch-B batch measurement persistence — live driver ready

**Date:** 2026-08-14 (UTC)

**Roadmap:** resolver consolidation item 7, Branch B step 3

**Base commit:** `35bbad0` (`feat(od): instrument camera double-read evidence`)

## Result

The clustered-live preflight found that the batch endpoint and DTO already
carried the complete read-pass measurement, but
`invoke-batch-rehearsal.ps1` discarded it while writing the evidence dump.
The previous documentation claim that the rehearsal reported the measurement
was therefore stale. Launching in that state would not have preserved the
Item-7 evidence the top-10 action promised.

The driver now converts every resolved batch response through a pure,
fail-closed helper before writing the dump. The persisted whitelist contains:

- `batchStartedAtUtc` and `batchEndedAtUtc`;
- `clockSnapshotAtUtc` (the post-read G2 snapshot moment);
- derived `readPassMilliseconds`;
- derived `clockSnapshotLagMilliseconds`.

Missing or incomplete timestamps, a pass that ends before it starts, or a
clock snapshot that predates the pass end aborts the rehearsal. No new raw
bytes, entity identifiers, paths, addresses, or capabilities enter the
measurement block. The existing region evidence and cross-check schema remain
additively compatible.

## Scope boundary

- No API/DTO, coordinator, resolver, runtime offset, or shared flag changed.
- `ConsistentDoubleRead` and `HardwareAtomicReadProven` remain unchanged.
- The owner-gated Branch-B step-2 proposal remains unapplied.
- No replay or game process was launched during this unit.

## Validation

- Batch read-pass measurement Pester suite: 4/4 passed under Windows
  PowerShell 5.1.
- PowerShell parse check and `git diff --check`: passed.
- PSScriptAnalyzer repository gate: passed (99 non-fatal warnings reported).
- Full `scripts/validate.ps1`: 1,205 tests passed with 7 local opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer,
  Pester 8/8 + 16/16 + 4/4 + 4/4, offline pack, blocker/ledger consistency,
  and eight-chain schema validation passed.

## Next

Run the approved top-10 launch cluster. The batch lane is acceptable only if
all three scheduled dumps preserve valid measurement blocks, keep the decoded
clock attestation, and pass the existing position cross-check. Any missing or
reversed measurement remains an honest-negative abort.
