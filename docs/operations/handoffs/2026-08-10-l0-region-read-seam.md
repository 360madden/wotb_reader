# Handoff — 2026-08-10: L0 entity region-read seam (the ONE product addition)

**Branch:** `main` — clean tree before this phase, gate green after.

## What landed

### L0 — `EntityRecordRegionReadRequest/Result` (shipped end-to-end)

The roadmap Phase 2 gating item — the single product addition every live
session (L1 HP, L2 facing, L3 damage-dealt, L4 replayTime) consumes — is now
implemented, offline-tested, and gated-safe:

- **Application contracts** (`GameSessionContracts.cs`):
  `EntityRecordRegionReadRequest(EntityId, RegionLength, BattleSessionId?)`
  with `MaxLength = 4096` enforced fail-closed, and
  `EntityRecordRegionReadResult` (bytes + replay time ONLY — no absolute
  address, process id, or module base ever leaves the coordinator). New
  `IGameMemoryScanner.ReadEntityRegionAsync` member.
- **Coordinator** (`GameSessionCoordinator.ReadEntityRegionAsync`): mirrors
  the proven position-read path — gate check → exact-build identity check →
  guarded reader lease → entity address resolution via
  `ResolveEntityPositionAddressAsync` → replay-clock label + same-decoded
  clock attestation (one `IReplayClockSource.GetSnapshotAsync` call;
  `EstimatedReplayTime` → `ReplayTimeSeconds`, ≤ 2 s uncertainty bound) →
  guarded `ReadAsync` of the clamped region → bytes + replay time only.
  Unresolved entities return null bytes with the failure stage, never a
  partial read.
- **Web** — `POST /api/v1/game/discover/entity-region` returns the region as
  base64 + replay time; 400 on gate failure / invalid length; serialized
  JSON never contains an address.

### Tests (8 new)

- Coordinator: exact-build returns bytes only (read lands at the resolved
  ring-record address 0x25000038, length forwarded); with-session-id attests
  clock and labels replay time; invalid length (0 and 5000) fails closed
  before the gate; missing offline gate never creates the reader;
  unsupported build returns UnsupportedBuild with null bytes; unresolved
  entity returns null bytes and never fires the region read.
- Web: success projects base64 bytes + replay time with no address leak;
  failure returns 400.

## Files changed

- `src/WotBTreader.Application/Game/GameSessionContracts.cs` — contracts +
  interface member.
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` —
  `ReadEntityRegionAsync`.
- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` —
  request/response DTOs.
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — route + handler.
- Tests: `GameSessionCoordinatorTests.cs` (6), `GameApiEndpointsTests.cs` (2).
- Docs: roadmap L0 row ✅, groundwork live-plan section updated to reflect
  the implemented seam.

## Validation

- `dotnet build WotBTreader.sln` — 0 warnings, 0 errors.
- GameIntegration tests: 269 passed (incl. 6 new). Web tests: 130 passed
  (incl. 2 new).
- `python scripts/python/offline_check.py --refresh` — links 0 broken,
  blockers contiguous, ledger consistent.
- `scripts/validate.ps1` (check-schema) — PASS.

## Assumptions / unknowns

- The region read is exercised only against the LIVE game behind the
  approval gate — this commit is the offline-tested seam, not a live run.
  The existing poll path is untouched.
- `EntityIdentityRevalidated`/`ConsistentDoubleRead` are not claimable from
  the address path (it does not double-collect position bytes) — the region
  result deliberately leaves them false.

## Recommended next steps

1. The session drivers (`invoke-hp-diffing-session.ps1` etc.) can now be
   wired to `/discover/entity-region` as their dump acquisition seam.
2. L1 (HP) is the natural first gated session once approved — the victim is
   already qualified and the dump schedule pre-staged.
3. L2 (facing) plan is pre-staged (see `record-diffing-groundwork.md`) with
   the O5 yaw-delta rehearsal target.
