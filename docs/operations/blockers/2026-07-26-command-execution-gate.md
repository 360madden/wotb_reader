# Command-execution gate blocker record

First observed: `2026-07-26T21:18:46.329Z`  
Documented: `2026-07-26T21:23:27Z`

## BLK-0007: local command and MCP execution unavailable

- Affected milestones: all milestones that require restore, build, test,
  formatting, Git staging/commits, local game inspection, UI execution, or
  acceptance validation.
- Impact: source authoring and review can continue, but no newly authored
  changes can honestly be reported as compiled, tested, committed, or exercised
  after this point.
- Cause: an external approval/usage gate rejected command and MCP execution and
  indicated that access remains unavailable until `2026-08-02`.
- Resolution: honor the gate. Continue only work that can be performed through
  permitted repository edits and agent coordination. Do not use another tool,
  shell, process, or agent as an execution bypass.
- Why: validation evidence is trustworthy only when produced through authorized
  execution. Attempting to route around the gate would violate the execution
  boundary and could put the workspace at risk.
- Current validation status: all results explicitly dated before this blocker
  remain valid for the source state that produced them. Every edit made after
  the gate is marked pending and must be revalidated.
- Recovery: after execution is explicitly available, perform a locked restore,
  formatting verification, Release build, complete tests, vulnerability audit,
  doctor run, privacy scan, UI and overlay smoke tests, guarded game scenario,
  and Git diff review before any milestone commit.
- Prevention: validation documentation must always separate last-known-good
  evidence from unvalidated later source changes.

## Pending validation ledger

The following areas have post-gate or immediately pre-gate edits requiring a new
run:

- SQLite migration/storage final hardening;
- replay EOF-sentinel and descriptor cross-validation hardening;
- persistent replay-clock integration;
- bootstrap diagnostics and CLI tests;
- dashboard, streaming adapter, and overlay;
- Replay Sanitizer;
- guarded Game Harness;
- root validation script regression behavior; and
- solution-wide formatting, build, tests, audit, privacy, and secret scans.

## Resolution amendment — `2026-07-26T21:59:25Z`

Command execution became available again in a later session, before the
`2026-08-02` estimate. The ledger above was executed through
`scripts/validate.ps1 -AuditPackages` after repairing the never-compiled
post-gate source:

- locked restore, format verification, and the Release build passed with zero
  warnings after fixing eleven compile/analyzer errors (CLI argument parsing,
  web host references to the not-yet-authored dashboard services, replay
  decoder nullability, bootstrap logging types and culture, doctor service
  field types, and harness timeout/static analyzer findings);
- the complete test suite passed (95 passed, 2 opt-in installed-game tests
  skipped) after fixing a real `DiagnosticBundleService` defect the bootstrap
  tests exposed: zip entry streams were left open across entry creation and
  the archive stream was still open during the atomic rename;
- the transitive vulnerability audit reported no vulnerable packages;
- the repository scan passed after resolving BLK-0012 (ignore patterns hid
  `Diagnostics/` sources and `wwwroot/lib` assets); and
- the BLK-0006 guardrail was observed working: each failed phase terminated
  `scripts/validate.ps1` with a non-zero exit before later phases ran.

Still pending, tracked as future milestones rather than validation debt:
dashboard query/endpoint surface (`MapWebApi`, `DashboardQueryService`,
`MinimapProjector` were removed from composition until authored), overlay and
UI smoke tests, and the guarded game scenario.

