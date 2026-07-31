# WoT Blitz Replay Loading Mechanisms

## Current Managed Launch Pipeline (from codebase)

### Suspended Process Creation

The current approach in `WindowsSuspendedProcessPlatform.CreateAsync` creates a NEW
game process each time:

```csharp
string commandLine = $"\"{executablePath}\" \"{replayPath}\"";
bool created = NativeMethods.CreateProcessW(
    executablePath, commandLine,
    IntPtr.Zero, IntPtr.Zero, false,
    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
    IntPtr.Zero, workingDirectory,
    ref startupInfo, out processInfo);
```

**Key observation:** The replay path is passed as the **second command-line argument**
to `wotblitz.exe`. This is the standard Windows file-association pattern:
`wotblitz.exe "C:\path\to\replay.wotbreplay"`.

### How the Existing Game Process Handles Arguments

When `wotblitz.exe` starts, it:
1. Parses `argv[1]` during initialization
2. If a `.wotbreplay` path is found, it enters **replay playback mode** instead of
   logging into the game server
3. The game writes lifecycle markers to `blitz-logs_*.txt` in `DAVAProject/`

### Lifecycle Markers

The game writes these markers to its native log:

| Marker | Meaning |
|--------|---------|
| `START_REPLAY_LOCAL` | Offline replay playback started |
| `STOP_REPLAY_LOCAL` | Offline replay playback stopped |
| `ReplayRecorder::StartRecording` | Replay recording started |
| `ReplayRecorder::StopRecording` | Replay recording stopped |

These are monitored by `BlitzReplayLogMonitor` which watches `DAVAProject/` for
`blitz-logs_*.txt` files using `FileSystemWatcher` + periodic reconciliation.

## Replay File Association

### Windows Registry

`.wotbreplay` files are associated with `wotblitz.exe` in the Windows Registry.
Double-clicking a replay in File Explorer launches:
```
wotblitz.exe "C:\...\replay.wotbreplay"
```

This spawns a **new process** — it does NOT send the replay to an already-running
game instance.

### DAVA Engine Behavior

WoT Blitz uses Wargaming's proprietary DAVA Engine. The engine reads command-line
arguments during startup. There is no known runtime API for hot-reloading replays.

## Steam Integration

On Steam, WoT Blitz:
- Is launched via `steam://rungameid/<appid>`
- Uses `ISteamApps::GetLaunchCommandLine()` to receive arguments
- Steam does NOT provide replay-specific IPC — it's just a process launcher
- No known Steam protocol for passing files to running instances

## Replay Storage Location

```
%LOCALAPPDATA%/wotblitz/DAVAProject/replays/
```

Example: `C:\Users\mrkoo\AppData\Local\wotblitz\DAVAProject\replays\`

The game's native log files are in the parent directory:
```
%LOCALAPPDATA%/wotblitz/DAVAProject/blitz-logs_*.txt
```

## In-Game Replay Browser (CRITICAL FINDING)

WoT Blitz has a built-in replay browser accessible from:
```
Profile (upper left) → Replays
```

Three tabs:
| Tab | Capacity | Description |
|-----|----------|-------------|
| **Recent** | Up to 100 battles | Chronologically ordered, auto-deleted on version update |
| **Favorites** | Unlimited (per version) | Starred replays, exempt from auto-deletion |
| **Uploaded** | External files | Replays opened from outside the game (file association) |

### The "Uploaded" Tab

This is the key insight: when a replay is opened via file association
(`wotblitz.exe "replay.wotbreplay"` or double-click in Explorer), it appears
in the **Uploaded** tab. The game copies the replay to an internal location
and makes it browsable from within the running game.

**Unknown but critical:** Where does the game store "uploaded" replays?
Candidates:
- `%LOCALAPPDATA%/wotblitz/DAVAProject/replays/` (with a special flag?)
- A separate "uploaded" subdirectory
- An internal database/cache

### Hot-Swapping Limitation

Even with the in-game browser, WoT Blitz **does not support hot-swapping**
replays. Selecting a replay from the browser:
1. Flushes the current battle/hangar state
2. Deallocates assets
3. Reinitializes the scenario parser
4. Rebuilds the map and tank models

This is effectively a "soft restart" — faster than a full process restart
but still a context switch.

## Single-Instance Behavior

**Unknown.** WoT Blitz may or may not implement single-instance detection. If it does:

- A second `wotblitz.exe "replay.wotbreplay"` invocation would detect the existing
  instance (via named mutex), forward the argument via IPC, and exit
- If it does NOT, the second invocation spawns a second game process

Testing needed: launch `wotblitz.exe "replay.wotbreplay"` while a game instance is
already running and observe whether a new process appears.

## Comparison: WoT Blitz vs WoT PC vs War Thunder

| Feature | WoT PC (BigWorld) | War Thunder (Dagor) | WoT Blitz (DAVA) |
|---------|-------------------|---------------------|------------------|
| **In-game replay browser** | No | Yes (`Community → Replays`) | Yes (`Profile → Replays`) |
| **Launch via file association** | Yes | Yes | Yes |
| **Hot-swap between replays** | No (restart) | No (back to hangar) | No (back to profile) |
| **Version dependence** | Strict | Strict | Strict |

**Conclusion:** No tank game supports true hot-swapping. All require a context
switch between replays. The pragmatic approach for WoT Blitz is to accept the
restart cycle and optimize for speed.

## Playback Sequence (Observed)

1. Game process starts with replay path as argument
2. Game enters replay playback mode (skips login/server connection)
3. Game writes `START_REPLAY_LOCAL` to `blitz-logs_*.txt`
4. Replay plays back
5. Game writes `STOP_REPLAY_LOCAL` when playback ends
6. Game typically returns to main menu (or exits, depending on behavior)
