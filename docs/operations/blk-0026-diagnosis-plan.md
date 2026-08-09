# BLK-0026 launch-diagnosis plan

- Prepared: `2026-08-09` (owner-delegated autonomy; **no live testing performed yet**)
- Status: plan encoded; hypothesis (b) refuted offline; execution awaits live-testing approval
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
| 6 | Complete launcher exit-path inventory (pre-gate): import — `FAILED_cli_missing_build_release_first`, `FAILED_replay_path_missing`, `FAILED_not_wotbreplay`, `FAILED_no_wotbreplay_in_game_folder`, `FAILED_replay_is_staging_copy_use_original`, `FAILED_import_parse`; marker/ACL — `FAILED_launch_marker_directory_acl`, `FAILED_launch_marker_acl`; launch POST — `FAILED_launch_http`, `FAILED_launch=<msg>`; window — `FAILED_no_window`; host readiness — `FAILED_host_missing_build_release_first`, `FAILED_host_stale_build`, `FAILED_host_down`; post-watch gate — `FAILED_gate_not_verified` (exit 4); other — `FAILED_host_denied_before_watch_restart_required`, `FAILED_game_died_during_settle`, `FAILED_unexpected` | launcher source (`grep -o "FAILED_[a-zA-Z_]*"`) | The wrapper's tee'd `od_launch:` stream will pinpoint which of these paths the next attempt takes |

## Updated hypothesis space

| # | Hypothesis | Status |
|---|-----------|--------|
| (a) | Launcher-side pre-game failure (import/marker/ACL/HTTP/window/post-watch gate) | **Open** — leading; the tee'd `od_launch:` stream will confirm or refute |
| (b) | Replay archive/version not decodable | **REFUTED** (fact 1) |
| (c) | Gate/timing: battle boundary or evidence-lifetime expiry revokes the gate before verification | **Open** — Host-state sampler evidence needed |
| (d) | Host attach/identification failure on the fresh process | **Open** — Host-state sampler evidence needed |

## Execution steps (live-testing phase only)

1. **Wrapper launch** — run `.data/diagnose-blk0026-launch.ps1` (default mode) with
   the canonical launcher arguments; it tees the `od_launch:` stream, samples Host
   state every 5 s, and records marker state + launcher exit code. One launch.
2. **Branch** — from the tee'd stream: a `FAILED_*` exit pinpoints (a); a live window
   that dies before the gate points to (c)/(d). Reproduce the failing signature
   (twice if intermittent).
3. **Resolved run** — fix the specific surviving cause on the diagnosis side only
   (e.g., fresh import for a stale/mismatched marker; re-export if decode ever
   regresses), reach `OfflineReplayVerified` once. One cause per run.
4. **Exactly one unchanged bounded OD-075 poll** — within 20 minutes of the fresh
   import (fact 3), on the content-distinct replay, resolver/read-surface/offsets
   untouched.
5. **Record** — ledger result + dated handoff; then update BLK-0026.

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
