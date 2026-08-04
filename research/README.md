# WoT Blitz Replay Research Index

## Documents (12 committed files)

| File | Topic |
|------|-------|
| [complete-reference.md](complete-reference.md) | **Start here** — all findings consolidated: paths, markers, endpoints, versions, blockers, decision matrix |
| Reforged / UE5 migration note | **STRATEGIC RISK** — DAVA → Unreal Engine 5 migration assumptions are tracked separately and may invalidate DAVA-era format/offset/log research when it ships |
| [findings-summary.md](findings-summary.md) | Executive summary with recommended path forward |
| [replay-loading-mechanisms.md](replay-loading-mechanisms.md) | How WoT Blitz loads replays — command line args, file association, DAVA engine, in-game browser |
| [uploaded-replays.md](uploaded-replays.md) | **NEW** — The Uploaded tab mechanism: how external replays get into the in-game browser |
| [ipc-mechanisms.md](ipc-mechanisms.md) | 6 IPC approaches for live replay switching rated by feasibility |
| [lifecycle-monitor.md](lifecycle-monitor.md) | Native log lifecycle monitor — markers, FileSystemWatcher, offline gate |
| [dava-engine.md](dava-engine.md) | DAVA Engine analysis — architecture, scene management, open source effort |
| [memory-analysis.md](memory-analysis.md) | Memory scanning techniques — string search, pointer chains, Ghidra, ReClass |
| [memory-offsets-unknowncheats.md](memory-offsets-unknowncheats.md) | Community-compiled memory offsets from UnknownCheats |
| [community-tools.md](community-tools.md) | Community resources — parsers, reverse engineering, SteamDB |
| [online-research-swarm-2026-08-03.md](online-research-swarm-2026-08-03.md) | **NEW** — 2026-08-03 online research swarm: current entity chain, rotation-near-position, struct dump, PDB lead, XMPP/TLS, replay parsers |
| [approaches.md](approaches.md) | 6 approaches (A-F) with implementation details and testing protocols |

## Quick Facts

### Version and offset status — current snapshot (2026-07-31)
- **Game installed:** v11.19.0.10
- **Decoder:** `wotb-11.x-strict` supports normalized 11.18 and 11.19 versions; `EventStreamReader` and `IsSupportedVersion` accept both
- **Offsets:** executable SHA-256 is recorded; `playerYaw` has static-analysis provenance but is quarantined/Stale (conflicting representations), and seven fields remain unknown
- **11.19 released July 2026** (per community release trackers: Reddit
  r/WorldOfTanksBlitz, Uptodown changelog) — minor rebalances only; replay
  format/log markers unchanged

### Reforged / UE5 Migration (STRATEGIC RISK)
- Wargaming is migrating WoT Blitz from DAVA to **Unreal Engine 5** (Reforged)
- Announced 2026-06-17, **postponed indefinitely**; live client still DAVA
- A separate Reforged / UE5 risk note may be kept locally; all DAVA-era research has finite shelf life

### Managed Launch Pipeline Status
- Preparation, trusted executable lease, replay staging, suspended launch, identity
  verification, correlation, resume, and handoff are implemented and covered by
  synthetic/unit tests
- Installed-game launch and replay-gate validation remain explicit local opt-in work
- The former `child_exe_mismatch` issue was addressed in the P/Invoke/path handling

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
