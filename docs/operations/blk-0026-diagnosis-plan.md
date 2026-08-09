# BLK-0026 launch-diagnosis plan

- Prepared: `2026-08-09` (owner-delegated autonomy)
- Status: **executed and resolved** — root cause proven and fixed; launcher reaches
  `OfflineReplayVerified`; exactly one unchanged bounded OD-075 poll returned a
  positive verdict on the content-distinct replay. See `blocker-log.md` `BLK-0026`.
- Blocker: [`blocker-log.md`](blocker-log.md) `BLK-0026` — a content-distinct replay's
  managed launch exits before the `OfflineReplayVerified` gate; the Host stays
  `Unknown` / `session.initial`; no memory operation runs; no evidence result is
  created. Cross-replay continuous-polling repeatability remains unproved.

## Settled decisions (grilled 2026-08-09)

1. **Boundary** — strictly non-memory lifecycle evidence. No debugger attach before
   `OfflineReplayVerified`. A read-only Process Monitor capture is permitted only as
   a fallback if the ordinary sources fail to explain the exit.
2. **Evidence sources** — five, all lifecycle-only: launcher exit path/code; artifact
   marker state before/after; allowlisted `START_REPLAY_LOCAL` marker observation
   (no generic log tail — BLK-0004); process/window lifetime; Host
   `/api/v1/game/state` transitions (`verificationState` + `reasonCode` only).
3. **Hypothesis order** — (a) marker integrity first as a zero-cost check, then
   (b) replay decode/version, then (c)/(d). A *different-but-valid* marker for a
   different replay is normal; only absent / stale / malformed / non-owner-only /
   mismatch-with-selected-session is abnormal.
4. **Reproduction** — canonical launcher unchanged behind a read-only observation
   wrapper (scratch, gitignored under `.data/`). No diagnosis harness, no product
   code, no launcher-argument changes.
5. **Validation bar** — one reproduced failing causal signature plus one controlled
   resolved run (the specific cause gone, `OfflineReplayVerified` reached once).
   Intermittent causes: reproduce the signature twice. Then **exactly one** unchanged
   bounded OD-075 poll on the content-distinct replay — nothing more.
6. **Frozen surfaces** — position resolver, memory read surface,
   `memory-offsets/11.19.0.10.json`, and evidence/result classification. A pre-gate
   exit is a launch/evidence failure, never a negative memory read or justification
   to broaden scanning.

## Verified facts (2026-08-09, offline)

| # | Finding | Source | Consequence |
|---|---------|--------|-------------|
| 1 | **All three real `.data/launch` replays decode cleanly** (`crosscheck` exit 0, 13–17 s each); the content-addressed store matches the launch folder by exact byte size (1,045,525 / 1,100,265 / 829,216 / 2×990), so the content-distinct replay is **confirmed among the decoded set** | `scripts/invoke-replay-crosscheck.ps1` sweep + `.data/content/` ↔ `.data/launch/` size mapping | **Hypothesis (b) refuted** — the content-distinct replay is neither corrupt nor an unsupported version |
| 2 | The poll waits for `OfflineReplayVerified` (180 s) **before** the marker binding check; the **launcher also verifies the gate post-watch** (`post_watch_vs` → exit 4 `FAILED_gate_not_verified`) | `scripts/od-073-entity-position-poll.ps1` (`waiting_for_verified_gate` → `Get-LaunchArtifactId`); `scripts/launch-offline-replay-for-od.ps1` (lines ~625–634) | A binding failure alone cannot explain `session.initial`; the original failure was pre-gate, in launch/evidence establishment — the launcher's own `FAILED_gate_not_verified` (exit 4) is the most likely exit for the observed `session.initial` |
| 3 | Marker fail-closed rules: **absent / not owner-only / stale (>20 min) / malformed** → `FAILED_launch_artifact_binding` | `Get-LaunchArtifactId` in od-073 | The one unchanged poll must run **within 20 minutes** of a fresh import, or it fails closed at binding (after the gate) |
| 4 | Marker currently **stale**: present, owner-only, valid GUID, **767.8 min old** (checked via wrapper `-StaticOnly`) | `.data/diagnose-blk0026-launch.ps1` | Any poll against the current marker is refused at binding — a fresh import is a prerequisite for the eventual poll; this is a landmine, not the BLK-0026 cause |
| 5 | `Write-Od` writes only to the console — the failed attempts' lifecycle stream was **never persisted** | `launch-offline-replay-for-od.ps1` (function `Write-Od`) | The original exit point is unrecoverable; the wrapper must tee the stream for future runs |
| 6 | Complete launcher exit-path inventory (pre-gate): import — `FAILED_cli_missing_build_release_first`, `FAILED_replay_path_missing`, `FAILED_not_wotbreplay`, `FAILED_no_wotbreplay_in_game_folder`, `FAILED_replay_is_staging_copy_use_original`, `FAILED_import_parse`; marker/ACL — `FAILED_launch_marker_directory_acl`, `FAILED_launch_marker_acl`; launch POST — `FAILED_launch_http`, `FAILED_launch=<msg>`; window — `FAILED_no_window`; host readiness — `FAILED_host_missing_build_release_first`, `FAILED_host_stale_build`, `FAILED_host_down`; post-watch gate — `FAILED_gate_not_verified` (exit 4); other — `FAILED_host_denied_before_watch_restart_required`, `FAILED_game_died_during_settle`, `FAILED_unexpected` | launcher source (`grep -o "FAILED_[a-zA-Z_]*"`) | The wrapper's tee'd `od_launch:` stream pinpointed which path the failing attempts took: `FAILED_unexpected` |
| 7 | **Root cause mechanism (proven):** .NET `Set-Acl` with a fresh security descriptor throws `PrivilegeNotHeldException` (`SeSecurityPrivilege`) when the target **already has a protected owner-only ACL** — the marker dir/file persist between launches, so this hit on every launch after the first. `icacls /inheritance:r /grant:r` succeeds on any prior state. | instrumented launcher run (stack at `Set-OwnerOnlyDirectoryAcl` → `Set-Acl`) + controlled probe (fresh-object `Set-Acl` OK on a fresh target, throws on the same target again; `icacls` always OK) | The launcher's catch-all mapped the throw to `FAILED_unexpected` (exit 5) → no marker rewrite, no game launch, gate never verified — the exact `session.initial` signature. The ACL functions arrived in `1ad5381` (02:38Z), **after** the last successful launch (02:14Z), so no launch worked since |

## Updated hypothesis space

| # | Hypothesis | Status |
|---|-----------|--------|
| (a) | Launcher-side pre-game failure (import/marker/ACL/HTTP/window/post-watch gate) | **CONFIRMED and FIXED** — marker-ACL `Set-Acl` throw (fact 7); replaced with `icacls` in both owner-only ACL functions |
| (b) | Replay archive/version not decodable | **REFUTED** (fact 1) |
| (c) | Gate/timing: battle boundary or evidence-lifetime expiry revokes the gate before verification | **Not needed** — the resolved run reached `OfflineReplayVerified`; poll ran during the active battle |
| (d) | Host attach/identification failure on the fresh process | **Not needed** — Host identified the fresh process and verified the replay |

## Execution steps (live-testing phase) — completed 2026-08-09

1. **Wrapper launch** — `.data/diagnose-blk0026-launch.ps1` run in `-StaticOnly`
   (marker forensics) and in wrapped-launch mode. The wrapped tee reproduced the
   failing sequence (`replay_selected → … → importing → FAILED_unexpected`).
2. **Branch** — instrumented launcher copy surfaced the exact throw:
   `PrivilegeNotHeldException (SeSecurityPrivilege)` at `Set-OwnerOnlyDirectoryAcl`
   → `Set-Acl` (fact 7). Deterministic — one reproduction sufficed.
3. **Resolved run** — replaced `Set-Acl` with `icacls` in the launcher's two
   owner-only ACL functions (and the snapshot helper's identical copy). Launcher
   now reaches `OK OfflineReplayVerified` (exit 0) repeatedly: import → marker
   write → `launch.accepted` → window → dialog click → post-watch gate.
   Cold-boot note: the game can exceed the default 90 s window wait
   (`FAILED_no_window`); passed `-WindowWaitSeconds 240` for cold boots.
4. **Exactly one unchanged bounded OD-075 poll** — ran immediately after the gate
   during the active battle, well inside the 20-minute marker window, on the
   content-distinct replay (resolver/read-surface/offsets untouched).
   Result: `verdict=stable-resolver-positive`, `resolved=24/24`, `distinct=24`,
   `within1=12`, `within3=21`, `allModuleRooted=true`, `trajectoryConsistent=true`;
   aggregate result in `.data/od-073-entity-position-poll-*.json` (privacy flags
   all `false`). Managed game/Host processes stopped afterward.
5. **Record** — ledger result + dated handoff; BLK-0026 updated to resolved.

## Stop conditions

- Stop if the launcher reaches `OfflineReplayVerified` and the poll is then refused
  at binding (marker stale) — that is a solved operational constraint, not a blocker.
- Stop if the evidence points to a resolver/read-surface/offset change — that is
  explicitly forbidden by the blocker decision.
- Stop after **one** unchanged poll, regardless of outcome. No second poll, no
  broadening, no promotion.

## Privacy rules (tracked docs)

No replay path, artifact UUID, process address, PID, raw byte, player/account data,
or other private value is copied into tracked documentation. The plan references the
content-distinct replay generically; the wrapper's scratch logs stay under
gitignored `.data/`.

## Artifacts

- Diagnosis wrapper (scratch, gitignored): `.data/diagnose-blk0026-launch.ps1`
- This plan: `docs/operations/blk-0026-diagnosis-plan.md`
- Blocker record: `docs/operations/blocker-log.md` `BLK-0026`
- Poll result (scratch, gitignored): `.data/od-073-entity-position-poll-*.json`
- Scratch diagnostics/logs (gitignored): `.data/diagnostics/`
