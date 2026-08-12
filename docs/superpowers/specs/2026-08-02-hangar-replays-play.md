# Design: Hangar → Profile → REPLAYS → play

Date: 2026-08-02  
Status: approved-to-implement (owner: hangar UI crops)  
Scope: Start (or attach to) the live game, open LATEST replay via hangar UI, confirm offline playback.

## Goal

Get an offline replay running by navigating the logged-in hangar UI.

**Success:** in-game offline playback (`START_REPLAY_LOCAL` in blitz-log). Logged-in hangar play often skips `LoginOnReplayDialog`; chain `click-watch-offline.ps1 -VisualDismissOnly` only when that dialog appears.

**Non-goal:** Host `OfflineReplayVerified` / discover gate. Managed argv launch stays in `scripts/launch-offline-replay-for-od.ps1` for later OD work.

## Why not argv launch

Managed `wotblitz.exe "replay"` forces `LoginOnReplayDialog`; a click-timing
miss then wastes the session. **Error 126 is NOT click timing — it is a
replay client-version mismatch** (the game refuses a replay whose
major.minor version family differs from the installed game; root-caused
2026-08-12: an 11.18.0 replay against an 11.19.0.10 game). The launcher now
probes the replay version pre-flight and refuses mismatches before the
launch dance. Hangar path uses the world where orange **Battle** is visible.

## Flow

```text
EnsureGame (attach HWND or Start-Process wotblitz.exe, no replay argv)
  → LookingForHangar (Battle orange, top-third center, stable)
  → ClickProfile (top-left hex)
  → ClickReplays (top-right REPLAYS)
  → ClickPlay (first/top play control on LATEST)
  → ConfirmPlayback (START_REPLAY_LOCAL and/or chain click-watch-offline.ps1)
```

## Dim / overlay rule

**Do not click** while a modal overlay dims the UI (low mean luminance in the active ROI). Sync and Error dialogs block input; wait for bright ready or fail with ErrorDialog exit.

## Default ROIs (client ratios)

| Target | x0–x1 | y0–y1 | Notes |
|--------|-------|-------|-------|
| Battle | 0.35–0.65 | 0.08–0.28 | Strong orange blob = hangar ready |
| Profile | 0.00–0.12 | 0.00–0.14 | Hex emblem; template preferred |
| REPLAYS | 0.72–0.98 | 0.05–0.22 | After profile menu opens |
| Play | 0.08–0.42 | 0.28–0.55 | **Inside white triangle** (bright-pixel centroid; not dark circle) |

## Templates

Owner UI crops (no account chrome) under `scripts/ui-templates/hangar/`:

- `profile-hex.png`
- `replays-label.png`
- `play-triangle.png`

Template match in ROI when loadable; otherwise ROI + color heuristics.

## Which replay

First visible play control on the **LATEST** tab. No import/staging in this unit.

## Implementation

`scripts/play-replay-from-hangar.ps1`

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Playback confirmed |
| 1 | No game window / start failed |
| 2 | Hangar Battle never appeared |
| 3 | Profile / REPLAYS / play sequence failed |
| 4 | Unexpected |
| 5 | Error dialog / replay failed after play |

## Safety

Offline hangar + local replay list only. Never automate online Battle. Do not log account IDs, full paths, tokens, or screenshots into the repo.

## Live smoke (2026-08-02)

| Attempt | Result |
|---------|--------|
| Cold start ×2 (post white-triangle + log-cursor fix) | Exit 0 `OK START_REPLAY_after_play_click`; blitz-log `Start replay event`; play @ client 218,316 white left-card |
| LoginOnReplay / Watch Offline chain | Not entered — logged-in hangar play starts replay without that dialog (expected) |

Command: `powershell -File scripts/play-replay-from-hangar.ps1`
