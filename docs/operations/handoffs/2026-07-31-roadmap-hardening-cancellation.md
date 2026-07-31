# Session handoff — 2026-07-31: Offset evidence and scan cancellation hardening

**Status:** implementation and validation complete; changes ready for commit

## What changed

- `OffsetTableReader` now fails closed when a table has an empty or malformed
  `executableSha256`; a table is usable only when its SHA-256 exactly matches
  the observed executable hash. Malformed observed hashes return a stable
  failure instead of reaching comparison logic.
- Candidate offsets remain discovery-only. `GameSessionCoordinator` now
  requires an explicitly `Verified` field before creating a runtime memory
  base; a hash match alone cannot authorize candidate reads.
- The observed executable hash is validated before file access, preventing
  malformed identity evidence from reaching comparison logic.
- `MemoryScanDiscoverer` and `MemoryScanEngine` accept cancellation tokens and
  check them at region, chunk, and neighborhood-value boundaries.
- `GameSessionCoordinator` passes the request cancellation token through the
  snapshot, compare, known-value, and neighborhood scan paths.
- Added application tests covering valid exact hashes, missing/malformed hash
  rejection, mismatches, unknown validation fields, incomplete promotion
  evidence, and pre-cancelled offset loads.
- Updated the stale replay decoder regression fixture from 11.19 (now
  supported) to 11.20 (unsupported), preserving the unknown-version test.

## Safety effect

Placeholder offset files in `memory-offsets/` remain explicit evidence gaps and
cannot silently authorize memory reads. Long-running discovery scans can stop
without continuing to enumerate and read the entire process after the caller
cancels the request. Existing process/session gates remain unchanged.

## Validation

- Release build: 0 errors, 0 warnings.
- Full solution tests: 419 passed, 0 failed, 4 opt-in skips.
- Offset schema/documentation validator: passed for all 3 offset files.
- `git diff --check`: passed.

## Remaining roadmap work

- Add dedicated cancellation tests for the native scan loops when a portable
  fake platform is available.
- Complete the real installed-game HUD smoke test and promote only evidence
  backed offset fields.
- Keep placeholder and candidate offset tables unpromoted until exact
  executable hashes and required dynamic evidence are recorded. The reader still
  accepts the complete table shape for discovery, but runtime reads require
  per-field `Verified` status.
