# Complete Replay Research Reference

## File Paths

### Game Executable
| Platform | Path |
|----------|------|
| Steam | `C:\Program Files (x86)\Steam\steamapps\common\World of Tanks Blitz\wotblitz.exe` |
| WGC | `C:\Games\World_of_Tanks_Blitz\wotblitz.exe` |
| Default discovery | `C:\Games\World_of_Tanks_Blitz\wotblitz.exe` |

### Game User Data
| Component | Path (Windows Steam/WGC) |
|-----------|--------------------------|
| Base | `%LOCALAPPDATA%\wotblitz\` |
| Replays | `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\` |
| Native logs | `%LOCALAPPDATA%\wotblitz\DAVAProject\blitz-logs_*.txt` |
| DLC packs | `%LOCALAPPDATA%\wotblitz\packs\` |

### Our Managed Storage
| Component | Path |
|-----------|------|
| Data root | `.data\` (configurable) |
| Database | `.data\treader.db` |
| Content store | `.data\content\` |
| Staging | `.data\staging\` |
| Diagnostics | `.data\diagnostics\` |
| Logs | `.data\logs\` |

## Lifecycle Markers

| Marker | Engine State | Our State |
|--------|-------------|-----------|
| `START_REPLAY_LOCAL` | Replay playback active | `OfflineReplayVerified` |
| `STOP_REPLAY_LOCAL` | Replay playback ended | `EvidenceStale` / `Denied` |
| `ReplayRecorder::StartRecording` | Recording started | (info only) |
| `ReplayRecorder::StopRecording` | Recording stopped | (info only) |

## API Endpoints

| Method | Path | Capability Required | Status |
|--------|------|---------------------|--------|
| GET | `/api/v1/game/state` | No | ✅ Working |
| GET | `/api/v1/game/memory` | No | ✅ (returns Unknown without gate) |
| POST | `/api/v1/game/start` | Yes | ✅ Working |
| POST | `/api/v1/game/launch` | Yes | ⚠️ child_exe_mismatch (fix coded) |
| POST | `/api/v1/game/discover` | Yes* | ⚠️ Requires offline gate |
| POST | `/api/v1/game/discover/snapshot` | Yes* | ⚠️ Requires offline gate |
| POST | `/api/v1/game/discover/compare/{id}` | Yes* | ⚠️ Requires offline gate |
| POST | `/api/v1/game/discover/neighborhood` | Yes* | ⚠️ Requires offline gate |

*Capability required for mutation; gate required for memory access

## Game Versions

| Version | Decoder Support | Offset Table | Installed |
|---------|----------------|--------------|-----------|
| 11.8.0.7 | Yes | Placeholder | No |
| 11.18.0.7 | Yes | Placeholder | No |
| 11.19.0.10 | **No** | Placeholder | **Yes** |

## Current Blockers

| Blocker | Status | Fix |
|---------|--------|-----|
| `child_exe_mismatch` | Fix coded, not tested | NormalizeExePath + byte-count detection |
| v11.19 decoder | Not started | Need to add decoder support |
| Single-instance behavior | Unknown | Need live test |
| Uploaded replays mechanism | Understood | Need live test |
| DAVA Engine source | Not accessible (404) | Use community reverse engineering |

## Community Resources

| Resource | URL |
|----------|-----|
| UnknownCheats DAVA thread | `unknowncheats.me/forum/other-mmorpg-and-strategy/689828` |
| wotbreplay-parser (Rust) | `github.com/eigenein/wotbreplay-parser` |
| blitz-tools (Python) | `github.com/Jylpah/blitz-tools` |
| SteamDB WoT Blitz | `steamdb.info/app/444200/` |
| WOTInspector Replays | Web-based replay viewer |

## Decision Matrix: Live Replay Switching Approaches

| Approach | Feasibility | Speed | Risk | Code Change |
|----------|-------------|-------|------|-------------|
| **A: Re-invoke file association** | Medium | Fast | Low | Small |
| **B: Drop to replays dir** | Low | Medium | Low | Small |
| **C: Extended managed pipeline** | High | Medium | Low | Medium |
| **D: Memory manipulation** | Low | Instant | High | Large |
| **E: Hybrid fast restart** | High | Medium | Low | Small |
| **F: Uploaded tab delivery** | Medium | Fast | Low | Small |

### Recommended Priority
1. **Fix pipeline** (C) → Get managed launch working first
2. **Test A + F** → Re-invoke + Uploaded tab delivery
3. **Implement E** → Hybrid fast restart as fallback
4. **Investigate D** → Memory manipulation for true live switching (long-term)
