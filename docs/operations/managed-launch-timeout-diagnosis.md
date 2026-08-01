# Managed launch timeout — root-cause diagnosis

**Date:** 2026-07-31
**Status:** Analysis only — no code changed, no game process attached or scanned.
**Context:** Supplies the "diagnose the managed-launch timeout and
lifecycle-correlation boundary" prerequisite for `OD-RECOVERY-003` (see
[`offset-discovery-ledger.md`](offset-discovery-ledger.md)). Every claim below
is grounded in the code at HEAD `684a6bf`; line references are approximate.

## Observed symptoms (from the ledger and handoffs)

- `OD-RECOVERY-002-BLOCKED` (2026-07-31): replay artifact staging completed, but
  the launch timed out before a correlated process/lifecycle result appeared.
- The host stayed at `verificationState=Unknown` with `reasonCode=launch.awaiting_evidence`.
- Follow-up inventory found 4–5 responsive `wotblitz.exe` processes with mixed
  parentage; no existing process was admissible because no host lifecycle
  evidence matched a launch correlation.

## How the launch actually behaves

`POST /api/v1/game/launch` → `GameApiEndpoints.LaunchGameAsync` →
`GameSessionCoordinator.LaunchCoreAsync`. The pipeline (prep → executable lease
→ artifact staging → suspended process → correlation → resume → handoff → record)
runs **synchronously** and returns `launch.accepted` **immediately**. It does
not wait for game evidence. Evidence is gathered afterwards by a fire-and-forget
background loop (`StartMonitoringLifecycle`, `Task.Run`), which polls the
lifecycle feed every 500 ms looking for an `OfflineReplayStarted` marker.

So a "launch timeout" is one of two things:

1. A pipeline stage hung (the HTTP request never returned), or
2. The request returned `launch.accepted`, but `OfflineReplayVerified` never
   arrived and the caller's bounded wait expired (GameHarness uses 5 s / 30 s /
   2 min timeouts; see `tools/src/WotBTreader.GameHarness/Program.cs`).

The ledger state (`Unknown / launch.awaiting_evidence`) is exactly the snapshot
written by `RecordManagedLaunch` — i.e. **no evidence was ever applied after the
launch returned**. That points at hypothesis 2: the request completed, and the
background monitor never observed a qualifying `OfflineReplayStarted` marker.

## Primary hypothesis (ranked)

### H1 — No `OfflineReplayStarted` marker was observed after launch (highest confidence)

The monitor only transitions to `OfflineReplayVerified` inside
`StartMonitoringLifecycle` when an `OfflineReplayStarted` marker event arrives
from the lifecycle feed. `Evaluate` then additionally requires:

- lifecycle state `OfflineReplayStarted` and `ReplayUiConfirmed`;
- `_managedLaunch` non-null, same PID / start identity / launch correlation;
- cursor validity (`IsCursorValid`): the marker must come from the **same
  source identity and generation** captured in the launch baseline, with a
  sequence strictly greater than the baseline sequence.

If the game never writes a qualifying marker, the coordinator keeps returning
`Unknown / launch.awaiting_evidence` forever. Contributing reasons a marker may
never appear:

- **H1a — the launched child exited immediately (single-instance behavior).**
  The research notes flag "the game is single-instance (unverified)" and the
  inventory found multiple responsive `wotblitz.exe` processes. If the child
  exits right after `Resume` (an existing instance owns the game), no marker is
  written by that process. The monitor cannot detect this: it only checks
  process liveness **inside** the marker branch, so an exited child is
  invisible and the loop silently polls forever.
- **H1b — the game did not auto-play the staged replay.** Research
  (`research/approaches.md`, `research/replay-loading-mechanisms.md`) documents
  that invoking `wotblitz.exe "replay.wotbreplay"` may open the main menu or the
  "Uploaded" tab rather than playing the local replay. No local replay start →
  no `START_REPLAY_LOCAL` / `Start replay event` line in the native log.
- **H1c — the native log the child writes is not tailed by the feed.** The feed
  tails `blitz-logs_*.txt` under the configured user-data roots and
  `%LocalAppData%\wotblitz\DAVAProject` (`BlitzReplayLifecycleFeed.TryGetLogDirectories`).
  If the launched child's user-data root differs (different account, install-dir
  logs, DAVAProject path mismatch), the marker is never observed.

### H2 — A pipeline stage failed or stalled instead (lower confidence, ruled in only if H1 is disproven)

`ManagedLaunchPreparer.PrepareAsync` captures a reconciled lifecycle baseline
(`CaptureReconciledBaselineAsync`) and fails fast if the feed is not healthy
(`game.launch.lifecycle_unhealthy`). The barrier mechanics do not deadlock: the
producer loop calls `CompleteBarriersThrough` on every iteration regardless of
reconcile success, and the producer's catch/finally completes all pending
barriers, so a barrier resolves within one `LogReconciliationInterval` (default
5 s) even on failure. What a degraded feed *can* do is fail the launch with
`lifecycle_unhealthy` (or delay the pipeline by up to one reconciliation
interval) — a fast, stable failure, not a hang. The ledger's "staging
completed" wording already argues against a stage failure here (staging is a
later stage), so H2 is the least likely branch.

## Secondary findings (code-level, independent of H1/H2)

1. **The monitor fabricates process evidence.** In `StartMonitoringLifecycle`,
   `GameProcessEvidence` is constructed with constant
   `IsAlive: true`, `WindowHandle: 1`, and `ReplayUiConfirmed: true`
   (`WindowOwnerProcessId` is the real launched child PID). The coordinator's
   "verification" of the child is synthetic — the real query-only observer
   (`GameProcessIdentityObserver`) exists in the codebase but is **not** wired
   into the monitor. This is both a diagnosability gap (no signal about whether
   the child is actually alive) and an evidence-integrity gap (the roadmap's M2
   design says identity evidence must be observed, not assumed).
2. **No server-side evidence deadline.** After `launch.accepted`, the server has
   no timer; the caller's polling timeout is the only bound. A caller cannot
   distinguish "evidence pending" from "evidence will never arrive".
3. **No launch correlation is surfaced to the caller.** `GameLaunchResponse`
   carries only `Success` + `Message`; the child PID and launch correlation
   never leave the adapter, which is why the follow-up inventory could not
   attribute any process. (This is deliberate privacy/containment, but it
   blocks diagnostics.)
4. **The child is launched hidden** (`SwHide` in
   `WindowsSuspendedProcessPlatform`). Combined with hardcoded
   `WindowHandle: 1`, the window-identity requirement in `IsProcessIdentityValid`
   (`WindowHandle != 0`) is satisfied only by fabrication.

## Proposed fixes (ranked)

1. **Wire the real query-only process observer into the monitor.**
   Replace fabricated `GameProcessEvidence` with observed liveness/window
   evidence from `GameProcessIdentityObserver`. If the child exits after launch,
   surface a distinct reason (e.g. `process.exited_after_launch`) instead of
   silently staying `Unknown`. This is the highest-value fix: it turns the
   dead-zone into a diagnosable state and restores evidence integrity.
2. **Add a server-side evidence deadline.** If `OfflineReplayVerified` is not
   reached within a bounded window (e.g. 60 s) after `launch.accepted`,
   transition to a distinct terminal reason (e.g. `launch.no_evidence`) so
   callers stop polling and receive an actionable code.
3. **Surface bounded launch metadata for diagnostics.** Add the launch
   correlation (not the child PID/paths) to `GET /api/v1/game/state` or a
   `launch` sub-route so the next OD session can attribute the correct process.
   Keep paths and PIDs off the wire per the privacy rule.
4. **Test single-instance behavior live** before the next recovery attempt:
   invoke the installed executable with a replay argument while an instance is
   running (per `research/approaches.md`). If single-instance, the suspended
   pipeline should deny early with a stable code
   (e.g. `launch.instance_already_running`) instead of launching a doomed child.
5. **Verify the tailed log path matches the launched child's user-data root**
   during a live attempt; if not, the feed's source configuration must be
   corrected before any marker can be observed.

## Amendment (2026-07-31) — fix #2 implemented

Proposed fix **2 (server-side evidence deadline)** is now implemented in
`GameSessionCoordinator` + `GameIntegrationOptions`:

- `GameIntegrationOptions.EvidenceDeadline` (default 60 s, validated 5 s – 10 min).
- The deadline starts when a managed launch is recorded; if
  `OfflineReplayVerified` is not reached before it elapses, the session
  transitions to a terminal `launch.no_evidence` state and the monitor stops
  polling (`ApplyEvidenceDeadline`). The transition is also applied lazily on
  `GetSnapshotAsync`, so the terminal state is observable even if the
  background monitor task dies.
- Verification disarms the deadline — a later evidence expiry surfaces as
  `evidence.stale` / `evidence.expired`, never `launch.no_evidence`.
- Late evidence after the terminal state is rejected (`_evidenceDeadlineApplied`).

Fixes **1 (wire the real observer), 3 (bounded diagnostics), 4 (single-instance
probe), 5 (log-path verification)** remain open and still require a live session.
`OD-RECOVERY-003` remains `Planned` in the ledger.

## Amendment (2026-08-01) — fix #1 implemented

Proposed fix **1 (wire the real query-only process observer into the monitor)**
is now implemented in `GameSessionCoordinator`:

- The coordinator takes `IGameProcessIdentityObserver` (already DI-registered).
  The monitor no longer constructs `GameProcessEvidence` with hardcoded
  `IsAlive: true`, `WindowHandle: 1`, `ReplayUiConfirmed: true`; it observes
  the launched child on **every** poll iteration and maps the result via
  `BuildObservedProcessEvidence`.
- `Available` with the observed PID matching the launched child → the observed
  identity builds the evidence (real window handle, observed
  path/version/hash/start identity); `ReplayUiConfirmed` is only set when a
  real eligible window is observed.
- An `OfflineReplayStarted` marker is remembered as a pending marker instead
  of being applied immediately, so a marker whose window appears a moment
  later (the child launches hidden) still verifies once the window is
  observed. `SetUnverified` never advances the evidence cursor, so applying
  the pending marker later remains cursor-valid.
- `Absent` maps to a candidate `Exited` outcome, but the child launches hidden
  (`SwHide`) and window eligibility requires a visible `SDL_app` window, so
  absence cannot by itself prove exit. The monitor disambiguates with
  `IsChildProcessAlive`: a **confirmed-exited** child is terminal
  `Deny("process.exited_after_launch")` via `ReportProcessExitedAfterLaunch`
  (no-op when no launch is active or the evidence deadline already closed the
  session); an alive-but-not-yet-visible child keeps polling (the deadline
  bounds the launch).
- `Ambiguous` / `QueryFailed` / `Unsupported` / other-instance PID → no
  evidence is applied, and the monitor keeps polling until the evidence
  deadline bounds the launch.
- The observed start identity comes from the query-only process query session;
  `Process.GetProcessById` is used only for the liveness disambiguation above.

Fixes **3 (bounded diagnostics), 4 (single-instance probe), 5 (log-path
verification)** remain open and still require a live session. `OD-RECOVERY-003`
remains `Planned` in the ledger.

## Amendment (2026-08-01) — fix #3 implemented

Proposed fix **3 (surface bounded launch metadata for diagnostics)** is now
implemented:

- `GameSessionSnapshot` (Application) and `GameStateResponse` (ApiContracts)
  each carry a nullable `LaunchCorrelation` — the adapter-generated GUID, never
  a PID, path, or other machine-identifying value.
- `GameSessionCoordinator` tracks `_lastLaunchCorrelation`, set on every
  `RecordManagedLaunch` and deliberately **not** cleared by `RevokeSession`, so
  the owning launch of a failed/denied session (`launch.no_evidence`,
  `process.exited_after_launch`) remains attributable from
  `GET /api/v1/game/state`. A new launch replaces it.
- `GameApiEndpoints.GetGameStateAsync` maps the correlation onto the wire.
- Tests: 5 coordinator tests (null before launch, exposed after launch,
  persists through both terminal states, replaced by next launch) and 2
  endpoint tests (maps correlation; maps null when no launch).
- Gate hygiene: `research/approaches.md` and `research/memory-analysis.md`
  carried committed absolute replay paths with a username; both were redacted
  to `%LOCALAPPDATA%` placeholders. `scan-repository.ps1` passes (514 files).

Fixes **4 (single-instance probe) and 5 (log-path verification)** remain open
and still require a live session. `OD-RECOVERY-003` remains `Planned` in the
ledger.

## What was NOT done

- No live game process was started, attached, resumed, or scanned.
- No offset table, ledger status, or runtime authorization was changed.
- This document is analysis evidence only; `OD-RECOVERY-003` remains `Planned`
  in the ledger.
