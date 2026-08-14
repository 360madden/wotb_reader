# Launch-marker ACL failure — module dependency root-caused and removed

**Date:** 2026-08-14 (UTC)

**Roadmap:** OD-099 persisted completion marker; clustered-live preflight

**Base commit:** `033b42a` (`chore(od): expose bounded ACL failure reason`)

## Result

The third pre-gate attempt emitted the new bounded reason:
`FAILED_launch_marker_directory_acl reason=exception-CommandNotFoundException`.
The game process count again stayed zero, so the replay remains unconsumed.

An exact nested Windows PowerShell 5.1 probe reproduced the discrepancy:
`Get-Item`, `New-Object`, and native `icacls` were available, but `Get-Acl`
was not. Explicitly importing `Microsoft.PowerShell.Security` also failed
because its extended type data was already registered. This explains why the
strict ACL itself passed in direct processes but the launcher verifier's
catch-all returned false in its nested process.

The verifier no longer depends on module auto-loading. It reads the same
owner/access sections through the Windows .NET `DirectorySecurity` and
`FileSecurity` constructors. Every security predicate remains unchanged:
directory reparse points fail, inheritance must be protected, the owner SID
must match, exactly one non-inherited owner Allow ACE must exist, and it must
grant FullControl. `icacls` remains the setter; only the post-set read
mechanism changed.

## Validation before milestone gate

- Completion-marker Pester suite: 14/14 passed under Windows PowerShell 5.1;
  the new case pins that the helper contains no `Get-Acl` dependency and uses
  both direct security readers.
- Exact nested-process proof with `Get-Acl` unavailable:
  `strictTest=true`, diagnostic `verified-after-retry-window`.
- `git diff --check`: passed.
- PSScriptAnalyzer repository gate: passed (99 non-fatal warnings reported).
- Post-attempt process check: 0 game processes; the temporary host was stopped
  before validation.

## Milestone gate

- `scripts/validate.ps1`: passed.
- Release build: 0 warnings, 0 errors.
- Tests: 1,205 passed, 7 local opt-in skips, 0 failed.
- PowerShell evidence suites: completion marker 14/14, replay selection 16/16,
  camera double-read 4/4, batch read-pass measurement 4/4.
- Repository scan, script-hygiene gate, offline-pack freshness/link checks,
  blocker/ledger consistency, and offset schema/chains validation: passed.

## Next

Retry the approved launch with the module-independent verifier. If it reaches
`OfflineReplayVerified`, immediately attach the batch, camera, and live-frame
evidence consumers to the same session; the damage-dealt chain owns completion
marker persistence and the final fast-reject re-run.
