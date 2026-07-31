# IPC Mechanisms for Live Replay Switching

## Goal

Feed a new replay file to an **already-running** `wotblitz.exe` process without
restarting the game.

## Mechanism Matrix

| Mechanism | Feasibility | Complexity | Notes |
|-----------|-------------|------------|-------|
| **Command-line re-invoke** | Unknown | Low | Depends on single-instance detection in wotblitz.exe |
| **File watcher in DAVAProject/** | Unknown | Low | Drop replay into replays dir, game may detect it |
| **Named pipes** | Low | High | Requires game-side pipe server (not present) |
| **Memory-mapped files** | Low | High | Requires game-side reader (not present) |
| **Windows messages (WM_COPYDATA)** | Low | Medium | UIPI blocks cross-privilege messages |
| **TCP loopback** | Low | High | Requires game-side HTTP/socket server |
| **DLL injection + hooking** | Medium | Very High | Risky, anti-cheat concerns |
| **Steam protocol** | Low | Low | Steam only launches processes |

## Detailed Analysis

### 1. Command-Line Re-invoke (Most Promising)

**How it works:**
```cmd
wotblitz.exe "C:\path\to\new_replay.wotbreplay"
```

**If single-instance:**
- Second `wotblitz.exe` detects existing instance via named mutex
- Forwards the replay path argument to the first instance (via IPC of game's choice)
- Exits immediately
- First instance loads the new replay

**If NOT single-instance:**
- A second `wotblitz.exe` process spawns
- Two game processes run simultaneously — not ideal
- Second process plays the replay, first continues at main menu

**Testing protocol:**
1. Start the game via `POST /api/v1/game/start` (simple launch, no replay)
2. Wait for main menu
3. Run: `wotblitz.exe "C:\path\to\replay.wotbreplay"` from command line
4. Observe: new PID in Task Manager? Or does existing window load the replay?
5. Check native log for `START_REPLAY_LOCAL` marker

### 2. File Watcher / Drop Directory

**Theory:** The game might watch `%LOCALAPPDATA%/wotblitz/DAVAProject/replays/`
for new files. If a replay is dropped there while the game is running, it might
auto-load it.

**How to test:**
1. Start the game (main menu)
2. Copy a `.wotbreplay` file into `DAVAProject/replays/`
3. Observe if the game reacts (UI change, log entry, etc.)
4. Check `blitz-logs_*.txt` for any reaction markers

**Evidence for/against:**
- The codebase's `BlitzReplayLogMonitor` watches `DAVAProject/` for the GAME'S log
  output — not the replay directory specifically
- No evidence that the game watches the replays subdirectory

### 3. Named Pipes (Would Require Game Modding)

Named pipes are the gold standard for single-instance IPC:
```
\\\\.\\pipe\\WoTBlitzReplayPipe
```

But this requires the game to have a pipe server built in. WoT Blitz does not
expose any known named pipe interface. This would require DLL injection.

### 4. Windows Messages (WM_COPYDATA)

```csharp
SendMessage(hWnd, WM_COPYDATA, IntPtr.Zero, ref copyDataStruct);
```

**Blockers:**
- UIPI (User Interface Privilege Isolation) blocks messages between processes
  at different integrity levels
- The game must have a window procedure that handles custom messages
- Window handle discovery is fragile (titles change, borderless windows)

### 5. DLL Injection + Hooking

The most powerful approach but highest risk:
- Inject a DLL into the game process
- Hook the game's replay-loading function
- Call it with a new replay path

**Risks:**
- Anti-cheat detection (WoT Blitz uses client-side anti-cheat)
- Process instability
- Maintenance burden (game updates break hooks)

### 6. Steam Protocol

```
steam://rungameid/444200//"C:\path\to\replay.wotbreplay"
```

Steam's protocol handler only launches processes. It does not provide IPC for
passing data to running instances.

## Recommended Investigation Order

1. **Command-line re-invoke** — simplest, test immediately
2. **File watcher / drop directory** — easy to test, low risk
3. **Process memory observation** — if game has a "current replay path" in memory,
   we could write a new path there
4. **DLL injection** — last resort, high complexity
