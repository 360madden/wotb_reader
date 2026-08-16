# SQLitePCL disposal race fix (Host.Cli.Tests)

**Date:** 2026-08-16 (UTC)
**Status:** merged
**HEAD:** `3e27d87 fix(sqlite): disable pooling in single-shot CLI to remove teardown race`
**Blocker:** none new

## Root cause

`Host.Cli.Tests` runs with method-level parallelism, and every test's
`TemporaryDataRoot.Dispose()` called the process-global
`SqliteConnection.ClearAllPools()`. One test's teardown therefore drained
*every* pool while sibling tests still held active connections, intermittently
surfacing as `SQLitePCL.sqlite3` "disposed object" errors under the parallel
full gate. `StorageTestScope` and `CompositionRootTests.TemporaryRoot` carried
the identical latent drain.

## Fix

- Added `SqliteStorageOptions.Pooling` (default `true`) and honored it in
  `SqliteStorageContext.OpenConnectionAsync`.
- Added `TreaderBootstrapOptions.SqliteConnectionPooling`, threaded through
  `AddWotBTreaderFoundation`.
- The single-shot CLI host now passes `SqliteConnectionPooling: false`;
  pooling buys nothing for a one-command-per-process host and only left file
  handles open until exit.
- Teardown in the three affected test projects now relies on non-pooled
  connections that close on dispose; all `ClearAllPools()` calls and the raw
  seed connections' implicit pooling were removed.
- Two composition tests lock the option plumbing (pooled default + explicit
  non-pooled propagation).

No `ClearAllPools`/`ClearPool` call remains in `tests/` or `tools/`, so the
race is structurally removed rather than timed around.

## Validation

- Full `scripts/validate.ps1` gate passed: 0 build warnings/errors,
  **1,330 passed**, 8 expected opt-in skips, 0 failed.
- `Host.Cli.Tests` 67/67 across the gate plus three extra runs, no flake.

## Next decision

The HUD code ship and this test-infrastructure fix are both merged. Remaining
forward work is the live/owner-gated penetration capture and HUD visual review
(next-10 actions 1-6 and 10), plus offline items 7 (rendezvous ACL hardening)
and 8 (Oasis batch re-verdict) — none of which this scope admitted.
