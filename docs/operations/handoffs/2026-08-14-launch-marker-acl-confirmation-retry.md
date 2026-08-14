# Launch-marker ACL confirmation retry — pre-gate failure hardened

**Date:** 2026-08-14 (UTC)

**Roadmap:** OD-099 persisted completion marker; clustered-live preflight

**Base commit:** `a892de0` (`fix(od): persist batch read measurements`)

## Result

The first clustered-live attempt failed safely before the game process was
created. Replay version probing passed (11.19.0 against installed
11.19.0.10), the loopback host started, and launch setup stopped at
`FAILED_launch_marker_directory_acl`. The game count remained zero, so the
replay was not consumed and no completion marker was written.

Post-failure inspection showed the launch-marker directory already had the
exact required owner-only ACL: protected inheritance, one non-inherited Allow
ACE for the current owner SID, FullControl with container/object inheritance,
and no reparse point. The same verifier passed immediately after the launcher
exited. The narrow evidence supports a transient immediate post-`icacls`
verification miss; the verifier's existing catch-all intentionally hid the
underlying Windows exception, so no more specific cause is claimed.

The security requirement is unchanged. New confirmation helpers retry the
same strict file/directory verifier up to five times at 100 ms intervals after
`icacls`; they never add an ACE, accept inheritance, bypass a reparse point,
or continue after exhaustion. Both launch-marker creation and completion-
marker writes use the bounded confirmation. Native setup still fails closed
if the ACL never verifies.

## Validation

- Completion-marker Pester suite: 11/11 passed under Windows PowerShell 5.1.
  Added cases prove transient directory verification retries, permanent file
  failure remains false, and the launcher uses both confirmation helpers.
- Launcher parse check and `git diff --check`: passed.
- PSScriptAnalyzer repository gate: passed (99 non-fatal warnings reported).
- Post-failure process check: 0 game processes; the temporary loopback host
  was stopped before validation.
- Full `scripts/validate.ps1`: 1,205 tests passed with 7 local opt-in skips;
  Release build 0 warnings/0 errors; repository scan, PSScriptAnalyzer,
  Pester 11/11 + 16/16 + 4/4 + 4/4, offline pack, blocker/ledger consistency,
  and eight-chain schema validation passed.

## Next

Retry the same approved clustered launch. This pre-gate attempt did not touch
the game, so top-10 actions 1–4 remain pending and can still share one actual
replay start.
