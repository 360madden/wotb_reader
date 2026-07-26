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
