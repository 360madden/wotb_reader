# Handoff — batch N-entity region read: coordinator + endpoint implemented (2026-08-11)

## Summary

Implemented the batch entity-region read surface per
`docs/operations/batch-entity-read-design.md` (items 1–2 of its sequencing):
`GameSessionCoordinator.ReadEntityRegionsAsync` + `POST
/api/v1/game/discover/entity-regions`, with 7 coordinator tests + 4 web
endpoint tests. No shared-contract semantics changed beyond the additive
batch records; the single-read seam is untouched.

## What landed

**Contracts (additive):**
- ApiContracts: `EntityRegionsReadRequest`, `EntityRegionReadItemRequest`,
  `EntityRegionsReadResponse`, `EntityRegionReadItemResponse`.
- Application: `EntityRegionsReadRequest` (MaxEntities = 16,
  MaxTotalBytes = 16 KB), `EntityRegionReadRequestItem`,
  `EntityRegionsReadResult`, `EntityRegionReadResultItem`; new
  `IGameMemoryScanner.ReadEntityRegionsAsync` member.

**Coordinator (`ReadEntityRegionsAsync`):**
- Validation before the gate: entity count 1..16, per-entity length 1..4096,
  known anchor enum, total bytes ≤ 16 KB — any violation fails closed with
  `discover.entity_regions.invalid_request` and never creates a reader.
- Read discipline per the design: gate + build identity first (no reads on a
  violation) → resolve ALL addresses under one lease → read ALL regions →
  ONE post-read G2 clock snapshot (≤ 2 s bound) that labels the whole batch
  (`SameDecodedClockProven`); per-entity time mirrors carry the batch label
  (only the batch attestation is load-bearing).
- Per-entity statuses in REQUEST ORDER: an unresolved entity fails only
  itself (`EntityNotFound` + failure stage, null bytes); the retryable
  `ReplaySessionInactive` fails the WHOLE batch (`pre-battle-inactive` on
  every item, no region reads — a frame cannot be half-timed); region-read /
  anchor failures report `ReadFailed` with a stage, never failing the batch.
- Privacy unchanged: bytes only; addresses stay coordinator-owned.

**Endpoint:** `ReadEntityRegionsAsync` handler — per-item anchor parse
(invalid → 400 `invalid_anchor`), empty entities → 400 `invalid_request`,
failure mapping, base64 bytes, no-address-leak asserted in tests.

**Tests (11 new):**
- Coordinator (7): exact-build bytes in request order (one reader, one read
  per entity at the ring-record address); ONE clock snapshot for two
  entities (CallCount == 1) with per-item mirrors; one unresolved entity
  fails only itself (1 region read); inactive phase fails the whole batch
  (no reads); invalid request variants all fail before the gate (empty, >16,
  length 0, bad anchor enum, >16 KB total); missing gate; unsupported build.
- Web (4): batch response mapping + base64 + no-address leak + forwarding
  (entities + anchors + session id); invalid anchor; empty entities; failure
  mapping. Test helper extended (per-entity address map; `StubReplayClockSource`
  gained `CallCount`).

## Files touched

- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` (batch records)
- `src/WotBTreader.Application/Game/GameSessionContracts.cs` (batch records +
  interface member)
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`
  (`ReadEntityRegionsAsync` + `BuildBatchResult`)
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` (route + handler)
- `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs`
  (7 tests + helper extensions)
- `tests/WotBTreader.Host.Web.Tests/GameApiEndpointsTests.cs` (4 tests +
  fake member), `ReadApiEndpointsTests.cs` (stub member)
- `docs/operations/batch-entity-read-design.md` (status → DESIGN ADOPTED,
  items 1–2 DONE), `docs/operations/resolver-path-consolidation.md`
  (item 6 implementation marked)

## Verification

- GameIntegration 284/284, Host.Web 151/151, full `scripts/validate.ps1`
  gate green (932 passed, 3 local opt-in skips, 0 warnings, 0 errors).

## Remaining

- Item 3 of the design sequencing: the replay rehearsal (dump all roster
  entities at replay-clock-labeled times vs the decoded frame) needs one
  approved live session.
- Item 4: measure the batch window + double-read spans → feeds item 7
  (hardware atomicity, still LAST and untouched).
