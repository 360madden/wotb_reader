# Uploaded Replays — Complete Mechanism

## Discovery

The "Uploaded" tab in WoT Blitz's replay browser (`Profile → Replays → Uploaded`)
shows replays that were **opened via the operating system's file handler**. This is
the key mechanism for delivering replays to a running game.

## How It Works

1. User double-clicks a `.wotbreplay` file in File Explorer (or it's opened programmatically)
2. Windows invokes the file association: `wotblitz.exe "C:\path\to\replay.wotbreplay"`
3. The game detects the replay path in `argv[1]`
4. The replay appears in the "Uploaded" tab
5. User can select and play it from the in-game UI

## Critical Insight

The game does NOT copy the replay to a special "uploaded" folder. It reads the
replay from **wherever the file was opened from**. The Uploaded tab is essentially
a list of recently-opened external replay paths.

This means: to programmatically add a replay to the Uploaded tab, we need to
**invoke the file association** for that replay file.

## Replay Storage by Tab

| Tab | Location | Notes |
|-----|----------|-------|
| **Recent** | `DAVAProject/replays/` | Auto-saved, capped at 100, auto-deleted on version update |
| **Favorites** | `DAVAProject/replays/` | Same folder, flagged in internal DB, exempt from auto-deletion |
| **Uploaded** | Original open location | Not copied — game remembers the path |

## Platform-Specific Paths

### Windows Steam / WGC
```
%LOCALAPPDATA%\wotblitz\DAVAProject\replays\
```

### Windows Microsoft Store
```
%LOCALAPPDATA%\Packages\7458BE2C.WorldofTanksBlitz_x4tje2y229k00\LocalState\DAVAProject\replays\
```

### macOS Steam
```
~/Library/Application Support/net.wargaming.wotblitz.macos/DAVAProject/replays/
```

### macOS App Store
```
~/Library/Containers/net.wargaming.wotblitz.macos/Data/Documents/DAVAProject/replays/
```

## Implications for Live Replay Switching

### What Works:
1. **Stage replay file** to a known location (e.g., the managed staging directory)
2. **Programmatically invoke file association:**
   ```csharp
   Process.Start(new ProcessStartInfo
   {
       FileName = "C:\\staging\\replay.wotbreplay",
       UseShellExecute = true  // Uses Windows file association
   });
   ```
3. The game detects the new replay in `argv[1]`
4. If game is already running: may forward to existing instance (single-instance
   behavior unknown) or spawn second process
5. Replay appears in Uploaded tab

### What's Unknown:
- **Single-instance behavior:** Does the second invocation spawn a new process
  or forward to the existing one?
- **Args forwarding:** If single-instance, does the game forward argv[1] to the
  existing instance? In-game replay browser or just new process?

### Test Protocol:
1. Start game via `POST /api/v1/game/start` (simple launch, main menu)
2. Verify game is at main menu
3. Run: `Process.Start("C:\\staging\\replay.wotbreplay")` with `UseShellExecute=true`
4. Observe: new PID? Or existing window loads replay?
5. Check Uploaded tab: does the replay appear?
6. Check native log: `START_REPLAY_LOCAL` marker?
