# Handoff: OD-044 replay-start flake — root cause and fix

**Date:** 2026-08-04
**Status:** Complete — both death signatures root-caused, fixed, and validated live
**Campaign:** WoT Blitz PC offset discovery (follow-up to OD-RECOVERY-044)

## Repository state

- Branch `main`, head `0c80a692` (`docs(ops): amend OD-044 launcher rules-out after
  headless re-verification`).
- Working tree carries the 3 fix files below plus this handoff and the
  ledger/knowledge updates. The pre-existing unstaged edit to
  `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md` predates this
  session; it was left untouched and uncommitted.

## Changed files and public contracts

- `scripts/click-watch-offline.ps1` — marker-based click-stop + focus behavior
  (below). CLI parameters and exit codes unchanged.
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` — new
  internal `RefreshVerifiedEvidence` liveness heartbeat. Internal API only; no
  public contract change.
- `tests/WotBTreader.GameIntegration.Tests/GameSessionCoordinatorTests.cs` — 5 new
  tests for the heartbeat (extension, identity-drift deny, and the no-op guards).
- `docs/operations/offset-discovery-ledger.md`, `knowledge.md` — record-only updates.

## Summary

The replay-start flake that killed ~50% of OD-044 managed launches was **two
independent defects**, both now fixed. The "~2s" death in the original brief is one
variant of defect 1; the "game window closes unexpectedly" exits at ~60–105s were
defect 2.

### Defect 1: round-2 double-click into the live replay HUD (+ SW_RESTORE churn)

The game reaches `LoadGameScene`, then the window is hidden and the game
backgrounds itself (`become hidden` → `OnBackground`, no crash dump, no
`WindowDestroyed`). `click-watch-offline.ps1` clicked first and polled the Host
gate; when lifecycle evidence took ~9–10s to flip `OfflineReplayVerified`, the
round loop judged `dialogGone=False` and round 2 re-clicked — but the orange blob
it re-detected was the replay-HUD false positive (OD-017). That second click plus
the `ShowWindow(SW_RESTORE)`/foreground churn landing in a fragile window state hid
the window. Latency after scene load ranged 2s / 16s / 42s; deaths came only after
successful dialog dismissal (2 of 3 after `Start replay event`), i.e. mid-roll
kills matching the OD-044 ledger note. Correlation across 9 launches: 4/4
single-click runs survived; double-clicked runs died 2/5.

### Defect 2: `OfflineReplayEvidenceLifetime` expiry terminated the game mid-battle

The Host terminates the managed game (`RevokeSession(terminateProcess: true)`)
when verified authorization expires. The DAVA log goes silent during replay
playback, so no fresh `Start replay event` markers ever arrive; the 120s lifetime
(option maximum) expired ~120s after verification and the game was killed —
"window closes unexpectedly". The ~103s/65s validation exits and several OD-044
roll deaths (10s/30s/~90s) were this path, not the replay ending: the OD-044
replay holds ~281s of position data with the player tank alive through the end, so
every sub-281s exit was premature.

## Fixes

- **watch_offline marker stop:** the script now treats the blitz-log
  `START_REPLAY_LOCAL` / `Start replay event` marker (written at dialog dismissal,
  seconds before the Host gate flips) as fast ground truth. Round 1 still clicks a
  visibly-present dialog — the game can auto-start playback ~8s after the dialog
  appears without dismissing it, and leaving the dialog up over the live replay
  causes an early teardown — but rounds ≥ 2 never click once the marker fired. All
  `ShowWindow(SW_RESTORE)` churn removed in favor of throttled soft
  `SetForegroundWindow` (+ `ForceForeground` without SW_RESTORE before a click).
  The marker log is re-resolved per check against the game process start time so a
  stale prior-session log cannot satisfy it; visual-only mode accepts the marker
  as success (the OD-017 orange-HUD false positive makes the pixel check
  unreliable once playback is live).
- **Coordinator liveness heartbeat:** `StartMonitoringLifecycle` now calls the new
  `RefreshVerifiedEvidence` every ~500ms once the launch is verified; while the
  process identity stays valid (same pid/start identity, window owned, executable
  hash and version match the trusted identity), the authorization expiry rolls
  forward by `OfflineReplayEvidenceLifetime` each beat, so a healthy game is never
  terminated mid-replay. Fail-closed paths unchanged: process death, window loss,
  identity drift, `OfflineReplayStopped` markers, and the next managed launch all
  still revoke immediately. Throttled (30s) Debug heartbeat log for diagnostics.

## Validation

- **Live launch (final, post-fix):** the game played the full ~281s replay to its
  natural battle end — it survived ~3 min past the old ~120s kill point with no
  `become hidden`/`OnBackground`; the Host gate stayed `OfflineReplayVerified` with
  the evidence expiry rolling forward live. Earlier validation launches confirmed
  the marker stop (single click, `replay_started_marker (no further clicks)`) and
  that the game no longer dies at the 2s/16s/42s points.
- **Build:** `dotnet build -c Release` → 0 errors (the only build failures seen
  were DLL locks from the still-running validation host, not code errors).
- **Tests:** full solution suite green — 546 passed, 0 failed (2 opt-in skips);
  `WotBTreader.GameIntegration.Tests` 232 passed.
- **Script:** PS parser, 0 errors; no `ShowWindow` call remains (4 comment-only
  mentions).

## Assumptions and unknowns

- The hangar-first-then-replay sequence remains untested: every retained log
  (08-03 evening + 08-04) is an argv launch (`PreLogin → SmartDlc → BattleLoading`,
  zero Lobby/Hangar controllers), so a true hangar-first flow would skip
  `LoginOnReplayDialog` and the scripted-click churn entirely, but there is no
  evidence either way yet.
- No WER reports or crash dumps on any death: the game was hidden/killed, never
  crashed. The kernel-clock survivor (`KUSER_SHARED_DATA.SystemTime`) was the
  artifact of a dying process, as the OD-044 ledger suspected.
- The heartbeat substitutes process liveness for replay markers during playback.
  It is bounded to the managed-launch pipeline, which only ever produces offline
  replay sessions (offline session gate + argv replay), so a live, identity-matched
  process of the trusted executable cannot be an online session.

## Integration risks

- `watch_offline` visual-only success is now marker-based; the pixel check remains
  as the fallback. The color-blob spec docs
  (`docs/superpowers/specs/2026-08-02-watch-offline-color-blob.md`,
  `2026-08-02-watch-offline-sync-ready-gate.md`) still describe the older
  pixel-only dual-verify and should be amended to the marker-based stop.
- `launch-offline-replay-for-od.ps1` / `od-018-session.ps1` still set
  `LifecycleEvidenceTimeoutSeconds=120` and the lifetime knob at 120s; with the
  heartbeat these are rolling ceilings rather than hard limits, but the docs should
  say so to avoid future confusion.
- The `OfflineReplayEvidenceLifetime` maximum (2 min) remains a floor for the
  heartbeat step, not the session; no option change was needed.

## Recommended next steps

1. Amend the watch-offline specs and the launch/OD driver comments to the
   marker-based stop + rolling-lifetime model (small doc pass).
2. Re-run an OD-045 prep with the fixed scripts and heartbeat host; the delta-pilot
   priority from OD-044 stands.
3. Optionally run the hangar-first experiment to retire the remaining dialog-path
   risk.
