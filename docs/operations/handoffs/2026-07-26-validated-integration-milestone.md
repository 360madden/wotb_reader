# Handoff — validated integration milestone

Written: `2026-07-26T22:05:17Z`
Author: lead agent session (post-gate validation and integration)

## Repository state

- Branch `main`, head commit `b6d6e1a`
  (`feat: integrate replay decoding, capture, storage, and host surfaces`),
  207 files changed on top of scaffold commit `6818b40`.
- Working tree clean. Nothing staged, nothing stashed, never pushed.
- Commits are authored locally as `Codex Agent <codex@local.invalid>` per the
  working agreements.

## What this session did

The previous session ended under BLK-0007, an external command-execution gate
that blocked every build, test, and commit, leaving the entire multi-agent
integration uncommitted and unvalidated. This session confirmed the gate was
lifted, executed the pending-validation ledger, repaired everything that had
never compiled, and committed the validated milestone.

1. Ran `scripts/validate.ps1 -AuditPackages` and fixed failures until the full
   pipeline passed end to end.
2. Fixed eleven compile/analyzer errors in post-gate source (details in the
   BLK-0007 resolution amendment in
   `docs/operations/blockers/2026-07-26-command-execution-gate.md`).
3. Fixed a real `DiagnosticBundleService` defect exposed by its test: zip
   entry streams stayed open across entry creation and the archive stream was
   still open during the atomic rename, so every bundle export failed.
4. Resolved BLK-0012 (`docs/operations/blocker-log.md`): `diagnostics/` and
   `dist/` ignore patterns hid `Diagnostics/` source folders and the
   `wwwroot/lib` static assets on the case-insensitive Windows worktree.
   Added targeted unignore rules and extended `scripts/scan-repository.ps1`
   to fail when any ignored file exists under a source tree.
5. Amended the blocker records with dated resolutions and committed the
   milestone as one commit, since the work was validated as a whole and
   intermediate subsets would not build.

## Changed public contracts

- `TreaderLogging.CreateBootstrapLogger` now returns
  `Serilog.Extensions.Hosting.ReloadableLogger` (the framework type actually
  produced); it still has no callers.
- The web composition (`WebSurfaceServiceCollectionExtensions`, `Program.cs`)
  no longer references `DashboardQueryService`, `MinimapProjector`, or
  `MapWebApi`; those types were never authored. Re-add the registrations when
  the dashboard surface is implemented.
- The architecture dependency test identifies the Replays and Storage
  assemblies through `WotbReplayDecoder` and `SqliteStorageOptions` instead of
  deleted marker classes.
- `TelemetryHub` reads `TelemetryStreamMessage.BattleSessionId` (the actual
  contract property).
- `GameHarnessService` guarded timeouts use the wall clock;
  `CancellationTokenSource.CancelAfter` has no `TimeProvider` overload, and
  guarded timeouts are bounded to two minutes.

## Validation evidence (all from this session, against `b6d6e1a` sources)

- `scripts/validate.ps1 -AuditPackages` exits zero: locked restore, format
  verification, Release build (0 warnings, 0 errors), full test suite,
  vulnerability audit, repository scan (284 tracked files).
- Tests: 95 passed, 0 failed, 2 skipped (opt-in installed-game tests) across
  9 test projects.
- `dotnet list ... --vulnerable --include-transitive`: no vulnerable packages
  in any project.
- BLK-0006 guardrail observed working in both directions: failing phases
  terminated the script non-zero before later phases; the clean run exited
  zero.

## Assumptions

- Removing the unregistered dashboard types is correct because no other code
  referenced them; the pending ledger already listed the dashboard as
  unfinished.
- The Blazor template's `wwwroot/lib/bootstrap` assets belong in the
  repository because `App.razor` references them and a fresh clone would
  otherwise render unstyled.

## Unknowns

- The repeat compatibility pass over the opt-in private replay directory has
  not been run this session; it requires the user's local private replays and
  explicit opt-in. Decoder changes since the last pass are limited to the EOF
  sentinel and nullability fixes, both covered by synthetic tests.
- The web host and CLI have not been smoke-run as processes; only their unit
  tests and composition were validated.

## Integration risks

- The dashboard endpoint surface (`/api/v1` queries, minimap projection) does
  not exist; the SignalR hub streams telemetry but no UI consumes it. The
  Blazor pages are still template placeholders (`Counter`, `Weather`).
- The Overlay project is still the scaffold and must remain a loopback web
  client without parser or storage references when implemented.
- The GameHarness has no test project; its safety policy is enforced by code
  review and the audit trail, not by automated tests.

## Amendment — U1 composition root wired (`2026-07-26T22:24:36Z`)

The "recommended next steps" below were written before verifying that the
committed milestone could actually start. It could not. Two defects made the
product unrunnable while every unit test stayed green, because no test built
the real container or started a host.

- `AddWotBTreaderFoundation` registered only paths, application services, and
  the doctor/bundle pair. It never called `AddSqliteStorage`,
  `AddReplayDecoding`, `AddCaptureLogs`, or `AddGameIntegration`, although
  `Bootstrap.csproj` already referenced all four and every extension method
  existed and was correct. `IReplayIngestionService` resolved but none of its
  ports did, and `TreaderBootstrapOptions.GameRoot`/`GameUserDataRoot` were
  accepted and silently discarded.
- `SequencedTelemetryEventPublisher` exposed an all-optional
  `(int = 4096, int = 512)` constructor alongside `(TimeProvider)`. The DI
  activator considers those ambiguous and refuses to construct the type, so
  the telemetry publisher and therefore the SignalR hub could never have been
  activated in any host.

Changes: registered the four adapters plus `AddLogging` in the foundation;
mapped the bootstrap game roots onto `GameIntegrationOptions`; derived
`SqliteStorageOptions` from `LocalApplicationPaths` so Bootstrap owns the
on-disk layout and the adapter cannot drift from what the doctor reports;
removed the ambiguous constructor defaults; and added
`StorageInitializationHostedService`, which applies migrations at host start
and treats failure as fatal.

Guard added: `tests/WotBTreader.Bootstrap.Tests/CompositionRootTests.cs`
builds the real container with `ValidateOnBuild` and `ValidateScopes`,
resolves all 18 published ports, asserts a strict decoder is registered, and
starts an actual `IHost` to prove the schema migrates. Written first and
observed failing on both defects before the fix.

Validation: `scripts/validate.ps1` exits zero; 100 tests pass (was 95), 2
opt-in skips; repository scan clean over 286 tracked files.

Note for the next session: the storage adapter's own default database path is
`data/wotbtreader.sqlite3`, but Bootstrap now overrides it to
`LocalApplicationPaths.Database` (`treader.db` under the data root). Bootstrap
is the single source of truth for layout.

## Recommended next steps

1. Author the dashboard query/endpoint services and re-register them in the
   web composition; add UI pages consuming the telemetry hub.
2. Add a GameHarness test project covering the safety policy denial paths.
3. Run the opt-in private replay compatibility pass locally and record the
   outcome in the replay decoder blocker record.
4. Smoke-run the web host and CLI, then implement the overlay against the
   loopback API.
