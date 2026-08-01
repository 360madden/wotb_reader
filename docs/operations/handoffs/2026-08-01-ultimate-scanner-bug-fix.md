# Session handoff — 2026-08-01: Ultimate Scanner lifecycle hardening

**Author:** Codex Agent
**Branch:** `main`
**Baseline before commit:** `877dfcb` (`docs(operations): add scanner external access handoff`)
**Working tree before commit:** nine related modified source/test files; this handoff is part of the same change set.

## What changed

This session performed an adversarial review of the Ultimate Scanner integration and its surrounding launch, host, and overlay paths. Confirmed defects were fixed rather than treating review findings as documentation-only concerns.

### Changed files and contracts

| File | Scope |
|------|-------|
| `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` | Pointer-chain and exclusive snapshot-bound contract documentation |
| `src/WotBTreader.Application/Game/GameSessionContracts.cs` | Exclusive snapshot-bound contract documentation |
| `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` | Launch lease ownership, fail-closed lifecycle, and post-handoff cleanup |
| `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` | Equal exclusive-bound request validation |
| `src/WotBTreader.Overlay/Services/TelemetryStreamService.cs` | `ITelemetryStreamService : IAsyncDisposable`, serialized connection lifecycle, and cancellation |
| `src/WotBTreader.Overlay/ViewModels/MainViewModel.cs` | Detail-load generations, host replacement, UI marshalling, and safe stream startup |
| `ultimate-scanner/MemoryScanEngine.cs` | Exclusive-bound engine validation and error messaging |
| `tests/WotBTreader.GameIntegration.Tests/UltimateScannerUnitTests.cs` | Equal-bound scanner regression test |
| `tests/WotBTreader.Overlay.Tests/MainViewModelTests.cs` | Async-disposable telemetry test double |
| `docs/operations/handoffs/2026-08-01-ultimate-scanner-bug-fix.md` | This handoff |

Public contract changes are intentionally limited to `ITelemetryStreamService` gaining
`IAsyncDisposable` and the documented exclusive `MaxAddress` snapshot semantics.

### Scanner and contracts

- Documented pointer-chain traversal semantics: each configured offset is added before dereferencing the pointer at that address.
- Documented `MaxAddress` as an exclusive upper bound; zero continues to mean no explicit upper bound.
- Rejected equal nonzero snapshot bounds consistently in the HTTP validation and scanner engine.
- Added a regression test for equal exclusive address bounds.

### Managed launch and authorization lifecycle

- Removed duplicate lease disposal in artifact, suspended-process, correlation-failure, and resume-failure branches.
- Hardened post-handoff failure cleanup so a committed launch is detached, terminated, monitored, and disposed without leaving orphaned state.
- Made process absence, stale evidence, identity changes, monitor failure, and denial fail closed by terminating the managed child when required.
- Preserved deferred authorization CTS cleanup semantics where in-flight scan admission can still hold a linked token.

### Overlay and SignalR lifecycle

- Added detail-load generation invalidation and owned CTS cleanup so replaced selections, deselection, host changes, and stale minimap requests cannot publish old data.
- Cleared old host detail state during client replacement and reloads retained selections after a successful host change, including same-session-ID cases.
- Marshalled map-boundary retry/apply state and live observation timeout updates through the UI synchronization context.
- Added deterministic asynchronous SignalR disposal, callback detachment, serialized connection replacement, and cancellation of stalled connection negotiation.
- Observed optional SignalR startup failures so polling remains authoritative without unobserved task faults.
- Added `IAsyncDisposable` to the telemetry stream contract and updated test doubles.

## Validation

The following checks passed after the final edits:

- `dotnet build WotBTreader.sln -c Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test WotBTreader.sln -c Release --no-build` — 468 passed, 0 failed, 2 opt-in skips.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore` — passed.
- `git diff --check` — passed.
- Focused adversarial review — no concrete remaining defect identified in the reviewed lifecycle, UI, stream, launch, or scanner paths.

## Assumptions and remaining unknowns

- The scanner remains an evidence-collection tool. Candidate offsets do not authorize runtime telemetry reads, and no offset was promoted to `Verified` here.
- No live game process or private replay was attached during validation. Real `ReadProcessMemory` behavior and the complete launch-to-scan path still require an approved offline replay smoke test.
- There is no deterministic transport seam yet for a unit test that blocks `HubConnection.StartAsync`; the implementation now owns and cancels that negotiation path, but the stalled-transport scenario is not directly simulated by the test suite.
- Pointer-chain semantics are documented and bounded, but a synthetic native-memory traversal test remains a useful future addition.

## Integration risks

- The current working tree also contains the pre-existing repository harness configuration and research changes outside this change set only if they appear separately in `git status`; do not stage unrelated files when applying future commits.
- Scanner results remain candidates until independently corroborated across launches/replays and checked against the exact executable identity.
- Future host or overlay changes must preserve UI-thread ownership for observable collections and must not reintroduce fire-and-forget transport tasks without observing failures.

## Recommended next steps

1. Run the approved offline replay smoke test through CLI/HTTP discovery against the intended game build.
2. Add a controllable SignalR transport seam and regression test for disposal during stalled negotiation.
3. Add synthetic pointer-chain traversal coverage using a fake authorized reader.
4. Continue multi-scan filtering and cross-launch evidence corroboration before any runtime offset promotion.
