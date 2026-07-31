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
   The decoder accepts both 11.18 and 11.19; installed-game validation remains local opt-in.

6. **STRATEGIC RISK — Reforged UE5 migration.** Wargaming is moving WoT Blitz from
   DAVA to Unreal Engine 5 (announced 2026-06-17, postponed indefinitely). The live
   client is still DAVA, but the replay format, memory offsets, log markers, and
   `DAVAProject` paths may all change when Reforged ships. Keep the Reforged / UE5
   risk note separate from the committed runtime documentation. Prioritize pipeline
   work that survives the migration (process launch, file association, log watching)
   over DAVA-internal research.

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

The managed launch and decoder work is implemented; the remaining uncertainty is
installed-game behavior and dynamic offset evidence. Keep these as local opt-in,
offline-only experiments and do not treat research notes as runtime contracts.

### Phase 1: Test (local opt-in, offline replay only)
1. Test single-instance behavior with a known replay while the game is at its menu.
2. Test whether the replay directory is observed while the game is running; do not
   overwrite user files or modify the installation.
3. Find "Uploaded" storage location by recording only redacted metadata and paths,
   never copying private replay bytes into the repository.

### Phase 2: Validate the Managed Pipeline (local opt-in)
1. Exercise the managed launch path against an installed game and approved replay.
2. Confirm the lifecycle gate reaches `OfflineReplayVerified` and closes on replay stop.
3. Keep candidate offset evidence separate from runtime promotion; the current
   11.19.0.10 table has one hash-bound `playerYaw` candidate and seven unknown fields.

### Phase 3: Optimize (only after evidence)
- If single-instance works: evaluate Approach A (re-invoke) behind the existing gate.
- If Uploaded location is confirmed: evaluate delivery without modifying the install.
- If neither works: evaluate Approach E (hybrid fast restart) as the bounded fallback.
- Keep memory manipulation out of scope unless a new safety review explicitly accepts it.

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
