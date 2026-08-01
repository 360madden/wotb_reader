# Session handoff — 2026-08-01: Ultimate Scanner external access

**Status:** implementation and validation complete; handoff is ready to commit and push.
**Branch:** `main`
**Implementation commit:** `7660a85` (`feat(scanner): expose hardened CLI and loopback HTTP access`)
**Working tree before this handoff:** clean
**Validation:** Release build passed; full solution tests passed with 467 passed, 0 failed, and 2 opt-in skips; format verification and `git diff --check` passed.

---

## What was accomplished

The Ultimate Scanner can now be used by the existing GameHarness CLI and by
other local programs through the loopback web host. Both access paths flow
through the existing evidence-backed coordinator instead of opening process
handles directly.

### External interfaces

- Added capability-authenticated scanner operations under `/api/v1/game`:
  typed value scan, AOB/pattern scan, pointer-chain evidence probe, snapshot,
  snapshot comparison, neighborhood scan, and snapshot-session discard.
- Added public request/response contracts for snapshot, compare, neighborhood,
  and discard operations.
- Updated GameHarness commands to discover the host through the rendezvous file,
  validate the short-lived capability, send
  `X-WotBTreader-Capability` on unsafe requests, and return bounded operator
  output with non-zero failure codes.
- Documented the HTTP route matrix, rendezvous requirements, CLI syntax, and
  integration examples in the offset-discovery guides.

### Correctness and security hardening

- Added on-demand trusted module-base resolution through a dedicated adapter;
  transient module enumeration failure denies only that operation and permits a
  later retry.
- Bound scanner observations and retained snapshot identity to the coordinator
  authorization generation, preventing stale authorization reuse.
- Propagated cancellation through module resolution, scanner loops, guarded
  process reads, and lifecycle revocation.
- Added a read-admission gate so reads admitted after revocation do not enter
  the native memory API.
- Hardened rendezvous validation: required schema, unexpired lease, live
  publisher process, literal loopback IP, HTTP(S) scheme, valid port, and no URI
  user information.
- Added strict CLI option parsing, numeric range validation, response disposal,
  and API validation for inverted or non-finite ranges.

## Changed files and contracts

- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` — public scanner
  request/response DTOs.
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` — scanner routes and
  request validation.
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` — scanner
  authorization, cancellation, generation binding, and live module resolution.
- `src/WotBTreader.GameIntegration/Session/GameProcessModuleBaseAddressResolver.cs`
  — new Windows module-base adapter.
- `ultimate-scanner/GuardedMemoryReader.cs`, `MemoryScanDiscoverer.cs`, and
  `MemoryScanEngine.cs` — guarded reads, cancellation, identity binding, and
  validation.
- `tools/src/WotBTreader.GameHarness/Program.cs` and
  `RendezvousConnection.cs` — CLI transport and capability validation.
- `tests/` — coordinator, scanner, API contract, middleware, and CLI containment
  coverage.
- `docs/operations/offset-discovery-guide.md`,
  `docs/operations/offset-discovery-workflow.md`, and
  `offline/offset-discovery.md` — operator and architecture documentation.

## Assumptions and remaining unknowns

- The scanner remains evidence-only. Candidate results do not promote offsets
  into the runtime table; runtime reads still require explicitly verified fields
  and matching process identity evidence.
- The real installed-game/replay smoke path has not been run in this session.
  Live Windows module resolution and `ReadProcessMemory` behavior therefore
  remain to be confirmed against the supported game installation.
- No public network listener is intended. The web host remains loopback-only,
  and the CLI is designed for local programs on the same machine.
- A native read already admitted before revocation may finish; cancellation
  cannot interrupt a synchronous native call already in progress.

## Integration risks

1. The coordinator must reach `OfflineReplayVerified` before scanner operations
   can proceed. A stale, ambiguous, or timed-out launch must remain denied.
2. The rendezvous capability is short-lived and rotates with publication;
   clients should reread the record after a `401` rather than cache it.
3. Scanner results are bounded and may be truncated. Consumers must inspect the
   truncation and candidate-count metadata rather than assuming completeness.
4. Windows-only native behavior, process architecture, module enumeration, and
   access rights can still make a scan unavailable even when the API is healthy.

## Recommended next steps

1. Run the documented local smoke sequence: start the web host, launch a managed
   offline replay, wait for `OfflineReplayVerified`, then exercise one CLI scan
   and one HTTP scan against the supported Windows installation.
2. Capture bounded structured logs for module resolution, gate transitions,
   cancellation, and scan completion without recording credentials or private
   replay data.
3. Use multi-scan filtering and replay-state changes to reduce candidate counts
   before considering any evidence promotion.
4. Keep offset promotion blocked until exact executable identity and required
   dynamic evidence are recorded and reviewed.
