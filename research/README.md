# WoT Blitz Replay Research Index

## Documents (9 files)

| File | Topic |
|------|-------|
| [complete-reference.md](complete-reference.md) | **Start here** — all findings consolidated: paths, markers, endpoints, versions, blockers, decision matrix |
| [findings-summary.md](findings-summary.md) | Executive summary with recommended path forward |
| [replay-loading-mechanisms.md](replay-loading-mechanisms.md) | How WoT Blitz loads replays — command line args, file association, DAVA engine, in-game browser |
| [uploaded-replays.md](uploaded-replays.md) | **NEW** — The Uploaded tab mechanism: how external replays get into the in-game browser |
| [ipc-mechanisms.md](ipc-mechanisms.md) | 6 IPC approaches for live replay switching rated by feasibility |
| [lifecycle-monitor.md](lifecycle-monitor.md) | Native log lifecycle monitor — markers, FileSystemWatcher, offline gate |
| [dava-engine.md](dava-engine.md) | DAVA Engine analysis — architecture, scene management, open source effort |
| [memory-analysis.md](memory-analysis.md) | Memory scanning techniques — string search, pointer chains, Ghidra, ReClass |
| [community-tools.md](community-tools.md) | Community resources — parsers, reverse engineering, SteamDB |
| [approaches.md](approaches.md) | 6 approaches (A-F) with implementation details and testing protocols |

## Quick Facts

### Version Gap (CRITICAL)
- **Game installed:** v11.19.0.10
- **Decoder supports:** v11.18.0.7
- **Fix needed:** Add v11.19 decoder support or find v11.18 game installation

### Managed Launch Pipeline Status
- Steps 1-3 (prepare, lease, stage) ✅ Working
- Step 4 (suspended process) ⚠️ P/Invoke fixed, path comparison fix coded but not tested
- Steps 5-7 (correlation, resume, handoff) Not reached yet
- See `child_exe_mismatch` fix in `SuspendedGameProcessLaunch.cs`

### Live Replay Switching
- **Uploaded tab:** Replays opened via file association appear in `Profile → Replays → Uploaded`
- **No hot-swapping:** Game flushes state between replays (like WoT PC, War Thunder)
- **Best approach:** Test file association re-invoke + hybrid fast restart
- **Long-term:** Memory manipulation for true live switching

### Replay File Locations
- **Recent/Favorites:** `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\`
- **Uploaded:** Read from wherever the file was opened (not copied)
- **Native logs:** `%LOCALAPPDATA%\wotblitz\DAVAProject\blitz-logs_*.txt`

### Lifecycle Markers
- `START_REPLAY_LOCAL` → Offline replay started
- `STOP_REPLAY_LOCAL` → Offline replay stopped
