# Research Findings — Executive Summary

## The Big Question

**Can we keep WoT Blitz running and switch between replays without restarting?**

## The Answer: Partially.

### What We Know (High Confidence)

1. **Command-line replay launch works.** WoT Blitz accepts a replay path as the
   first command-line argument: `wotblitz.exe "replay.wotbreplay"`. This is the
   standard Windows file-association pattern. The codebase already uses this.

2. **The game has an in-app replay browser.** Accessible from `Profile → Replays`
   with three tabs: Recent (100 battles), Favorites, Uploaded. The Uploaded tab
   accepts externally-opened replay files.

3. **No tank game supports true hot-swapping.** WoT PC, War Thunder, and WoT Blitz
   all require a context switch between replays — the game flushes state,
   deallocates assets, reinitializes the parser, and reloads the map.

4. **The lifecycle monitor already supports sequential replays.** The
   `BlitzReplayLogMonitor` watches `DAVAProject/blitz-logs_*.txt` for
   `START_REPLAY_LOCAL` / `STOP_REPLAY_LOCAL` markers. The offline gate opens
   and closes automatically based on these markers. No code changes needed.

5. **Version coupling is strict.** Replays can only be played on the exact game
   version they were recorded on. v11.18 replays won't work on v11.19 game.
   The codebase's decoder only supports v11.18.

### What We Don't Know (Needs Testing)

1. **Single-instance behavior.** Does running `wotblitz.exe "replay.wotbreplay"`
   while the game is already running spawn a second process, or forward to the
   existing one? TEST NEEDED.

2. **Where does the game store "uploaded" replays?** The Uploaded tab in the
   in-game browser reads from somewhere — likely a subdirectory of
   `DAVAProject/replays/` or an internal cache. FINDING THIS would enable
   programmatic replay delivery without process creation.

3. **Does the game watch `DAVAProject/replays/` for new files?** If yes, dropping
   a replay there while the game is running might trigger auto-detection.

## Recommended Path Forward

### Phase 1: Test (30 minutes of live experimentation)
1. Test single-instance: run `wotblitz.exe "replay.wotbreplay"` while game is at main menu
2. Test drop-directory: copy replay to `DAVAProject/replays/` while game is running
3. Find "Uploaded" storage location: open a replay via file association, then search for new/modified files

### Phase 2: Fix the Managed Pipeline (code work)
1. Fix `child_exe_mismatch` (byte-count issue in `QueryFullProcessImageNameW`)
2. Fix decoder to support v11.19.0.10 game version
3. Get the managed launch pipeline working end-to-end

### Phase 3: Optimize (based on Phase 1 results)
- If single-instance works: implement Approach A (re-invoke)
- If Uploaded location found: implement drop-to-Uploaded approach
- If neither works: implement Approach E (hybrid fast restart — keep game at main menu, quickly restart for each replay)

## Key Files in Codebase

| File | Purpose |
|------|---------|
| `GameSessionCoordinator.cs` | Offline gate, managed launch orchestration |
| `SuspendedGameProcessLaunch.cs` | `CreateProcessW(CREATE_SUSPENDED)` + identity verification |
| `ManagedReplayArtifactStager.cs` | Stages replay artifacts for managed launch |
| `BlitzReplayLogMonitor.cs` | FileSystemWatcher for native log lifecycle markers |
| `BlitzReplayLifecycleParser.cs` | Parses `START_REPLAY_LOCAL` etc. from log lines |
| `GameProcessLauncher.cs` | Simple `Process.Start()` for game (no replay) |
| `GameApiEndpoints.cs` | `POST /api/v1/game/start` and `/launch` endpoints |
