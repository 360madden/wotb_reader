# Major blocker log

This append-oriented record explains significant obstacles that changed the
implementation or validation path. Times are UTC. Corrections are added as new
entries rather than silently erasing prior evidence.

## BLK-0001 — Sandboxed PowerShell process creation denied

- First observed: `2026-07-26T20:46:15.238Z`
- Status: resolved for the current session
- Impact: the first read-only `git status` could not start, so repository
  inspection, scaffolding, build, and Git milestones could not proceed through
  the default command runner.
- Evidence: the runner returned Windows error 5 from
  `CreateProcessAsUserW` while starting the configured PowerShell executable.
- Cause: the workspace sandbox process token could not create the PowerShell
  child process. The repository and Git itself were healthy when invoked
  outside that runner.
- Resolution: use the product's explicit, audited elevated-command path for
  repository-local PowerShell and .NET commands. Keep every command scoped to
  `C:\work\wotb_reader`; use read-only checks before sensitive operations.
- Why: this preserves the requested workspace scope and command audit trail
  without weakening repository safety or altering unrelated machine state.
- Validation: elevated `git status` succeeded, then the untouched four-file
  baseline committed as `645ea74`.
- Prevention/follow-up: retry ordinary sandbox execution after environment
  changes. Do not generalize the workaround to destructive commands or paths
  outside the repository.

## BLK-0002 — Audited SQLite native package was vulnerable

- First observed: `2026-07-26T20:54:30.392Z`
- Status: resolved and validated
- Impact: NuGet restore failed because warnings are errors and the default
  `Microsoft.Data.Sqlite 10.0.10` graph selected
  `SQLitePCLRaw.lib.e_sqlite3 2.1.11`, which NuGet reported under high-severity
  advisory `GHSA-2m69-gcr7-jv3q`.
- Evidence: `dotnet restore` emitted `NU1903` for storage and every transitive
  host/test consumer.
- Cause: the current Microsoft package graph has a permissive transitive
  version floor that still resolved the affected native bundle.
- Resolution: centrally pin `SQLitePCLRaw.lib.e_sqlite3 2.1.12`, the patched
  line available from NuGet, while retaining `Microsoft.Data.Sqlite 10.0.10`.
  Central transitive pinning makes every consumer resolve the same native
  binary.
- Why: overriding to the compatible patched patch release removes the known
  vulnerable binary without suppressing auditing or introducing a separate
  unmanaged SQLite installation.
- Validation: restore resolved `SQLitePCLRaw.lib.e_sqlite3 2.1.12`; the release
  build and tests passed, and `dotnet list ... --vulnerable
  --include-transitive` reported no vulnerable packages for every project.
- Prevention/follow-up: CI keeps `NuGetAuditMode=all`, package versions remain
  centralized and locked, and Dependabot checks the NuGet graph monthly.

## BLK-0003 — Windows target leaked into shared composition

- First observed: `2026-07-26T20:54:30.392Z`
- Status: resolved and validated
- Impact: the portable web/CLI/bootstrap projects could not reference
  `GameIntegration` after it was initially scaffolded as
  `net10.0-windows`.
- Evidence: restore returned `NU1201` for Bootstrap, Web, CLI, and architecture
  tests.
- Cause: platform targeting was applied to the entire adapter even though only
  the WPF overlay and developer harness require Windows executable surfaces.
- Resolution: keep the integration library on `net10.0`; isolate guarded
  P/Invoke implementations with Windows platform annotations and runtime
  checks. Keep `Overlay` and `GameHarness` on `net10.0-windows`.
- Why: metadata parsing, log interpretation, and safety policy are testable
  without a Windows target, while the actual Win32 entry points remain
  explicitly guarded at their narrow boundary.
- Validation: restore and the release build passed. Architecture tests confirm
  adapter isolation; Windows-targeted test projects also built and passed on
  the local Windows host.
- Prevention/follow-up: architecture tests enforce adapter dependency
  direction, and future platform-specific code stays behind injectable ports.

## BLK-0004 — Native-log inspection conflicted with privacy boundaries

- First observed: `2026-07-26T21:04:21.3745337Z`
- Status: safe implementation path implemented and synthetically validated
- Impact: direct inspection of local WotB log lines could have accelerated
  lifecycle parsing, but even filtered output might expose private player
  identifiers, chat, tokens, or unrelated session data.
- Evidence: the integration task's privacy gate denied the read before any raw
  log content was returned, retained, or written.
- Cause: native game logs mix the small set of required replay lifecycle
  markers with unrelated, potentially sensitive free-form data.
- Resolution: implement a strict marker-only parser from already established
  lifecycle marker names and validate it with synthetic lines. Unrecognized
  text is discarded at the read boundary and is never included in output or
  application logs.
- Why: native logs are only a synchronization anchor, not a telemetry source of
  record. Reading broader content would add privacy risk without improving
  replay evidence.
- Validation: seven focused cases passed: positive offline start, three known
  non-start markers, unknown private text discarded, oversized input discarded
  before marker search, and initial watcher reconciliation emitting only
  recognized marker metadata. The full owned synthetic run passed 25 tests
  with two opt-in tests skipped; two read-only installed-game tests also
  passed. Real-log marker validation remains an explicit local opt-in.
- Prevention/follow-up: keep allowlisted markers versioned, never expose a
  generic log-tail API, and include log-content scanning in final diagnostics
  and PII review.

## BLK-0005 — Database ignore pattern hid the SQLite source project

- First observed: `2026-07-26T21:12:36.3874593Z`
- Status: resolved and validated
- Impact: new files created under `src/WotBTreader.Storage.Sqlite` and its test
  project could be silently omitted from status, review, and milestone commits.
- Evidence: `git check-ignore -v
  src/WotBTreader.Storage.Sqlite/SqliteDecodeRunRepository.cs` identified
  `.gitignore` line 48, pattern `*.sqlite`, as the matching rule.
- Cause: Git ignore patterns without a slash can match directory names as well
  as files. On the Windows worktree the comparison is case-insensitive, so the
  runtime database extension pattern also matched the `.Sqlite` project suffix.
- Resolution: keep the runtime database-file patterns but explicitly unignore
  `src/WotBTreader.Storage.Sqlite/**` and
  `tests/WotBTreader.Storage.Sqlite.Tests/**`.
- Why: explicit project exceptions preserve broad protection against accidental
  database commits while making the intended source/test trees auditable.
- Validation: `git ls-files --others --exclude-standard` exposed all 23 new
  storage source/test/lock files and zero `bin`, `obj`, or `TestResults`
  descendants. `git check-ignore -q --no-index` returned not-ignored for a
  source file and ignored for an `obj` file.
- Prevention/follow-up: add an ignore-policy regression check to repository
  scanning and verify every project has at least one tracked source file during
  final clean-state validation.

## BLK-0006 — Validation script returned success after native failures

- First observed: `2026-07-26T21:17:10.3588567Z`
- Status: guardrail fixed; clean full-solution validation pending integration
- Impact: `scripts/validate.ps1` printed restore, format, build, and compiler
  failures but continued into tests/repository scanning and returned exit code
  zero. A milestone could therefore appear validated when it was not.
- Evidence: the run contained `NU1004`, compiler, and formatting errors, then
  printed `Repository scan passed`; its shell result was success.
- Cause: PowerShell's `$ErrorActionPreference = 'Stop'` applies to PowerShell
  errors, not non-zero exit codes from native executables such as `dotnet`.
- Resolution: route every native validation command through
  `Invoke-CheckedNative`, immediately inspect `$LASTEXITCODE`, and throw with
  the failed phase and exit code. Harden the repository scanner's native Git
  calls the same way.
- Why: explicit exit-code handling is deterministic across PowerShell versions
  and preserves the readable sequential validation script.
- Validation: a deliberately stale locked restore must now terminate the script
  non-zero before later phases; after integration, the same script must run all
  phases and exit zero only when each passes.
- Prevention/follow-up: CI retains independent steps in addition to the local
  script, and future native commands must use the checked wrapper.

## BLK-0007 — Command execution blocked by the environment usage gate

- First observed: `2026-07-26T21:18:46.329Z`
- Status: externally blocked; implementation work continues without bypass
- Impact: the lead's deliberate validation-script regression run and an
  agent's final owned format/test rerun were rejected before process launch.
  Build, test, formatter, Git, and local-game commands cannot currently be
  executed through the approved command path.
- Evidence: the command tool reported that automatic approval review was
  rejected because the execution usage limit had been reached and explicitly
  prohibited retrying through a workaround. No repository command ran.
- Cause: an external Codex execution quota/approval gate, not repository code,
  test failure, sandbox policy, or user authorization.
- Resolution: do not bypass the gate. Continue reviewable source,
  documentation, test authoring, and agent integration that use supported
  non-command tools. Preserve every unexecuted validation command and run them
  through the normal approved path when it becomes available.
- Why: honoring the execution boundary protects the workspace and keeps
  validation evidence honest; a synthetic or indirect command result would not
  prove the repository.
- Validation: pending external gate availability. The first checks are the
  BLK-0006 non-zero regression, owned GameIntegration format/tests, full locked
  restore, release build, and complete test suite.
- Prevention/follow-up: keep validation phases independently reproducible and
  report this environmental gap explicitly if it remains at handoff.
- Resolution amendment (`2026-07-26T21:59:25Z`): command execution became
  available again in a later session. The full pending-validation ledger was
  executed through `scripts/validate.ps1 -AuditPackages`: locked restore,
  format verification, Release build, complete test suite (95 passed, 2 opt-in
  skipped), transitive vulnerability audit (clean), and repository scan all
  passed. Eleven compile/analyzer errors and one product bug found in
  never-compiled post-gate source were fixed first; see BLK-0012 for a hidden
  ignored-source recurrence discovered during the same run. UI/overlay smoke
  tests and the guarded game scenario remain pending because the dashboard and
  overlay surfaces are not yet implemented.
- Resolution amendment (`2026-07-27T00:00:00Z`): the overlay and dashboard
  surfaces noted as pending in the previous amendment are now fully
  implemented. The overlay (WPF, `net10.0-windows`) provides: session list
  with map/participant/position metadata, team-colored position scatter plot
  with auto-refresh, SignalR push-based session list updates via
  `TelemetryStreamService`, and an embedded Blazor dashboard tab via WebView2.
  The dashboard (`net10.0`, Blazor Server) provides: sessions table, session
  detail with participants and position counts, diagnostics doctor report, and
  an overlay status page. Both surfaces are unit-tested (38 overlay tests,
  54 web host tests) but have not been smoke-run together against a live host.
  See `docs/operations/handoffs/2026-07-27-signalr-webview2-completion.md`.

## BLK-0012 — `diagnostics/` and `dist/` ignore patterns hid tracked sources

- First observed: `2026-07-26T21:59:25Z`
- Status: resolved and validated
- Impact: `src/WotBTreader.Bootstrap/Diagnostics/` (doctor and diagnostic
  bundle services), `src/WotBTreader.Application/Diagnostics/` (their
  contracts), and the Blazor template assets under
  `src/WotBTreader.Host.Web/wwwroot/lib/bootstrap/dist/` were invisible to
  `git status`, searches that respect ignore files, and any milestone commit.
  A fresh clone would have failed to compile the Bootstrap test project and
  rendered the web host without styles.
- Evidence: `git check-ignore -v` matched `.gitignore` pattern `diagnostics/`
  for the source folders and `dist/` for the static assets. A test referencing
  `DiagnosticBundleService` compiled locally while the type's source file was
  untrackable.
- Cause: the same hazard as BLK-0005 — unanchored directory ignore patterns
  match anywhere in the tree, and Windows worktrees compare them
  case-insensitively, so runtime-data names collide with source folder names.
  The BLK-0005 prevention check only guarded a single storage sentinel path.
- Resolution: add `!src/**/Diagnostics/`(`**`) and `!src/**/wwwroot/lib/`(`**`)
  exceptions, and extend `scripts/scan-repository.ps1` to fail when any
  ignored file exists under `src`, `tests`, `tools/src`, `scripts`, or `docs`
  (excluding `bin`/`obj`/`TestResults`, local settings overrides, and `.user`
  files).
- Why: a general detector removes the whole class of silent omissions instead
  of guarding one more hand-picked sentinel, while the intentional runtime and
  `tools/external` ignore rules stay intact.
- Validation: `git check-ignore -v` now resolves the affected files to the
  negation rules, and the strengthened repository scan passes; the same
  `git ls-files --others --ignored` probe listed the hidden files before the
  fix and lists none after it.
- Prevention/follow-up: the scanner now runs the hidden-source probe on every
  validation. New runtime ignore patterns must be reviewed against source
  folder names case-insensitively.

## BLK-0013 — Composition root registered no adapters, so no host could run

- First observed: `2026-07-26T22:20:00Z`
- Status: resolved and validated
- Impact: every host was unrunnable. `AddWotBTreaderFoundation` registered
  paths, application services, and the diagnostics pair, but never called
  `AddSqliteStorage`, `AddReplayDecoding`, `AddCaptureLogs`, or
  `AddGameIntegration`, so `IReplayIngestionService` resolved while none of its
  ports did, and the bootstrap game roots were accepted and discarded.
  `SequencedTelemetryEventPublisher` separately exposed an all-optional
  `(int, int)` constructor beside `(TimeProvider)`, which the DI activator
  treats as ambiguous, so the telemetry publisher and the SignalR hub could
  never be constructed in any host.
- Evidence: a new composition test building the real container with
  `ValidateOnBuild` reported "Unable to resolve service for type
  `ISourceArtifactStore`" and "The following constructors are ambiguous". The
  CLI entry point was still the template's `Console.WriteLine("Hello, World!")`,
  so no process had ever exercised the container.
- Cause: adapters were built and unit-tested project by project, and the
  milestone treated a green per-project suite as integration. No test built the
  composition root or started a host, so the one file that closes the
  dependency direction was never exercised.
- Resolution: register the four adapters plus `AddLogging` in the foundation,
  derive `SqliteStorageOptions` from `LocalApplicationPaths` so Bootstrap owns
  the on-disk layout, add `StorageInitializationHostedService` to migrate at
  startup with fatal-on-failure semantics, remove the ambiguous constructor
  defaults, and give the CLI a real entry point.
- Why: a container-level test is the only guard that fails for the whole class
  of "compiles and unit-tests but cannot start", which per-project suites are
  structurally unable to catch.
- Validation: `CompositionRootTests` resolves all 18 published ports and starts
  a real host that migrates the schema; `CliEntryPointTests` runs the CLI
  in-process; and the built executable returned exit code zero for `doctor`,
  `sessions --json`, and `sessions`.
- Prevention/follow-up: any new port must be added to the published-port list
  in `CompositionRootTests`. Treat "the unit tests pass" as insufficient
  evidence that a host starts.

## BLK-0014 — Rendezvous capability file relied on inherited permissions

- First observed: `2026-07-26T22:36:40Z`
- Status: resolved and validated
- Impact: `docs/architecture/overview.md` promises discovery through "a
  short-lived rendezvous file with owner-only permissions", but the file was
  written with `FileShare.None`, which is a sharing mode rather than an access
  control entry, into a directory created by plain
  `Directory.CreateDirectory`. The record contains a live capability token that
  authorizes mutations against the local API. Under the default
  `%LOCALAPPDATA%` root the inherited ACL is already user-private, so shipped
  defaults were not exposed; a custom `Paths:ApplicationDataRoot` under a
  permissive parent would have published the credential to every local account.
- Evidence: `LocalApplicationPaths.EnsureDirectoriesExist` applied no DACL, and
  `RendezvousPublisher.PublishAsync` re-created the directory with
  `Directory.CreateDirectory` on every refresh, which would restore inherited
  permissions even after an out-of-band fix.
- Cause: the security property was stated in the architecture document and
  implemented only as far as the default location happened to satisfy it. A
  sharing mode was mistaken for a permission.
- Resolution: `LocalApplicationPaths` now creates or re-secures the rendezvous
  directory with a protected DACL granting only the current user, severing
  inheritance so a permissive parent cannot re-grant access, with an
  `OperatingSystem.IsWindows()` guard and a `0700` equivalent elsewhere. The
  publisher calls that helper instead of `Directory.CreateDirectory`.
- Why: securing the directory rather than each file means the temporary file
  the publisher writes and renames inherits the restriction, so there is no
  window in which the token exists under weaker permissions.
- Validation: a Bootstrap test asserts the rendezvous directory's access rules
  are protected and contain exactly one allow entry for the current user's SID;
  `scripts/validate.ps1` passes with 135 tests.
- Prevention/follow-up: any future file holding a credential or capability must
  be created inside a directory secured by this helper. Security properties
  asserted in architecture documents need a test that fails when the property
  regresses.

## Amendment — BLK-0003 target-framework regression (`2026-07-28T22:37:54Z`)

- Status: reopened; assigned to architecture roadmap Milestone 1.
- Regression: `Host.Web`, `Host.Web.Tests`, and `GameIntegration.Tests` were
  changed to `net10.0-windows` after BLK-0003 was marked resolved. The
  architecture suite did not inspect project target frameworks, so the full
  validation gate could remain green.
- Containment: Host.Web process-memory access was disabled in M0. The portable
  TFM restoration and an all-project TFM allowlist test are explicit M1 exit
  criteria; the blocker is not considered resolved again until both pass.
- Prevention: release evidence must come from project-file architecture tests,
  not only from the fact that the solution compiles on a Windows machine.

## Amendment — BLK-0014 ACL regression and recovery (`2026-07-28T22:37:54Z`)

- Status: implementation restored; focused validation passed, full gate pending
  at the time of this amendment.
- Regression: the protected owner-only ACL helper and its test were removed in
  favor of inherited `%LocalAppData%` permissions while the rendezvous record
  still carried a mutation capability. That contradicted the original
  resolution and the accepted local trust boundary.
- Recovery: rendezvous creation now severs inheritance, grants only the current
  user full control, rejects reparse points, and verifies the owner and complete
  access-rule set before publication. Non-Windows creation enforces and verifies
  mode `0700`. Failure is explicit; no capability is written into an
  unverified directory.
- Evidence: the Bootstrap regression test starts with a permissive inherited
  ACL and proves the resulting directory is protected, current-user-owned, and
  contains exactly one explicit allow rule.

## BLK-0015 — Unverified process-memory attachment bypassed offline evidence

- First observed: `2026-07-28T22:00:00Z`
- Status: contained in Milestone 0; centralized authorization remains M2 work.
- Impact: Host.Web attached after finding a game window, while GameHarness
  `scan --pid` and `probe` called a raw memory scanner without replay evidence.
  Either path could request a memory-capable handle before positively verifying
  playback of a pre-recorded replay.
- Evidence: Host.Web called its memory reader from window discovery; the
  GameHarness CLI parsed or discovered a PID and called
  `MemoryOffsetScanner.Attach` directly.
- Containment: Host.Web attachment is fail-closed and contains no
  process-memory P/Invoke. GameHarness `scan` and `probe` now return stable
  `UnsupportedCapability` results before argument parsing, process enumeration,
  or attachment. Black-box tests exercise both commands.
- Follow-up: M2 must introduce the evidence-backed offline-session state
  machine and a non-forgeable, short-lived authorized observation bound to PID,
  executable identity, version/hash, launch correlation, source, and freshness.
  Raw PID or caller-constructed authorization records are not acceptable.

## BLK-0016 — Managed replay correlation pinned an arbitrary lifecycle source

- First observed: `2026-08-02T01:21:51Z`
- Status: resolved and validated
- Impact: a managed offline replay launched successfully, but the positive
  lifecycle marker could be written to a newly created native log while launch
  correlation was pinned to one arbitrary pre-launch source. The session then
  failed closed with `evidence.cursor_invalid`, so no scanner authorization was
  issued.
- Evidence: the lifecycle baseline contained multiple active native-log
  sources, and the post-launch replay marker appeared in a new source. The
  registrar retained only the first sorted source, while the journal also kept
  deleted-source tombstones in launch baselines.
- Cause: the launch context modeled one source cursor even though the feed
  reconciles a set of independently rotating native logs. The first repair
  treated every generation-one source first observed after a healthy pass as
  live, but observation time alone could also bless stale prepopulated bytes.
- Resolution: capture only active source cursors, retain the entire defensive
  baseline set in the managed launch context, and record the completion time of
  each successful reconciliation. Initial bytes in a first-seen source are live
  only when the file creation time and parsed native marker timestamp are both
  at or after that barrier. The coordinator independently rechecks the marker
  timestamp, source generation, journal sequence, and byte-offset monotonicity.
  A healthy baseline may contain zero active sources because its completed-time
  anchor still makes a later generation-one source positively correlatable.
- Validation: focused lifecycle/coordinator tests cover multiple sources,
  tombstone exclusion, stale prepopulated new sources, time-correlated live new
  sources, healthy empty baselines, historical markers, advanced generations,
  and baseline byte offsets. A private replay then reached
  `OfflineReplayVerified` through the managed launch API.
- Prevention/follow-up: lifecycle authorization must remain set-based and
  provenance-aware; never select one baseline source as representative of a
  rotating native-log directory.

## BLK-0017 — Guarded scanner rejected the installed WOW64 x86 client

- First observed: `2026-08-02T01:43:00Z`
- Status: resolved and validated
- Impact: the managed replay reached `OfflineReplayVerified`, but every scanner
  operation returned `discover.identity_mismatch` before a memory read. The
  installed client is a WOW64 x86 process, while the guarded scanner accepted
  only native x64 targets.
- Evidence: a path-free native probe during a positively verified offline
  replay proved that process opening, creation-time query, and executable-path
  equality all succeeded. `IsWow64Process2` reported an x86 process on an AMD64
  host, isolating the rejection to the architecture allowlist.
- Cause: `AuthorizedProcessLease` treated `IMAGE_FILE_MACHINE_I386` as an
  identity failure and pointer-chain traversal used the host `IntPtr.Size`
  instead of the target pointer width.
- Resolution: accept native x64 and WOW64 x86 targets from a 64-bit host,
  retain target architecture, pointer width, and maximum address on the
  guarded lease, bound region enumeration to the target address space, and
  decode pointer chains using four or eight bytes according to the target.
  Snapshot base/minimum/maximum ranges are validated against that measured
  target bound after the identity lease opens, including room for one complete
  value, so an x86 request above 4 GiB fails instead of returning an empty
  successful snapshot.
- Validation: architecture and pointer-decoding regression tests pass. The
  real offline smoke reported target architecture `x86`, completed a bounded
  neighborhood read, created and compared a bounded snapshot, and discarded
  the retained session.
- Prevention/follow-up: architecture failures should remain fail closed, but
  supported target machines and pointer widths must be tested independently of
  the host process architecture.

## BLK-0018 — Scanner diagnostics emitted sensitive identity and memory data

- First observed: `2026-08-02T01:35:00Z`
- Status: resolved and validated
- Impact: scanner diagnostics included canonical executable paths, caller
  labels and expected/mask bytes, and completion samples containing absolute
  addresses, decoded values, and observed process-memory bytes. Any matched
  player name, account identifier, chat fragment, or other private runtime data
  could therefore persist in logs.
- Evidence: structured log templates in both scanner engines contained
  `ExecutablePath`, `ExpectedHex`, `ToleranceMaskHex`, and `CandidateSample`
  fields populated from authorization, request, and scan-result objects.
- Cause: identity data needed for fail-closed revalidation was reused as
  diagnostic context without applying the path-redaction boundary.
- Resolution: remove full paths, caller labels, expected bytes, masks, query
  values/ranges, memory addresses, and candidate samples from persistent
  scanner logs. Diagnostics retain only operation kind, bounded counts,
  truncation/read-failure status, elapsed time, measured architecture, and
  non-sensitive identity metadata. Internal identity comparison and returned
  loopback results remain unchanged.
- Validation: a logger-capture regression test proves sentinel labels and byte
  sequences never enter rendered diagnostics. Architecture source guards reject
  full-path, raw-input, candidate-sample, and formatted-address log templates;
  the full validation suite passes.
- Prevention/follow-up: authorization, request/result, and diagnostic fields
  are separate concerns. Any new scanner log must remain path-free and must not
  serialize caller-controlled labels or process-memory content.

## BLK-0019 — Offset promotion lacks a second independent replay

- First observed: `2026-08-02T02:47:26Z`
- Status: open
- Impact: the private replay inventory contains multiple files but only one
  distinct replay payload. Fresh process launches can establish cross-launch
  repeatability, but no dynamic candidate may satisfy the repository's
  two-independent-replay promotion rule with the currently available evidence.
- Evidence: an aggregate-only local inventory counted replay artifacts and
  distinct content digests without publishing file names, paths, hashes, replay
  bytes, or player data. All available artifacts resolved to one distinct
  payload.
- Cause: no second independently sourced private replay is currently available
  to the offset-discovery campaign.
- Resolution: pending. Obtain or record a second offline replay through normal
  gameplay, then repeat the same hypothesis and transition protocol in a fresh
  managed launch. Keep all replay artifacts and raw discovery evidence local
  and ignored.
- Prevention/follow-up: campaign tooling and summaries must report launch and
  replay independence separately. Never infer replay independence from file
  count, file name, or repeated launches, and never promote a candidate while
  this blocker remains open.

## BLK-0020 — Campaign module probe crossed below the trusted module base

- First observed: `2026-08-02T03:06:35Z`
- Status: resolved and validated
- Impact: the first positively verified campaign attempt failed closed with an
  HTTP 400 before snapshot creation, so no aggregate evidence was collected.
- Evidence: the campaign requested the minimum 64-byte neighborhood at relative
  offset zero. Neighborhood windows are a radius on both sides of the reference,
  which placed the computed lower bound before the trusted module base.
- Cause: the new CLI treated `WindowSize` as a total forward length while the
  scanner contract treats it as the radius around `ReferenceOffset`.
- Resolution: place the private base-derivation probe one minimum radius into
  the module. Its lower bound now lands exactly on the trusted base; value
  decoders remain disabled, and neither the derived base nor response candidates
  are rendered.
- Validation: a focused regression asserts the 64-byte reference displacement;
  the GameHarness suite passes. Two fresh managed offline launches then completed
  the probe, bounded snapshot, two rolling comparisons, and session discard.
- Prevention/follow-up: callers must distinguish radius-based neighborhood
  windows from exclusive snapshot ranges. Keep a request-shape test at this
  boundary.

## BLK-0021 — Controlled-transition work exceeds the fixed evidence lifetime

- First observed: `2026-08-02T03:23:58Z`
- Status: open
- Impact: the coordinator terminates the exact managed replay process 15
  seconds after its correlated live start marker. That bound is appropriate
  for automated aggregate reconnaissance but cannot accommodate an operator's
  bounded interactive Cheat Engine scan and controlled movement transition.
- Evidence: `GameSessionCoordinator` hard-codes a 15-second evidence lifetime;
  two OD-RECOVERY-003 launches terminated at that expiry. The repository's
  controlled-transition workflow requires an observed state A, an operator
  transition, and an observed state B. The guarded input service is currently
  unwired scaffolding, so it cannot safely automate the transition instead.
- Cause: authorization lifetime and the aggregate campaign were designed for
  short automated reads before the later interactive research protocol was
  exercised against a real managed launch.
- Resolution: pending. Add an explicit local research opt-in whose default
  remains 15 seconds and whose hard maximum is two minutes. Preserve immediate
  revocation on replay stop, unhealthy or gapped lifecycle evidence, process
  exit, identity change, cancellation, and expiry. Do not wire or bypass the
  unavailable guarded-input adapter.
- Prevention/follow-up: prove default and bound validation, configurable
  expiry, and every immediate monitor revocation path before using the opt-in
  in a positively verified offline replay. Never expose it as an implicit
  production default or use it to authorize an uncorrelated process.
