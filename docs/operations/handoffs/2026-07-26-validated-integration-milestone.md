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

## Amendment — U2 CLI entry point and first working vertical slice (`2026-07-26T22:29:30Z`)

`src/WotBTreader.Host.Cli/Program.cs` was still the project template's
`Console.WriteLine("Hello, World!")`. The whole CLI surface — invocation
parsing, the router with `doctor`/`import`/`inspect`/`reprocess`/`sessions`,
envelope rendering, and exit-code mapping — was unreachable dead code with no
coverage beyond two isolated unit tests.

Changes:

- Added `CliEntryPoint.RunAsync`, which parses arguments, composes a host,
  starts it (so migrations run), dispatches through `CliCommandRouter`, writes
  the envelope, and shuts down. `Program.cs` is now only argument forwarding
  plus a `Console.CancelKeyPress` handler that cancels cooperatively so
  shutdown and the error envelope still run.
- Added `CliExitCode.Cancelled` and mapped error codes containing `cancelled`
  onto it, so user interruption is distinguishable from internal failure.
- Chose `Host.CreateEmptyApplicationBuilder` deliberately: the default host
  logging providers write to stdout and would corrupt the machine-readable
  envelope. `TreaderLogging` is added explicitly, and its console sink is
  already configured with `standardErrorFromLevel: Verbose`, so every log
  event goes to stderr and stdout carries only the envelope.
- Output routing rule: JSON always goes to stdout so a failed command stays
  pipeable; human-readable failures go to stderr.
- The catch-all surfaces only the exception *type* name, never its message,
  because exception text routinely embeds local paths.

Guard added: `tests/WotBTreader.Host.Cli.Tests/CliEntryPointTests.cs` runs the
full path in-process against a temporary data root and parses stdout as JSON,
which also proves no log line leaks onto stdout.

Real-process smoke test (the item this handoff previously listed as pending):
`doctor --json`, `sessions --json`, and `sessions` in human mode all exit zero
against a temporary data root. `doctor` reported five checks passing, including
`installed-game-metadata`, which discovered and version-gated the local
`11.18.0.7` installation through read-only probing. No private paths, names, or
identifiers appeared in output.

Validation: `scripts/validate.ps1` exits zero; 105 tests pass (was 100), 2
opt-in skips; repository scan clean over 288 tracked files.

## Amendment — U3 loopback trust boundary now has tests (`2026-07-26T22:33:26Z`)

`WotBTreader.Host.Web` had no test project at all, so the entire local trust
boundary — the loopback gate, the capability lease, and the mutation
middleware — shipped unverified. Added `tests/WotBTreader.Host.Web.Tests` with
29 cases and `InternalsVisibleTo` on the web host.

Covered: loopback host recognition including `localhost`, `127.0.0.0/8`, and
both `::1` spellings; rejection of lookalike hosts such as
`localhost.example.com` and `127.0.0.1.example.com`; rejection of remote
addresses, of DNS-rebinding Host headers arriving on the loopback socket, of
another local site's origin on a different port, and of non-HTTP origins;
capability token entropy, URL-safety, rotation, expiry, and constant-time
comparison behavior.

One hypothesis was disproved rather than fixed: bracketed IPv6 hosts
(`[::1]`), which is the form `HostString.Host` yields, were expected to fail
`IPAddress.TryParse`. They parse correctly on .NET 10, so no change was needed
and the behavior is now pinned by a test.

Still open from this audit, carried into the next unit: the architecture
overview promises the rendezvous file has "owner-only permissions," but
`RendezvousPublisher` writes it with `FileShare.None` (a sharing mode, not an
ACL) and `LocalApplicationPaths.EnsureDirectoriesExist` calls plain
`Directory.CreateDirectory`. The file carries a live mutation capability. The
default `%LOCALAPPDATA%` location is user-private so the shipped default is
safe, but a custom `Paths:ApplicationDataRoot` would inherit whatever the
parent grants.

Validation: `scripts/validate.ps1` exits zero; 134 tests pass (was 105), 2
opt-in skips; repository scan clean over 290 tracked files.

## Amendment — U4 rendezvous capability file secured (`2026-07-26T22:36:40Z`)

Closes the gap U3 left open. `LocalApplicationPaths` now creates or re-secures
the rendezvous directory with a protected DACL granting only the current user's
SID, severing inheritance so a permissive parent cannot re-grant access, under
an `OperatingSystem.IsWindows()` guard with a `0700` equivalent elsewhere. No
new package was needed; the ACL APIs are in the shared framework and the
platform guard satisfies CA1416.

`RendezvousPublisher` re-created the directory with plain
`Directory.CreateDirectory` on every refresh, which would have restored
inherited permissions even after an out-of-band fix, so it now calls the
hardening helper. Securing the directory rather than each file also means the
temporary file the publisher writes and renames inherits the restriction, so
the token is never briefly present under weaker permissions.

Recorded as BLK-0014, along with BLK-0013 for the composition-root defect from
U1/U2.

Validation: `scripts/validate.ps1` exits zero; 135 tests pass, 2 opt-in skips;
repository scan clean over 296 tracked files.

## Amendment — web host smoke test against a running process (`2026-07-26T22:38:21Z`)

The web host had never been started as a process either. Ran the Release
executable with a temporary data root and a fixed loopback port:

- the host starts and `GET /` from loopback returns `200`;
- the same request carrying a rebinding `Host: attacker.example.com` header
  returns `403`, so `LoopbackOnlyMiddleware` behaves in a real pipeline exactly
  as the unit tests assert;
- exactly one rendezvous record is published and it contains the capability
  field;
- the record's access control lists a single identity, the current user.

On the last point, the file itself reports `AreAccessRulesProtected == False`
because it *inherits* its entry from the hardened directory rather than
carrying a protected list of its own. That is the intended design: inheritance
is severed at the directory, and the single inherited entry is the current
user, so the effective access is owner-only. The Bootstrap unit test asserts
the protection flag on the directory, which is where it belongs.

This closes the "web host not smoke-run" unknown recorded earlier in this
document. No code changed for this amendment.

## Amendment — U6 game harness safety policy covered (`2026-07-26T22:40:50Z`)

`HarnessSafetyPolicy` decides whether the harness may touch a running game and
had no tests. Added `tools/tests/WotBTreader.GameHarness.Tests` (a new tree,
mirroring `tools/src`) with 26 cases that pin every denial path by its stable
code, plus the two permit paths.

The safety-critical case is explicit: `OnlineBattle` is denied, and
`Ambiguous`, `Unknown`, `NotRunning`, `LaunchPending`, and
`OfflineReplayStopped` are denied identically, so only a positively verified
offline replay passes. Also covered: process identity by path, version, and
hash; the refusal to accept truncated hashes even when both sides match;
background windows; unknown or unequal integrity levels; evidence from an
unapproved source; stale *and* future-dated evidence; evidence belonging to
another process or an uncorrelated launch; and the arming rules for identity,
expiry, not-yet-valid windows, and the two-minute maximum lifetime.

Also extended `scripts/scan-repository.ps1` to include `tools/tests` in the
hidden-source probe, so the new tree is covered by the BLK-0012 guard.

Validation: `scripts/validate.ps1` exits zero; 161 tests pass, 2 opt-in skips;
repository scan clean.

## Recommended next steps

1. Author the dashboard query/endpoint services and re-register them in the
   web composition; add UI pages consuming the telemetry hub.
2. Add a GameHarness test project covering the safety policy denial paths.
3. Run the opt-in private replay compatibility pass locally and record the
   outcome in the replay decoder blocker record.
4. Smoke-run the web host and CLI, then implement the overlay against the
   loopback API.

## Amendment — superseding status and next steps (`2026-07-26T22:41:57Z`)

The four-item list directly above is historical and no longer accurate. Items 2
and 4 were completed in the amendments recorded earlier in this document. This
section replaces it and is the authoritative state at the end of this session.

### Repository state

`main`, working tree clean, never pushed. Six commits were added this session,
each independently validated before it was made:

- `9860142` register every adapter in the composition root
- `89771b4` run the CLI end to end from a composed host
- `db97a53` cover the loopback trust boundary
- `e6f8c47` give the rendezvous capability file owner-only permissions
- `713b270` record web host process smoke test evidence
- `464909f` pin every game harness safety denial path

Test count went from 95 to 161 with 2 opt-in skips. `scripts/validate.ps1`
exits zero.

### What now actually works

Both hosts start and do real work. The CLI executes `doctor`, `sessions`,
`import`, `inspect`, and `reprocess` against a composed container with storage
migrated at startup; `compare`, `export`, `watch`, and `serve` remain reserved
and return `UnsupportedCapability` by design. The web host serves on loopback,
rejects rebinding hosts, and publishes an owner-only rendezvous record.

### Honest gaps

- Only `doctor` and `sessions` were exercised against a real process. `import`,
  `inspect`, and `reprocess` are wired and unit-tested but have never run
  against an actual replay file, so the decode-to-storage path is unproven end
  to end.
- The dashboard does not exist. There are no query services, no API endpoints,
  and no pages beyond the Blazor template; the previously removed
  `DashboardQueryService` and `MinimapProjector` registrations were never
  reinstated because the types were never written.
- The overlay is still the WPF project template.
- The private replay compatibility pass has still not been run. It needs local
  replays and explicit opt-in.

### Integration risk worth knowing before building a client

`TelemetryHub` is mapped at `/api/v1/stream`, which sits under the path prefix
`MutationProtectionMiddleware` guards. SignalR's negotiate step is a `POST`, so
it will be required to carry both the capability header and an antiforgery
token. No client exercises this today, so the interaction is untested; whoever
builds the first hub client should expect to handle it, or the hub path should
be deliberately exempted.

### Recommended next steps

1. ~~Prove the decode path end to end.~~ Done; see U7 below.
2. ~~Author the dashboard read API.~~ Done; see U8 below.
3. ~~Build the UI pages against that API~~ (read UI done; hub/mutation
   interaction still deferred — see U9).
4. Run the opt-in private replay compatibility pass and record the outcome in
   the replay decoder blocker record.
5. Implement the overlay last, against the loopback contract only.

## Amendment — U7 decode path proven end to end (`2026-07-26T22:55:26Z`)

Closes recommended step 1. The import path was wired and unit-tested but had
never carried a replay archive from disk through decode into storage and back
out through the query commands. It does now, and the pass needs no private
game files.

`SyntheticReplayFactory` moved from `tests/WotBTreader.Replays.Tests` into a new
`tests/WotBTreader.TestSupport` library so both the decoder tests and the CLI
tests can build the same fixtures. `WotBTreader.Replays` grants it
`InternalsVisibleTo` because fixtures are generated from the real format
constants and must not be able to drift from the decoder they exercise. The
three existing consumers needed only an added namespace import; all 18 decoder
tests still pass unchanged.

`CliReplayIngestionTests` adds four cases through the real `CliEntryPoint`:

- `import` of a synthetic archive succeeds, decodes a non-zero participant and
  position count, `sessions` then reports exactly one session, and `inspect`
  returns the same decode run that `import` created;
- importing identical bytes twice reuses the content-addressed artifact but
  still creates a second, distinct decode run, which is the evidence-first
  reprocessing rule stated in the architecture overview;
- a file with a valid extension but corrupt contents is rejected with a
  non-zero exit code and at least one stable error code;
- `inspect` of an unknown decode run identifier fails rather than inventing an
  empty result.

Finding recorded, not fixed: the envelope renders identifiers as nested objects
(`"id": { "value": "..." }`) because the `Core` identifier types are
`readonly record struct` wrappers with no JSON converter. That is awkward for
scripting against the CLI. It was left alone deliberately because adding
converters would also change the NDJSON capture format documented in
`docs/formats/telemetry-capture-ndjson-v1.md`, which needs a versioning
decision rather than a drive-by change.

Validation: `scripts/validate.ps1` exits zero; 165 tests pass, 2 opt-in skips;
repository scan clean over 300 tracked files.

## Amendment — U8 dashboard read API (`2026-07-26T23:07:00Z`)

Closes recommended step 2. The web host now exposes a read-only loopback JSON
API over the existing storage query ports, with dedicated wire DTOs so clients
do not inherit the domain identifier wrappers.

Routes (all GET, so mutation middleware does not require a capability):

- `/api/v1/doctor`
- `/api/v1/sessions` (offset/limit paging; default 50, max 200)
- `/api/v1/sessions/{battleSessionId}`
- `/api/v1/decode-runs/{decodeRunId}`

Contract choices: identifiers are plain GUID strings; `AccountId` is never
exposed; bot status and confidence are passed through and never inferred;
capability flags expand to names; position series are capped at 5000 samples
with `positionsTruncated` / `totalPositionCount` when a battle exceeds that.
Application error codes map to stable HTTP statuses (404/409/400/501/500).

`ReadApiEndpointsTests` covers paging bounds, truncation, error mapping, and
the privacy/bot-status rules. A process smoke against the real host confirmed
empty sessions, over-limit 400 (problem+json), unknown 404, doctor 200, and
forged-Host 403.

Also hardens `TemporaryDataRoot` cleanup: Serilog's file sink can briefly
outlive host disposal, so directory deletion is retried then abandoned rather
than failing an otherwise passing CLI assertion.

Validation: `scripts/validate.ps1` exits zero; 183 tests pass, 2 opt-in skips;
repository scan clean over 304 tracked files.

## Amendment — U9 dashboard read UI (`2026-07-26T23:14:00Z`)

Delivers the Grok-safe half of recommended step 3: Blazor pages over the U8
wire DTOs, without touching SignalR or mutation middleware.

`IDashboardReadClient` / `DashboardReadClient` map storage/doctor ports into
the same `Contracts` types the HTTP API returns, including the 5000-sample
position cap. Pages: sessions list at `/`, session detail at
`/sessions/{id}`, diagnostics at `/diagnostics`, overlay stub at
`/overlay`. Template Weather/Counter pages removed. Nav links already pointed
here; they now resolve.

Hub negotiate still requires capability + antiforgery under
`MutationProtectionMiddleware`; left deferred on purpose.

Plan: `docs/superpowers/plans/2026-07-26-dashboard-read-ui.md`.

Validation: `scripts/validate.ps1` exits zero; 188 tests pass, 2 opt-in skips.

## Amendment — U10 token-lean Cursor harness (`2026-07-26T23:51:27Z`)

Installed a progressive-disclosure Cursor control plane without changing product code: thin `AGENTS.md` router, two always-on rules (safety + session budget), glob rules for architecture/binary, four focused subagents with pinned models (Grok glue, Composer verifier, Opus decoder, Fable security), three on-demand skills (validate / handoff / commit), `.cursor/reference` catalogs, and `.cursorignore` for build noise. Goal is lower default tokens and correct model routing.
