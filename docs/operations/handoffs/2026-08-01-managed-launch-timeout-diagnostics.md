# Session handoff — 2026-08-01: managed-launch timeout diagnostics

**Status:** implementation milestone complete; live replay verification remains pending.

## What changed

- Added `GameIntegrationOptions.LifecycleEvidenceTimeout`, defaulting to 45 seconds
  and bounded to 5 seconds through 5 minutes.
- Added structured managed-launch stage logging for preparation, executable lease,
  artifact staging, suspended process creation, correlation, resume, handoff, and
  lifecycle evidence.
- Added a bounded lifecycle-evidence wait. Missing correlated evidence now fails
  closed as `Denied` with `launch.lifecycle_evidence_timeout`.
- On timeout, the active identity-bound handed-off child is termination-requested
  before the session is revoked. Lease locking serializes termination and disposal.
- Replacement launches and coordinator disposal terminate unverified handed-off
  children; verified children remain alive. A termination request is retried during
  lease disposal before the process handle is released.
- Added timeout-bound validation tests and documented the operator diagnostics,
  event IDs, timeout behavior, and next-session procedure.

## Diagnostic interpretation

- `ManagedLaunchStage` (event 3135): normal stage transition.
- `ManagedLaunchStageFailed` (event 3136): stable operation-result failure.
- `ManagedLaunchLifecycleEvidenceTimeout` (event 3140): no correlated lifecycle
  marker before the configured deadline; includes process ID and termination result.

A timeout is a launch-gate failure, not discovery evidence. Existing or ambiguous
processes must not be reused for scanning.

## Validation

- Release solution build: passed, 0 warnings/errors.
- GameIntegration tests: 169 passed, 2 opt-in skips, 0 failed.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.

## Remaining limitation

The timeout path is covered by configuration validation and existing lifecycle
state tests, but a fully deterministic test of the detached monitor's native
termination path still needs a process-termination seam. Monitor shutdown is
owned asynchronously to avoid synchronous cancellation callbacks while the
coordinator lock is held. The next live session
should capture the stage log and confirm whether the host reaches
`OfflineReplayVerified` before any memory scan.
