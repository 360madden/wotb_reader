# Proposed Approaches for Live Replay Switching

## Context

The managed launch pipeline (`GameSessionCoordinator.LaunchCoreAsync`) creates a
**new suspended process** for each replay. This is architecturally sound for
security (fresh process, verified identity, clean correlation) but operationally
inefficient — the game restarts for each replay.

**User's goal:** Keep `wotblitz.exe` running and switch replays without restarting.

## Approach A: Command-Line Re-invoke (Test First)

**Hypothesis:** WoT Blitz implements single-instance detection. Re-invoking with
a new replay path tells the existing process to load it.

**Implementation:**
```
POST /api/v1/game/switch-replay { sourceArtifactId: "..." }
```
1. Stage the replay artifact (reuse `ManagedReplayArtifactStager`)
2. Launch `wotblitz.exe "C:\staging\replay.wotbreplay"` (NOT suspended)
3. If single-instance: existing game loads it, second process exits
4. If NOT single-instance: second process spawns (works but wasteful)
5. Lifecycle monitor detects `START_REPLAY_LOCAL` → gate opens

**Pros:** Minimal code change, reuses existing staging infrastructure
**Cons:** Unknown if single-instance, may spawn extra process

**To test:** Run while game is at main menu:
```cmd
wotblitz.exe "<REPLAY_PATH>"
```

## Approach B: Drop-Directory + File Watcher

**Hypothesis:** Copying a replay into `DAVAProject/replays/` while the game
is running triggers auto-load.

**Implementation:**
1. Stage replay to `DAVAProject/replays/` (not the private staging dir)
2. Game's internal file watcher detects the new file
3. Game auto-loads the replay
4. Lifecycle monitor detects markers

**Pros:** Non-invasive, no process creation
**Cons:** Unknown if game watches that directory, pollutes user's replay folder

## Approach C: Extend the Managed Pipeline

**Current flow:** Create suspended process → stage artifact → resume
**Modified flow:**
1. Check if game is already running (via `GameSessionCoordinator` state)
2. If running: terminate current child, re-invoke with new replay
3. If not running: use existing suspended process pipeline
4. Re-correlate the lifecycle feed to the new launch

**Pros:** Maintains security guarantees of managed pipeline
**Cons:** Still restarts the game (just more elegantly)

## Approach D: Process Memory Manipulation

**Hypothesis:** The game stores the "current replay path" in memory. We could
write a new path to that memory location and trigger a reload.

**Implementation:**
1. Use `MemoryScanEngine` to find the replay path string in process memory
2. Write a new path using `WriteProcessMemory`
3. Call an internal function (via hook or code cave) to reload

**Pros:** True live switching without process restart
**Cons:** Extremely fragile, anti-cheat risk, version-dependent offsets

## Approach E: Hybrid — Fast Restart

**Accept that restart is needed but make it fast:**

1. Keep `wotblitz.exe` running at main menu after replay ends
2. Managed launch creates NEW suspended process (current behavior)
3. Terminate old process immediately after new one is verified
4. User sees: replay ends → brief flash → new replay starts

**Pros:** Maintains all security guarantees, minimal code change
**Cons:** ~2-3 second transition (process creation + replay load)

## Approach F: Uploaded Replay Delivery (NEW - Based on Research)

**Hypothesis:** WoT Blitz has an in-game replay browser with an "Uploaded" tab.
If we can find where the game stores uploaded replays, we can copy replays there
and the user can select them from the in-game UI without restarting.

**Implementation:**
1. Find the "uploaded" replay storage location (research task)
2. Stage replay artifact to that location
3. User navigates to `Profile → Replays → Uploaded` and selects the replay
4. Lifecycle monitor detects playback markers
5. After replay ends, game returns to profile (not main menu)

**Pros:** No process creation, uses official game UI, fast context switch
**Cons:** Requires user interaction (selecting replay from UI),
       need to find Uploaded storage location

**Discovery task:**
```cmd
# Open a replay via file association
wotblitz.exe "<REPLAY_PATH>"
# Then find newly created/modified files:
dir /s /od "%LOCALAPPDATA%\wotblitz\DAVAProject" | findstr wotbreplay
```

## Recommended Order of Investigation

1. **Test Approach F** — find Uploaded storage location (10-minute experiment)
2. **Test Approach A** — check single-instance (5-minute experiment)
3. **Test Approach B** — check file watcher (5-minute experiment)
4. If none work: **Approach E** (hybrid fast restart) is the pragmatic fallback
5. Approach D only if long-term investment in live switching is justified

## Immediate Action Items

1. Find Uploaded replay storage:
   ```cmd
   # Open a replay, then check for new files
   wotblitz.exe "<REPLAY_PATH>"
   ```

2. Check if `wotblitz.exe` is single-instance:
   ```cmd
   start wotblitz.exe "replay1.wotbreplay"
   # Wait for main menu after replay
   start wotblitz.exe "replay2.wotbreplay"
   # Does a new process appear? Or does existing window load replay2?
   ```

3. Check if game watches replays directory:
   ```cmd
   copy replay.wotbreplay %LOCALAPPDATA%\wotblitz\DAVAProject\replays\
   # Does game react?
   ```

4. Validate the managed launch identity path against an installed game as a local
   opt-in test; the prior `child_exe_mismatch` handling is implemented and unit-tested
