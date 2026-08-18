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

## Amendment — Numbering note (`2026-08-02`)

BLK numbers are assigned sequentially, and the union of numbers across this
log and `blockers/` must stay contiguous (`scripts/python/offline_check.py`
fails the gate on gaps or repeats). As of this note, BLK-0008–0011 are
recorded in [`blockers/2026-07-26-replay-decoder.md`](blockers/2026-07-26-replay-decoder.md),
and [`blockers/2026-07-26-command-execution-gate.md`](blockers/2026-07-26-command-execution-gate.md)
is a companion deep-dive for BLK-0007. See [`README.md`](README.md) in this
folder for the full numbering convention and document map.

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

## Amendment — BLK-0014 file-level ACL hardening (`2026-08-16T13:35:00Z`)

- Status: follow-up hardening complete; the directory-level property is
  extended to the published record itself.
- Gap: the rendezvous directory was protected and verified owner-only, but the
  final `web.json` carried only an *inherited* ACL and was never re-verified
  after the temporary file was renamed into place.
- Resolution: `RendezvousPublisher` pins an explicit protected owner-only
  descriptor onto the temporary file before it becomes the record and
  re-verifies the final `web.json` after the move (rejects reparse points and
  re-reads the DACL from an open handle via `GetKernelObjectSecurity`, so a
  same-user pathname swap cannot redirect the verification; non-Windows
  enforces mode `0600`). Verification failure fails the publish closed and the
  lease rotates on the next cycle.
- Evidence: Bootstrap tests sever a permissive inherited parent ACL on the
  file, reject a file with an extra WorldSid ACE, reject a missing file, and
  reject a symbolic-link reparse point where the environment permits one; the
  full `scripts/validate.ps1` gate passes.
- Reader-side follow-up (`2026-08-16`): `RendezvousLocator` rejects a
  reparse-point record on read as well, so the overlay no longer follows a
  substituted symlink to an arbitrary target.

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
- Status: superseded (2026-08-11)
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
  - Status update `2026-08-08T06:10Z` (OD-RECOVERY-058): resolution path
  confirmed. The user replay folder
  (`AppData/Local/wotblitz/DAVAProject/replays/`) holds a second
  independently recorded 11.19.0 replay — the game-named save
  `20260802_1615__mrkool1138_GB08_Churchill_I_8565111466734423.wotbreplay`
  (sha `0fae5612…`, savanna/Oasis Palms, battle 2026-08-02T21:15:07) — which
  is distinct from the FRESH43 Dead Rail payload (sha `59c3b92e…`). It decoded
  cleanly as session `019fdff7-8dcf-7426-8547-9fb8cc3eb07b` (same player and
    tank as FRESH43). Cross-battle M3 validation can now run the correlate +
    interceptor on this second battle in a fresh managed launch.
  - Status update `2026-08-08T16:32Z` (OD-RECOVERY-059): **resolved**. FRESH44
    exercised the second content-distinct replay in a fresh managed launch with
    `OfflineReplayVerified`; the viewpoint-position correlation repeated at
    `0.9375` (15/16) and the sampled series were preserved durably. This resolves
    replay availability and independence only. It does not promote an offset:
    the matching addresses remain transient heap copies and the bounded trace
    captured no writes.
  - Prevention/follow-up: campaign tooling and summaries must report launch and
  replay independence separately. Never infer replay independence from file
  count, file name, or repeated launches, and never promote a candidate while
  this blocker remains open.
- Superseded (2026-08-11): two-replay promotion is now PROVEN for every
  published/live family — position (OD-075/076/078/081/082, Dead Rail + Oasis
  continuous polls; G0 publication applied 2026-08-10), yaw (OD-089:
  ring-record `+0x30` on BOTH replays, `twoReplayRepeatability = true`), and
  HP (OD-091: entity-base `+0xB8` on BOTH replays, Strict 4/4 exact sums,
  `twoReplayRepeatability = true`). The two-independent-replay promotion rule
  is satisfied and no longer blocks any publication.

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
- Status: superseded (2026-08-11)
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
- Superseded (2026-08-11): the Cheat Engine controlled-transition workflow
  was retired (offline-only policy, ADR 0002 amended); discovery now runs
  approved live sessions through the launcher with the research-only
  `Research:LifecycleEvidenceTimeoutSeconds` bound (BLK-0024) and bounded
  session drivers. Every 2026-08-11 session (087/088/089/090/091 + camera
  lane) completed within the evidence bounds — the fixed 15-second
  coordinator bound no longer blocks any active workflow.

## BLK-0022 — Controlled replay movement requires an available operator

- First observed: `2026-08-02T03:45:40Z`
- Status: superseded (2026-08-11)
- Impact: OD-RECOVERY-004 cannot yet collect a defensible state A to state B
  movement pair. Treating natural replay progression as controlled would repeat
  the invalid hypothesis already ruled out by OD-RECOVERY-003.
- Evidence: Cheat Engine 7.7 was located and launched twice without a game
  process, first through its installed launcher and then through the registered
  64-bit executable. Windows elevated the resulting process, and the approved
  computer-use bridge could not target its window. The repository's guarded
  input service has no registered Windows implementation. No replay was
  launched and no scanner attached during either attempt.
- Cause: the only policy-compliant transition source currently available is an
  operator key press in the positively verified offline replay. Automating the
  game through another UI or input-injection path would bypass the repository's
  one-shot input-arm boundary.
- Resolution: pending operator availability. Use the guarded loopback Float32
  snapshot/compare path over private/mapped regions, have the operator pause
  state A and perform the brief resume/pause transition, suppress response
  candidate details, retain aggregate counters only, and discard the scanner
  session. A two-minute hard lease bounds the attempt and terminates the exact
  managed child at expiry.
- Prevention/follow-up: confirm operator readiness before starting the managed
  replay lease. Keep natural progression classified as reconnaissance only,
  and do not record this setup attempt as position-field evidence.

- Resolution amendment (`2026-08-02T19:20:00Z`): for `OD-RECOVERY-005` the owner
  explicitly authorized end-to-end foreground window operation on the managed
  offline child. Watch Offline was activated by a click in the visible dialog
  region and the controlled pause→brief resume→pause transition used Space on
  the foreground verified window. The guarded GameHarness input adapter remains
  **unregistered**; this amendment does not wire or bypass that adapter for
  general use. Natural replay progression without an authorized transition
  remains reconnaissance-only. OD-RECOVERY-005 recorded aggregate narrowing and
  a private-mapping kind histogram only — not a promoted offset.

- Resolution amendment (`2026-08-02T19:40:00Z`): the same owner-authorized
  foreground path was reused for `OD-RECOVERY-006` (Watch Offline click + Space
  pause/resume) under the standing “keep autonomously / full permissions”
  instruction. OD-006 added aggregate A→B narrowing and privacy-safe AOB
  pointer-byte probes; no module-image root and no promoted offset.

- Resolution amendment (`2026-08-02T19:50:00Z`): the same authorized foreground
  path covered `OD-RECOVERY-007`. Soft-cap MaxBytes and `ImageRegionsOnly`
  pattern probes ran under the verified offline lease; 12/12 image-only
  absolute pointer AOBs returned zero hits (negative structural evidence). No
  promoted offset.

- Resolution amendment (`2026-08-02T20:05:00Z`): the same authorized foreground
  path covered `OD-RECOVERY-008`. Windowed Float A→B + absolute LE pointer AOB
  across private/all/image and align 1/8 returned zero hits. CE 7.7 x64
  launched and responded under `OfflineReplayVerified`; no automated
  attach/scan (adapter still unregistered). No promoted offset.

- Resolution amendment (`2026-08-02T20:10:00Z`): the same authorized foreground
  path covered `OD-RECOVERY-009`. Truncated low-32 LE dword AOB returned zero
  hits. CE attached with Windows debugger and three access breakpoints under
  the offline gate; overlapping resume pulse produced zero hits (no RIP module
  evidence yet). Adapter still unregistered. No promoted offset.

- Resolution amendment (`2026-08-02T20:15:00Z`): the same authorized foreground
  path covered `OD-RECOVERY-010`. A probed window reached changed≈1955; CE
  Windows debugger set three `bptWrite` breakpoints (list count 3) with zero
  hits during overlapping resume; VEH debug attach stalled. Adapter still
  unregistered. No promoted offset.

- Resolution amendment (`2026-08-02T20:20:00Z`): the same authorized foreground
  path covered `OD-RECOVERY-011`, including a required **WATCH OFFLINE** click
  on the not-logged-in dialog (never LOG IN AND WATCH). Second-pass Float
  narrowing reached changed≈1929; CE write breakpoints set (3) with zero RIP
  hits. Adapter still unregistered. No promoted offset.

- Resolution amendment (`2026-08-02T20:25:00Z`): agent-owned **WATCH OFFLINE**
  click covered `OD-RECOVERY-012`. Field pivot found a unique increased
  `replayTime` Double (`private-mapping`) and an HP Int32 unchanged pool
  (`mapped-mapping` sample). CE write-BP hitCount=0; pointer AOB 0. No
  promoted offset.

- Resolution amendment (`2026-08-02T20:30:00Z`): agent-owned **WATCH OFFLINE**
  click covered `OD-RECOVERY-013` on a second independent process launch.
  Rolling Double increased narrowed 193→60→15→4 (`private-mapping`). Same
  replay artifact (independentReplays unchanged). No promoted offset.

- Resolution amendment (`2026-08-02T20:35:00Z`): agent-owned **WATCH OFFLINE**
  click covered `OD-RECOVERY-014`. Neighborhood via survivor relativeOffset
  worked but was noisy; pointer AOB not stable. No promoted offset.
- Superseded (2026-08-11): the CE-controlled-movement hypothesis was ruled
  out and replaced by the approved-session flow with bounded gated
  polls/dumps (OD-RECOVERY-075..091). No Cheat Engine dependency remains;
  the resolver/ring chains and the entity-region read surface are the only
  live-memory paths (offline-only policy).

## BLK-0023 — Managed replay launch hid the window and fabricated window evidence

- First observed: `2026-08-02T04:34:31Z`
- Status: superseded (2026-08-11)
- Impact: two managed replay attempts produced an exact correlated
  `wotblitz.exe` PID and `OfflineReplayVerified`, but no visible game window.
  The operator correctly rejected both launches before any scanner operation.
- Evidence: the second attempt used exact PID 41072. It remained responsive and
  positively lifecycle-verified for more than 20 seconds while
  `MainWindowHandle` stayed zero; the approved computer-use window inventory
  likewise found no game window. Source inspection then showed
  `WindowsSuspendedProcessPlatform` passing `STARTF_USESHOWWINDOW` with
  `SW_HIDE`, while `GameSessionCoordinator` constructed monitor evidence with
  the synthetic constant `WindowHandle: 1`.
- Cause: the low-level launch path intentionally hid the child window, and the
  lifecycle monitor substituted a sentinel instead of querying the existing
  eligible SDL-window/process identity observer.
- Resolution: pending. Use the default normal window-display behavior for
  `CreateProcessW`. Before authorization, require exactly one eligible visible
  SDL game window whose PID, process-start identity, canonical executable,
  version, and SHA-256 match the managed launch. Revalidate that observation
  throughout the lease and revoke immediately on loss, ambiguity, query
  failure, or identity mismatch.
- Prevention/follow-up: lifecycle correlation and process liveness cannot
  substitute for visible-window ownership. Keep regressions for normal startup
  flags, zero-window denial, observed-handle propagation, delayed window
  materialization, and post-verification window loss. These attempts contain no
  offset evidence and must not be recorded as failed position scans.

- Resolution amendment (`2026-08-02T11:15:00Z`): resolved and live-proven.
  `WindowsSuspendedProcessPlatform` no longer sets `STARTF_USESHOWWINDOW` with
  `SW_HIDE`; the coordinator depends on `IGameProcessIdentityObserver` and no
  longer synthesizes `WindowHandle: 1`. Managed observation passes the exact
  expected PID; the Windows platform uses `EnumWindows` for that PID and
  accepts only visible, root, ownerless windows with a non-empty client area,
  stopping after a second match so ambiguity is reported without scanning the
  rest of the desktop. Process start identity, canonical executable path,
  product version, SHA-256, PID, owner PID, and nonzero handle must all match
  before authorization. Absence or mismatch of the exact window after
  verification is terminal and revokes authorization immediately. Repeated
  fresh private-replay launches reached `OfflineReplayVerified` with exactly
  one visible game window; the offline gate passed with zero online-match
  actions. The stale `SDL_app` class restriction and the intermediate
  desktop-enumeration cap that also blocked exact managed launches are recorded
  separately in BLK-0025. Live regressions cover normal startup flags,
  zero-window denial, observed-handle propagation, and the exact-PID
  enumeration policy.
- Superseded (2026-08-11): the launcher now shows AND sizes the game window
  explicitly (FRESH17: 640×360 after settle) and places it at the second
  monitor's top-left when one is attached (2026-08-11, `d5fbb94`); window
  visibility is live-verified by the dialog clicker's ready-gate on every
  launch (verified on the 2026-08-11 sessions, including the monitor-2
  placement). No launch hides the window anymore.

## BLK-0024 — Offline confirmation exceeded the fixed lifecycle startup wait

- First observed: `2026-08-02T04:57:21Z`
- Status: resolved and validated (2026-08-02, amendment below; launcher flow
  re-proven on every 2026-08-11 live session)
- Impact: after the managed child became visible and the operator-selected
  **Watch Offline** path began loading, no fresh native replay-start marker
  arrived before the fixed 45-second lifecycle-evidence deadline. The
  coordinator correctly denied the session and terminated the exact child;
  no scanner request ran.
- Evidence: the first visible attempt exposed the offline-choice dialog but
  expired before it could be selected. A clean retry selected **Watch Offline**
  immediately in exact PID 43004; the dialog disappeared and loading began,
  but the same bounded deadline ended with
  `launch.lifecycle_evidence_timeout` and zero game processes.
- Cause: `GameIntegrationOptions` already validates lifecycle startup waits
  from 5 seconds through 5 minutes, but the web-host composition exposed only
  the post-verification research lifetime. Local replay startup therefore had
  no supported way to accommodate the required operator confirmation and slow
  client load while preserving the production default.
- Resolution: pending live proof. Preserve the 45-second default and expose an
  explicit `Research:LifecycleEvidenceTimeoutSeconds` host setting. Use 120
  seconds for the next bounded offline retry; scanning remains denied until a
  fresh correlated marker and exact visible-window identity both exist.
- Prevention/follow-up: keep composition coverage for explicit timeout
  propagation and document both independent research bounds. A longer startup
  wait must never extend authorization after replay stop, feed failure, window
  loss, identity change, cancellation, or evidence expiry.

- Resolution amendment (`2026-08-02T11:15:00Z`): resolved and live-proven.
  The web host now accepts the research-only
  `Research:LifecycleEvidenceTimeoutSeconds` setting. The production default
  remains 45 seconds; research values from 5 through 300 seconds are validated
  and propagated independently from the 5–120 second post-verification replay
  evidence lifetime. A clean visible retry selected **Watch Offline**
  immediately and, with the explicit 120-second research override, completed
  the offline confirmation and client load before a fresh correlated native
  replay-start marker arrived. The loopback gate reached
  `OfflineReplayVerified` with `session.offline_replay_verified`, and the exact
  managed child was terminated at evidence expiry or stop as before. The longer
  startup wait never extended authorization after replay stop, feed failure,
  window loss, identity change, cancellation, or evidence expiry; focused
  coordinator tests pin that property. Composition coverage and both research
  bounds are documented in the operations and offline discovery guidance.

## BLK-0025 — Exact managed launches were blocked by the SDL_app restriction and a capped desktop enumeration

- First observed: `2026-08-02T05:20:00Z` (identified while resolving BLK-0023)
- Status: resolved and validated
- Impact: an exact managed replay launch produced a valid visible window and
  fresh replay-start evidence, but the window observer hard-coded the
  `SDL_app` window class and the installed client exposed a differently
  classified valid window. An intermediate exact-PID enumerator additionally
  capped total desktop enumeration, falsely reporting an incomplete
  observation on a busy desktop before the exact matching window could be
  reached. Both defects denied the launch without a scanner operation.
- Evidence: the installed client showed fresh replay-start evidence and a
  visible window that did not satisfy the `SDL_app` class restriction; a busy
  desktop produced `IsComplete=false` from the capped enumerator.
- Cause: the generic observer reused the historical `SDL_app` class filter for
  exact managed launches, where the class name is not a reliable identity
  signal, and the intermediate enumerator applied a total-desktop cap instead
  of stopping only when ambiguity is proven.
- Resolution: exact managed launches deliberately do not rely on the
  `SDL_app` class name; the exact-PID path uses `EnumWindows` for that PID,
  accepts visible, root, ownerless windows with a non-empty client area, and
  stops after a second match. Generic, unmanaged observation retains the
  historical `SDL_app` class filter. `IsComplete` is true when the native
  enumeration completes or ambiguity is proven, so a busy desktop cannot
  produce a false incomplete observation.
- Why: exact ownership by the managed PID, not a class heuristic, is the
  trust boundary for a correlated launch; class names vary by client and must
  not gate a positively verified managed replay.
- Validation: GameIntegration focused suite (206 passed, 2 expected local
  opt-in skips) after the exact-PID window changes; repeated live visible
  managed launches reached the exact offline gate. Regression coverage
  exercises exact-PID enumeration ambiguity and post-verification exact-window
  loss.
- Prevention/follow-up: keep the generic class filter only on the unmanaged
  discovery path; exact managed launches must enumerate by PID and stop at
  ambiguity, never at a fixed desktop cap.

## BLK-0026 — Content-distinct replay exited before the verified polling window

- First observed: `2026-08-09T02:17:00Z`
- Status: resolved and validated
- Impact: the unchanged OD-075 continuous-position poll could not be repeated
  on the other content-distinct replay/fresh process. This leaves
  cross-replay repeatability unproved for the stable polling resolver.
- Evidence: bounded canonical managed-launch attempts ended before
  `OfflineReplayVerified`; the Host remained `Unknown` with
  `session.initial`, no game window survived, no position request or other
  memory operation ran, and no OD result was created. Exact managed processes
  were stopped afterward.
- Cause: `Set-OwnerOnlyFileAcl`/`Set-OwnerOnlyDirectoryAcl` in
  `scripts/launch-offline-replay-for-od.ps1` (added 2026-08-09T02:38Z by
  `1ad5381`) threw `PrivilegeNotHeldException` (`SeSecurityPrivilege`) and the
  launcher's catch-all turned it into `FAILED_unexpected` (exit 5). The throw
  occurs in .NET `Set-Acl` when the target already carries a protected
  owner-only ACL — i.e. on EVERY launch after the first, because the
  `od-launch` marker directory/file persist between launches. Since the last
  successful launch (02:14Z) predates `1ad5381`, no launch worked afterward:
  no marker rewrite, no game launch, gate never verified — the exact observed
  `session.initial` signature. Proven by an instrumented launcher run
  (stack: `Set-OwnerOnlyDirectoryAcl` → `Set-Acl`, line 173) and by a
  controlled probe (T1/T2/T5/T6: fresh-object `Set-Acl` succeeds on a fresh
  target and throws on the same target a second time; `icacls` always
  succeeds).
- Decision: stop unchanged discovery retries. Diagnose the content-distinct
  launch path separately using non-memory lifecycle evidence. After that
  diagnosis is validated, permit one unchanged bounded poll on that replay.
- Resolution: replace .NET `Set-Acl` with `icacls /inheritance:r /grant:r`
  in both owner-only ACL functions (launcher + `publish-instruction-snapshot-
  helper.ps1`, which shared the identical pattern). `icacls` needs no
  security-descriptor privileges, preserves the current-user owner, and yields
  exactly the single owner FullControl rule the existing `Test-OwnerOnly*`
  checks expect. No product code, resolver, or read surface changed.
- Validation (2026-08-09, live): launcher reaches
  `OK OfflineReplayVerified` (exit 0) repeatedly after the fix — the managed
  launch POST is accepted, the game window appears, the offline-replay dialog
  is clicked, and the post-watch gate check passes with
  `reason=session.offline_replay_verified`. Then exactly ONE unchanged bounded
  OD-075 poll ran on the content-distinct replay during the active battle:
  `verdict=stable-resolver-positive`, `resolved=24/24`,
  `distinct=24`, `within1=12`, `within3=21`, `allModuleRooted=true`,
  `trajectoryConsistent=true`, aggregate result written to
  `.data/od-073-entity-position-poll-*.json` (privacy flags all `false`).
  Managed game/Host processes were stopped afterward.
- Prevention/follow-up: keep launch failure distinct from resolver evidence;
  never classify a pre-gate exit as a negative memory read or compensate by
  broadening the resolver/read surface. Operational note: a cold game boot can
  exceed the launcher's default 90-second window wait (`FAILED_no_window`);
  use `-WindowWaitSeconds` for cold boots. Full plan and evidence:
  `docs/operations/blk-0026-diagnosis-plan.md`.

## BLK-0027 — Penetration v0.3 lacks authoritative exact armor and primary validation inputs

- First observed: `2026-08-15T03:08:29Z`
- Status: open; safe product behavior implemented, fidelity claim blocked
- Impact: penetration items 1, 2, 3, 4, 5, 7, and 8 cannot meet the v0.3
  completion thresholds. Real frames must remain neutral `NotReady`; nominal
  armor, manual/static shells, and camera direction are excluded from the
  primary scorer cohort.
- Evidence: exact-build static analysis of `TankFactory::CreateArmorComponent`
  and `PushNormalsArmorConfiguration` proved that the optional collision-mesh
  hard-joint value is consumed only by a visualization mesh rebuild. The sole
  current builder passes an empty lookup map; no `armor_N` name join, explicit
  millimeter unit, ordered ray traversal, or physical layer accumulation is
  present. The scalar consumer applies a separate per-part scale and emits
  `ArmorNodeMaterial`, so mapping the stable 1..16 sample domain to XML armor
  groups would be an unsupported guess.
- Weapon/aim evidence: exact-build RTTI and constructors identify
  `VehicleGun` and `VehicleGunRotator` as the strongest sibling owners under
  the avatar/game-logic family. Static analysis does not identify a loaded
  shell field, configured-gun identity, turret yaw/elevation, muzzle origin,
  or shot direction. `AvatarGunAgent` is a small bridge rather than a credible
  state owner; replay shot signatures and CAM-013 remain rejected as exact
  shell/ray sources.
- Decision: do not connect `HardJointIndex` to XML thickness and do not count
  the sampled stable triangle keys as exact armor coverage. Keep exact face
  thickness and ordered layers unavailable until a current-build producer,
  explicit units, and physical ordering are independently proven.
- Implemented containment: readiness/provenance contracts prohibit colored
  real-data verdicts without exact triangle armor, exact loaded shell, and an
  exact gun ray; scorer primary eligibility requires the same exact inputs.
  Exact-build manifests re-hash every consumed DVPL source, diagnostics expose
  only relative paths/hashes/counts, and unknown/allied targets fail closed.
- Exit gate: prove all three exact runtime/static inputs (armor/layers,
  configured gun+loaded shell, shot-synchronous muzzle/gun ray), then build at
  least 12 content-distinct replays and 500 unconfounded eligible shots meeting
  the thresholds in `penetration-v0.3-plan.md`. A live managed-offline capture
  is permitted only after its bounded source contract and security review are
  complete; no broader scan or online operation is authorized.
- Smallest weapon/aim discriminator: under one fresh `OfflineReplayVerified`
  lease, require unique viewpoint-owned vtable matches for `VehicleGun` and
  `VehicleGunRotator`; demonstrate shell A->B->A transitions resolving to
  exact installed shell identities; independently correlate stationary-hull
  turret yaw and gun elevation; then require finite normalized shot rays that
  join decoded targets/impacts and repeat on a second content-distinct replay.
  Ambiguous ownership, camera-only correlation, or post-shot-only changes are
  decisive no-go outcomes.

- Progress amendment (`2026-08-16`, owner-approved live lane): the first
  discriminator step is now implemented offline. The three weapon-family
  vftable RVAs are derived hash-bound from their RTTI complete object locators
  via `FindVftableViaCol.java` (executable SHA-256 `1cda5c31…`): `VehicleGun`
  `0x32dacf4` (COL `0x35ce9e0`), `VehicleGunRotator` `0x32eeb40` (COL
  `0x35e06a8`), `AvatarGunAgent` `0x324dae8` (COL `0x35317c0`). The
  `IPenetrationCaptureEvidenceSource` production registration is now
  `ExactBuildOwnerCensusCaptureEvidenceSource` (no longer the neutral stub): it
  runs two gated vftable AOB census passes over Private|Mapped regions and
  reports only privacy-safe per-family counts + unique/stable flags;  shell/aim/
  ray phases remain unproven so every real capture stays `NotReady`. A new
  capability-gated `POST /api/v1/game/discover/pen-capture` endpoint triggers
  the serialized capture from an opaque `decodeRunId`. The blocker remains
  open: the live census capture, the viewpoint-ownership walk, and the shell/
  aim/ray field proofs are still outstanding.

- Live attempt (`2026-08-16`, honest negative): the first census launch
  (Dead Rail re-launch) reached `lifecycle_evidence outcome=verified` but the
  game then assert-crashed during replay playback (`ListenerHolderBase.cpp:15`,
  AkAudio write stack in the newest blitz log) and the coordinator denied
  `evidence.monitor_unhealthy`; no census aggregate was produced. The launcher
  hung past its bounded steps on the mid-flow game death (pre-existing
  robustness gap). Environment cleaned up; the census source and endpoint stay
  merged and verified offline, and no census result is promoted.

- Resolution (`2026-08-16`, launcher robustness): the mid-flow game-death hang
  is fixed — `scripts/launch-offline-replay-for-od.ps1` now checks for a live
  `wotblitz.exe` after the Watch Offline success (exit 3 on death) and before
  the clock-anchor HTTP calls (non-fatal skip), so a playback assert costs
  seconds instead of a full bounded-step hang. The census blocker itself stays
  open; this only removes the environment-failure tax on each retry.

- Live census result (`2026-08-16`, first real capture): the owner-census
  discriminator is now LIVE-proven. A managed offline launch (Churchill I,
  map Oasis Palms, exact build `11.19.0.10` hash `1cda5c31…`) reached
  `OfflineReplayVerified` and the capture endpoint returned
  `Rejected / BoundsExceeded` with `OwnerCandidateCount=43` and all
  weapon/shell/ray flags false. The two census passes agreed
  (`stable=True`) with per-family counts `vehicleGun=42,
  vehicleGunRotator=1, avatarGunAgent=1` — 42 live gun records (one per
  vehicle) but exactly one rotator/agent pair, which is the candidate
  viewpoint owner for the next static walk. `43 > MaxOwnerCandidates=4` is the
  expected ambiguous-ownership outcome; nothing is promoted and the blocker
  stays open. Three code gaps were fixed to reach this result: the launcher
  now names `battleSessionId` in `/game/launch` (was `capture.gate_not_satisfied`),
  the capture build check accepts a three-part session version that is a dotted
  prefix of the process version (was `capture.decode_build_mismatch`), and the
  launcher's HTTP calls are bounded with 30s timeouts.

- Reproducibility (`2026-08-16`, second content-distinct battle): the Dead
  Rail replay (Dead Rail map) returned the identical census —
  `vehicleGun=42, vehicleGunRotator=1, avatarGunAgent=1, stable=True`,
  `OwnerCandidateCount=43`, `Rejected / BoundsExceeded`. The exact 42/1/1
  pattern across two battles strongly supports reading the single
  rotator/agent pair as the viewpoint's live gun controller (42 one-per-vehicle
  `VehicleGun` records). The census remains the first discriminator, not a
  resolution: BLK-0027 stays open until the ownership walk + shell/aim/ray
  proofs land.

- Ownership walk static derivation (`2026-08-16`, hash-bound): the census
  counts DISTINCT objects (each weapon object carries its primary vftable
  dword once at `+0x0`; `+0x8`/`+0xC` are secondary sub-vtables), so 42
  `VehicleGun` = 42 live objects, not 14×3 slots. The `AvatarGameLogic` ctor
  (`FUN_01683b00`) stores the viewpoint `VehicleGun` at `+0x204` and sets the
  avatar marker `+0x200 = 1`; `VehicleGameLogic::onEnterWorld`
  (`FUN_016ea010`) stores the `VehicleGunRotator` (refptr) at `+0x1fc` only
  when that marker is set (matching the exactly-one rotator); and the rotator
  ctor stores its owner at `+0x10`. The candidate chain is
  `viewpoint entity <-[+0x04]- AvatarGameLogic` with `+0x1fc -> rotator` and
  `+0x204 -> gun`, plus the `rotator+0x10 -> owner` back-pointer. The bounded
  live-validation protocol is recorded in
  `pen-ownership-walk-proof-protocol.md`; BLK-0027 stays open until it is
  live-validated and the phase 2–4 shell/aim/ray fields are derived.

- Phase 2–4 candidate layouts (`2026-08-16`, hash-bound, semantics unproven):
  `VehicleGun +0x38=100.0f, +0x3C=9, +0x40=1.0f`; `VehicleGunRotator
  +0x84=Vector3(0,0,0)`, `+0xEC` zeroed 0x40-byte block,
  `+0x134/+0x138=-100500.0f`, `+0x1BC=Vector3(-100500,…)`. The `-100500.0f`
  sentinel marks aim-shaped rotator fields, but no semantic field is proven
  (turret yaw / gun elevation / aim point remain open). These are candidates
  for the next write/read-site trace, not offsets to promote.

- Live-validation phase IMPLEMENTED + security-audited (`2026-08-16`): the
  additive `PenOwnershipWalk` entity-region anchor from
  `pen-ownership-walk-live-validation-proposal.md` is shipped and merged —
  `EntityRecordRegionAnchor.PenOwnershipWalk = 4` plus `OwnershipCandidateIndex`
  and six aggregate-only verdict fields. The coordinator runs the gated rotator
  vftable AOB scan, re-reads the rotator's own vftable under the guarded lease
  before dereferencing (`pen-walk-identity-read`/`pen-walk-identity-mismatch`),
  then the fixed five-read chain twice, fail-closed; no address/pointer/raw
  bytes leave the coordinator and the new path logs nothing. 9 coordinator + 1
  endpoint tests cover positive, each failure stage, candidate selection,
  two-pass instability, and privacy. The required security/privacy audit was
  performed by the lead (no dedicated agent spawn available in this
  environment) across loopback/capability, privacy, mutation, and fail-closed
  lanes. BLK-0027 stays open: one exact-build managed offline replay is still
  required to run the anchor and confirm H1 live, before the phase 2–4
  shell/aim/ray field derivation.

- Live validation RESULT (`2026-08-16`, H1 CONFIRMED): one exact-build
  managed offline replay (Churchill I, Oasis Palms, `1cda5c31…`; battle
  session `01a00ba4-91b8-783a-a894-b800cb35407e`) reached
  `OfflineReplayVerified` and the `pen-ownership-walk` anchor returned
  `Resolved` with `rotator_candidate_count=1` and all four chain booleans
  true across both passes (`two_pass_stable`). The single live
  `VehicleGunRotator`'s `+0x10` resolves to the `AvatarGameLogic` owner,
  whose `+0x1fc` round-trips to the same rotator, `+0x204` points to a live
  `VehicleGun` (vftable `0x32dacf4` confirmed), and `+0x04` resolves to an
  entity with plausible HP at `+0xB8`. Nothing was promoted; the optional
  same-decoded-clock label did not fire (`False`) and is recorded as an
  honest gap (it is not part of H1). The ownership-walk discriminator is now
  closed; BLK-0027 stays open until the phase 2–4 shell/aim/ray fields are
  derived and the capture verdict can be adjudicated.

- Field-trace refutation (`2026-08-17`, hash-bound `1cda5c31…`): the phase
  2–4 candidate block `VehicleGun +0x38/+0x3C/+0x40` is REFUTED as
  configured-gun/loaded-shell identity. Both the in-place ctor `FUN_01da8bb0`
  and the allocating factory `FUN_01d9cc30` hardcode the same `100.0f / 9 /
  1.0f` for every instance — class defaults, not per-instance state; the
  `AvatarGameLogic` ctor stores the gun at `+0x204` but never reconfigures its
  field block. The earlier "possibly reload/rate / shell count" guesses are
  corrected.

- Descriptor producer trace (`2026-08-17`, hash-bound): there is NO equip-time
  write into `VehicleGun` — the 9 gun-aware functions never overwrite
  `+0x38/+0x3C/+0x40`. The configured gun / loaded shell live in descriptor
  classes parsed by `GunsReader`/`ShellsReader`, not copied into `VehicleGun`:
  `Gun` (vftable `0x31a7080`), `Shell` (vftable `0x31a1e14` + `eShellKind`),
  `VehicleDescr` (vftable `0x31a3510`, `.gun`/`.turret` sections). Aim angles
  are a separate concern (`CurrentGunAnglesComponent` /
  `DestinationGunAnglesComponent`).

- Descriptor field layout (`2026-08-17`, hash-bound, nothing promoted): the
  `Shell` descriptor's damage fields are named — `+0x11c` = `damage.armor` (HP
  damage), `+0x120` = `damage.devices` (module damage), plus kind/caliber/
  isTracer/ricochet/normalization/explosionRadius/piercingPowerLossFactorBy-
  Distance. The `Gun` `vector<Shot>` ballistic entries are named
  (`defaultPortion +0x24`, `speed +0x28`, `gravity +0x2c`, `maxDistance
  +0x30`, `isATGM +0x40`). Penetration (`piercingPower`) is NOT a `Shell`
  field — it is parsed as a space-separated curve in
  `GunsReader::ParseBaseGunInfo` and its destination offset is UNCONFIRMED.
  BLK-0027 stays open until the runtime shell-index link (which `Shell`/`Shot`
  loads at fire time) and the `piercingPower` destination offset are derived,
  or the live controlled shell-swap runs.

- Shell-index link FOUND (`2026-08-18`, static, byte-verified hash-bound
  `1cda5c31…`; handoff `2026-08-18-shell-index-link-found.md`):
  `AmmoController::ProcessCurrentShells` (`FUN_015ef402`, RVA `0x11ef402`)
  writes the current shell index to **`AmmoController + 0x38`** (int32, reset
  to 0 when not found), caches the last-processed shell at `+0x34`, and walks
  the shell list via `[this+0x40] → +0x20 → vector<ptr> at +0x1b0`;
  `AmmoController` is embedded at **`AvatarGameLogic + 0x4B4`** (ctor
  `FUN_01683b00`, `MOV [EDI],0x367d3e0`). `piercingPower` destination resolved
  `2026-08-17` (`Gun +0x34`). With both static gaps closed, BLK-0027 narrows
  to: (a) trace the equip-time setter of `[AmmoController+0x40]`, and (b) the
  live controlled shell-swap correlation of `+0x38` — the promotion gate per
  `pen-promotion-gates.md`. Nothing promoted.

- `AmmoController +0x40` setter FOUND (`2026-08-18`, static, byte-verified
  hash-bound `1cda5c31…`; handoff `2026-08-18-ammo40-setter-found.md`):
  the refcounted setter is `AmmoController::ResetAmmo` (`FUN_015eff70`) —
  save-old → `MOV [this+0x40],new` at `015effb3` → helper(new)/helper(old) —
  called at equip time from the `AvatarGameLogic` equip method
  (`FUN_016d1b60`, call at `016d1bbe`) with `this = &avatar[0x4B4]` and the
  new gun/ammo ref from `[vehicleInfo+0x68]`. Only one vftable construction
  site exists (the `AvatarGameLogic` ctor), confirming the setter is a method
  call, not a construction site. **The static shell-index chain is complete
  end-to-end** (`+0x4B4` AmmoController → `+0x40` ref → `+0x20` → `+0x1b0`
  shell vector → `+0x1c` Shell → damage `+0x11c`). BLK-0027's remaining item
  is only (b): the **live controlled shell-swap correlation** of `+0x38` and
  the `+0x40` chain in an exact-build managed offline replay — the promotion
  gate per `pen-promotion-gates.md`. Nothing promoted.

- Aim/ray static pass DONE (`2026-08-18`, hash-bound `1cda5c31…`; handoff
  `2026-08-18-rotator-static-structure.md`): the `VehicleGunRotator`
  vtable methods are event/UI plumbing plus the gun-marker target refs at
  `+0x1d4/+0x1d8`; `GetGunMarkerPosition` (`FUN_01ec12b0`) computes the
  gun-marker aim struct at `[rot+0x28..0x40]` (hit pos + normalized
  direction + distance); `Update` (`FUN_01ed1df0`) receives 4
  angle/state-shaped floats per frame from `AvatarGameLogic` (`016f303d`)
  and stores two at `+0xe0/+0xe4`. Naming turret yaw vs gun elevation (and
  the muzzle-ray role) is NOT statically resolvable — confirming the
  2026-08-16 conclusion. The live controlled turret/gun + shell-swap
  correlation is the shared promotion gate. Nothing promoted.

- Armor producer trace DECISION (`2026-08-18`, hash-bound `1cda5c31…`):
  **explicit no-go.** The 2026-08-15 bounded RTTI triage
  (`pen-v03-alternative-armor-owner-triage.md`) plus a follow-up symbol
  check found RTTI identities for `ArmorComponent`/`ArmorConfiguration`/
  `PushNormalsArmorConfiguration`/`ArmorVisualizationSystem` but zero
  producer method-name anchors (`s_…::…` strings) — no parse/load/build
  producer is visible, so a deeper trace is an open-ended hunt without a
  hypothesis. Revisit only if a concrete producer (surface identity +
  thickness/units + order + interaction kind) emerges; the rejected
  hard-joint path stays closed. Armor remains honest `NotReady`.
