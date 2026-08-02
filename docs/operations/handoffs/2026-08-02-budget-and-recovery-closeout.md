# Session handoff — 2026-08-02: budget closeout and blocker resolutions

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `40fb6b5` (`fix(game): require real managed replay windows`)

**Commit unit:** append-only blocker-log amendments (BLK-0023/BLK-0024 resolutions,
new BLK-0025), the OD-RECOVERY-003-BOUNDED ledger decision, a privacy-safe
`MaxBytes` retained-byte budget threaded through the scanner engine, contracts,
web endpoint, and GameHarness CLI (including `discover-campaign`), post-verification
exact-window-loss regression coverage, and the workflow/guide/offline-pack sync.
Nothing is committed yet; the worktree holds all changes locally and was not pushed.

## Outcome

The handoff's open documentation items are closed and the remaining
privacy-safe scanner gap from the last live trial is implemented.

- **BLK-0023** (hidden window / fabricated window evidence) is amended with its
  live resolution: normal visible launch, real nonzero window evidence,
  exact-PID `EnumWindows` observation, and repeated `OfflineReplayVerified`
  proof. The stale `SDL_app` restriction and the intermediate desktop-enumeration
  cap are recorded as the new immutable **BLK-0025** with the exact-PID
  correction and its fail-closed behavior.
- **BLK-0024** (lifecycle startup timeout) is amended with its live resolution:
  the research-only `Research:LifecycleEvidenceTimeoutSeconds` (5–300 s) was
  live-proven at 120 s while the 45 s production default stayed unchanged.
- **Ledger decision:** the zero-count bounded low-address trial is recorded as
  **OD-RECOVERY-003-BOUNDED** (`NoSignal`, negative setup evidence for
  OD-RECOVERY-004). The interrupted populated-slice selection attempt is
  explicitly **not** classified as scan evidence. The workflow next-session
  protocol now targets **OD-RECOVERY-004** with operator availability, the
  research lease, and the byte budget.

## Implementation in the worktree

- `MemoryScanEngine.SnapshotFilter` gains `MaxBytes`; `CreateSnapshot` resolves
  a per-snapshot budget with `ResolveSnapshotByteBudget` (explicit budget never
  exceeds the fixed 512 MiB ceiling; zero means the ceiling). `ValidateFilter`
  rejects negative budgets and budgets above the ceiling.
- `MemorySnapshotRequest` (Application) and `OffsetSnapshotRequest`
  (ApiContracts, serialization-only) carry `MaxBytes`; `OffsetSnapshotRequest`
  exposes the public `MaximumSnapshotBytes` ceiling constant. The web endpoint
  validates `MaxBytes` against both bounds and forwards it to the scanner.
- `GameSessionCoordinator.CreateSnapshotAsync` passes the budget through.
- GameHarness `discover-snapshot` accepts `--max-bytes`; `discover-campaign`
  accepts `--max-bytes` (0–512 MiB) and forwards it in the snapshot request,
  keeping output aggregate-only and discarding the scanner session on every
  completed path.
- Regression coverage added: budget validation and ceiling clamp at the engine
  level, endpoint negative/over-ceiling rejection and forwarding, campaign
  parsing/forwarding, post-verification exact-window loss and owner-change
  revocation in the coordinator, and a full `IsWindowObservationTerminalFailure`
  policy matrix (waits before verification, fails closed after).

## Live defects / reviewer findings addressed

1. The endpoint initially validated only the negative budget; the over-ceiling
   case was rejected later by the engine with a different error code. The
   ceiling is now public on the contract and validated at the endpoint too.
2. The ledger header date was stale, a `defision` typo existed, and the workflow
   still named `OD-RECOVERY-003` as the next session after the ledger moved to
   `OD-RECOVERY-004`; all fixed.
3. Budget enforcement was only covered by filter validation; `ResolveSnapshotByteBudget`
   is now directly unit-tested at the engine level.

## Validation performed

- `dotnet restore --locked-mode` + Release build: 0 warnings, 0 errors.
- GameHarness.Tests: 37 passed. Host.Web.Tests: 89 passed. GameIntegration
  focused (coordinator + scanner units): 75 passed.
- Full `scripts/validate.ps1`: locked restore, format, Release build with 0
  warnings/errors, complete test suite (520 passed, 2 expected local opt-in
  skips, 522 total), repository scan, offline pack freshness
  (`offline_check.py --check-fresh`, 12 files / 70 links), all green.
- `git diff --check` clean at handoff time.

## Uncommitted files

- `docs/operations/blocker-log.md` (BLK-0023/BLK-0024 amendments, new BLK-0025)
- `docs/operations/offset-discovery-ledger.md` (OD-RECOVERY-003-BOUNDED entry)
- `docs/operations/offset-discovery-workflow.md` (OD-RECOVERY-004 next-session protocol)
- `docs/operations/offset-discovery-guide.md` (campaign/snapshot `--max-bytes` CLI matrix)
- `offline/api-surface.md`, `offline/offset-discovery.md` (budget surface)
- `src/WotBTreader.ApiContracts/OffsetDiscoveryContracts.cs` (shared contract: `MaxBytes` + ceiling const)
- `src/WotBTreader.Application/Game/GameSessionContracts.cs` (`MaxBytes` on `MemorySnapshotRequest`)
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` (budget pass-through)
- `src/WotBTreader.Host.Web/Endpoints/GameApiEndpoints.cs` (budget validation + forwarding)
- `ultimate-scanner/MemoryScanEngine.cs` (budget resolution + validation)
- `tools/src/WotBTreader.GameHarness/OffsetCampaign.cs`, `Program.cs` (`--max-bytes`)
- Tests: `GameSessionCoordinatorTests.cs`, `UltimateScannerUnitTests.cs`,
  `GameApiEndpointsTests.cs`, `OffsetCampaignTests.cs`
- this handoff

## Cleanup state

- No game process or web host was started; no scanner session was created.
- No replay bytes, addresses, values, or scanner-session identifiers were
  written to the repository.

## Next move

1. Run the next live trial as **OD-RECOVERY-004** with operator availability:
   use the bounded research lease and the new `--max-bytes` budget, retain
   aggregate counters only, discard the scanner session. A second independent
   replay remains required before promotion (BLK-0019).
2. Optionally wire `--max-bytes` into `OffsetCampaign` internally as a
   bounded-slice selector so a future campaign can page populated private/mapped
   regions without exposing process-specific addresses.
3. Commit this checkpoint as `Codex Agent <codex@local.invalid>` when the owner
   asks; do not push unless requested.
