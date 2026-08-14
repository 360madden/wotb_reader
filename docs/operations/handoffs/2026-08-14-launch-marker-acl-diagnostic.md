# Launch-marker ACL diagnostic — strict failure made observable

**Date:** 2026-08-14 (UTC)

**Roadmap:** OD-099 persisted completion marker; clustered-live preflight

**Base commit:** `bb40e9b` (`fix(od): retry strict ACL confirmation`)

## Result

The clustered launch retry again stopped at
`FAILED_launch_marker_directory_acl` after successful version probe, host
startup, and replay import. The new 500 ms bounded confirmation did not clear
the condition. The game process count stayed zero, so the replay remains
unconsumed.

A separate fresh Windows PowerShell 5.1 process then ran the exact ACL setter
and verifier against the same directory while the host was active. All eight
100 ms samples passed every predicate: not a reparse point, inheritance
protected, owner SID matched, exactly one owner Allow ACE, and FullControl.
That falsifies the prior handoff's narrow timing explanation as sufficient;
the launcher-process-only difference remains unresolved.

The failure branch now emits one bounded diagnostic condition after retries:
reparse point, inheritance, owner, rule count/owner/type/rights, exception
type, or `verified-after-retry-window`. It never emits a path, SID, ACL text,
or capability. The authorization decision is unchanged: the launcher still
exits before game start whenever strict confirmation returns false.

## Validation

- Completion-marker Pester suite: 13/13 passed under Windows PowerShell 5.1.
  The new tests pin bounded/no-path diagnostics and the exact verified ACL.
- `git diff --check`: passed.
- PSScriptAnalyzer repository gate: passed (99 non-fatal warnings reported).
- The first full-gate attempt was environment-blocked by the loopback host's
  Release DLL locks; after stopping that host, one parallel-run SQLite
  concurrency test flaked with a disposed handle. The exact test passed 1/1
  in isolation, then the full gate passed.
- Final `scripts/validate.ps1`: 1,205 tests passed with 7 local opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer,
  Pester 13/13 + 16/16 + 4/4 + 4/4, offline pack, blocker/ledger consistency,
  and eight-chain schema validation passed.

## Next

Run one more pre-gate attempt to capture the condition code. Because neither
attempt created a game process, no replay start has been spent. Use the code
to correct the launcher-process discrepancy without relaxing any ACL rule;
only then resume the clustered live evidence.
