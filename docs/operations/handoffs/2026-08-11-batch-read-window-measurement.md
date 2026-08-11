# Handoff — batch read-pass measurement shipped (item-7 verification window) (2026-08-11)

## Summary

Pre-staged the item-4 measurement so the rehearsal session closes items 3
AND 4 in one run: the batch response now carries a wall-clock `Measurement`
(the read-pass window = first resolve -> last read, plus the G2 snapshot
moment), which quantifies how "one coherent moment" the whole-roster frame
read is — the item-7 verification window. Additive contract change; no
semantics changed.

## What landed

- **Application:** `EntityRegionsReadMeasurement(BatchStartedAtUtc,
  BatchEndedAtUtc, ClockSnapshotAtUtc?)`; `EntityRegionsReadResult` gains a
  trailing optional `Measurement` (null when no reads happened — inactive /
  unsupported-build batch outcomes).
- **ApiContracts:** `EntityRegionsReadMeasurementResponse` + `Measurement`
  on `EntityRegionsReadResponse`.
- **Coordinator (`ReadEntityRegionsAsync`):** captures `batchStartedAt`
  before the resolve-all pass, `batchEndedAt` after the last region read,
  and `clockSnapshotAt` at the G2 snapshot request (also reused as the
  snapshot's `observedAt`). Only the Resolved path carries a measurement;
  `BuildBatchResult` paths stay null.
- **Endpoint:** maps `Measurement` through.
- **Tests:** coordinator asserts measurement presence + sane ordering +
  snapshot null/not-null per path (exact-build, session, inactive,
  unsupported-build); web test maps the measurement and pins the three
  timestamps.

## Files touched

- `src/WotBTreader.Application/Game/GameSessionContracts.cs`
- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs`
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs`
- `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs`
- `tests/WotBTreader.Host.Web.Tests/GameApiEndpointsTests.cs`
- `docs/operations/batch-entity-read-design.md` (status + atomicity
  groundwork + item 4 pre-staged), `docs/operations/resolver-path-consolidation.md`

## Verification

- GameIntegration 284/284, Host.Web 151/151; full `scripts/validate.ps1`
  gate green (936 passed, 3 local opt-in skips, 0 warnings, 0 errors);
  file-tree regenerated.

## Remaining

- The approved live rehearsal run now collects everything item 7 needs
  about the read window: per-batch `sameDecodedClockProven`, the
  replay-time label, and the resolve-start -> last-read window. Per-entity
  double-read spans remain a separate item-7 question (region dumps do not
  double-collect position bytes). Item 7 itself stays LAST.
