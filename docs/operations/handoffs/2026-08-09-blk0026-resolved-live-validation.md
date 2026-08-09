# BLK-0026 resolved: root cause proven, launcher fixed, one unchanged poll positive

Date: 2026-08-09
Status: milestone complete — blocker resolved and validated with live evidence
Scope: live launch/evidence validation (approved), ACL root-cause fix, exactly
one unchanged bounded poll; product code, resolver, and read surface untouched

## Session summary

BLK-0026 is **resolved and validated**. The content-distinct replay's managed
launch failure was a launcher regression, now fixed; the launch reaches
`OfflineReplayVerified`, and exactly one unchanged bounded OD-075 poll returned
a positive verdict on the content-distinct replay during the active battle.

## Root cause (proven, not inferred)

- The marker-ACL functions added to `scripts/launch-offline-replay-for-od.ps1`
  in `1ad5381` (2026-08-09T02:38Z) threw `PrivilegeNotHeldException`
  (`SeSecurityPrivilege`) inside .NET `Set-Acl` — **whenever the target already
  has a protected owner-only ACL**, i.e. every launch after the first, because
  the `od-launch` marker directory/file persist between launches.
- The launcher's catch-all turned the throw into `FAILED_unexpected` (exit 5):
  no marker rewrite, no game launch, gate never verified — the exact
  `session.initial` signature recorded for BLK-0026.
- Timeline is decisive: the last successful launch (02:14Z) predates
  `1ad5381`, so **no launch worked after the ACL code landed**.
- Evidence: instrumented launcher copy printed the exception type/message and
  stack (throw at `Set-OwnerOnlyDirectoryAcl` → `Set-Acl`); a controlled probe
  proved fresh-object `Set-Acl` succeeds on a fresh target and throws on the
  same target the second time, while `icacls` succeeds on any prior state.

## Fix

- Replaced .NET `Set-Acl` with `icacls /inheritance:r /grant:r` in
  `Set-OwnerOnlyFileAcl` and `Set-OwnerOnlyDirectoryAcl` (launcher), and the
  identical copy in `scripts/publish-instruction-snapshot-helper.ps1`.
- `icacls` needs no security-descriptor privileges, preserves the current-user
  owner, and produces exactly the single owner FullControl rule the existing
  `Test-OwnerOnly*` checks expect. The `Test-OwnerOnly*` fail-closed checks
  (`FAILED_launch_marker_directory_acl`, `FAILED_launch_marker_acl`) are
  unchanged.

## Live validation

1. **Resolved runs** — the fixed launcher reached `OK OfflineReplayVerified`
   (exit 0) repeatedly: `importing → artifact_imported → managed_launch →
   launch.accepted → window_after=N s → dialog click → post_watch_vs=
   OfflineReplayVerified`. Cold-boot note: a cold game boot can exceed the
   default 90 s window wait (`FAILED_no_window`); the validated runs passed
   `-WindowWaitSeconds 240`.
2. **Exactly one unchanged bounded OD-075 poll** — ran immediately after the
   gate during the active battle (well inside the marker's 20-minute window)
   with the poll's default bounded campaign:
   `verdict=stable-resolver-positive`, `resolved=24/24`, `distinct=24`,
   `within1=12`, `within3=21`, `allModuleRooted=true`,
   `trajectoryConsistent=true`, bound to `launch-artifact-newest-decode`
   (the content-distinct replay). Aggregate result file under
   `.data/od-073-entity-position-poll-*.json` with all privacy flags `false`
   (no entity IDs, coordinates, addresses, raw bytes, or capability persisted).
3. **No second poll, no promotion, no resolver/read-surface/offset changes** —
   per the blocker decision. Managed game/Host processes stopped afterward.

## Operational notes for the next session

- The marker is written fresh by every launcher run; the poll must run within
  20 minutes of import or binding fails closed (after the gate).
- The launcher's `od_launch:` stream is console-only — the scratch wrapper
  (`gitignored` `.data/diagnose-blk0026-launch.ps1`) tees it with timestamps
  for future runs.
- `Start-Process -NoNewWindow` + `-RedirectStandardOutput` hangs in Windows
  PowerShell 5.1; background-with-file-redirect avoids the pipe-inheritance
  hang (Host.Web keeps the launcher's stdout handle open).

## Files

- Fixed: `scripts/launch-offline-replay-for-od.ps1`,
  `scripts/publish-instruction-snapshot-helper.ps1`
- Docs: `docs/operations/blocker-log.md` (BLK-0026 resolved),
  `docs/operations/blk-0026-diagnosis-plan.md` (execution complete)
- This handoff: `docs/operations/handoffs/2026-08-09-blk0026-resolved-live-validation.md`
