# Community Tools & Resources

## Replay Parsers

### wotbreplay-parser (Rust, by eigenein)
- **GitHub:** `github.com/eigenein/wotbreplay-parser`
- **Language:** Rust (crates.io package available)
- **Features:** Extracts metadata, team rosters, account IDs, battle results
- **Relevance:** Reference implementation for replay format. Can be used to
  validate our C# decoder against a known working parser.

### blitz-tools (Python, by Jylpah) — UNMAINTAINED
- **GitHub:** `github.com/Jylpah/blitz-tools`
- **Language:** Python
- **Status:** README says **"Unmaintained — Please use blitz-replays instead."**
- **Features (legacy):** Bulk upload/analyze replays, integrates with Tankopedia data,
  computes statistical summaries and win-rate histograms

### blitz-replays (Python, by Jylpah) — SUCCESSOR
- **GitHub:** `github.com/Jylpah/blitz-replays`
- **Language:** Python 3.11+, CLI (Typer/Click-style), install via `pip install git+...`
- **Status:** Active; uploads to `replays.wotinspector.com`, rich `analyze` reports
- **Note:** Client-side replay *parsing* is on the roadmap ("parse: Parsing replays
  client-side. This is a bigger task") — the tool currently relies on WOTInspector's
  JSON for analysis. Relevant as a potential future reference for our decoder.

### WOTInspector Replays (Web)
- Web-based replay viewer
- Inspects shots, trajectories, damage distribution
- **Relevance:** Demonstrates replay data richness. Community values this data.

## Reverse Engineering Resources

### UnknownCheats — DAVA Engine Analysis
- **URL:** `unknowncheats.me/forum/other-mmorpg-and-strategy/689828-world-tanks-blitz-dava-game-engine-structure-hints.html`
- **Content:** Detailed DAVA Engine memory structure analysis:
  - `TankVisual` component hierarchy
  - `GameScene` structure
  - `VehicleGameLogic` with health, team, destruction state
  - Static pointer offsets for entity lists
- **Relevance:** Direct memory structure reference for our `MemoryScanEngine`.
- **2026-07 verification:** Entity-list chain (`Base + 0x03E91978`) still current in
  community releases for recent 11.x builds; see `memory-offsets-unknowncheats.md`.

### Community Tool Stack (verified 2026-07)
| Tool | Use | Notes |
|------|-----|-------|
| Cheat Engine | Live memory inspection, pointer scans | Approved in this repo |
| Frida | Dynamic instrumentation; hooks `ws2_32.dll` to parse encrypted game traffic | Thread #702797 |
| pymem (Python) | Lightweight external memory automation | Popular for simple patches |
| Ghidra / IDA Pro | Static analysis of `wotblitz.exe`, VMT reconstruction | Ghidra scripts in `tools/ghidra-scripts/` |
| ReClass.NET | Runtime class layout reconstruction | Free |
| x64dbg | Dynamic debugging | Free |

### Notable Community Techniques
- **Tundra/foliage removal done externally** by tweaking D3D render parameters/memory
  flags (avoids DLL injection / integrity checks) — validates the external-tools-only
  approach this project follows.
- **Reload timers** extracted from `VehicleDescr` structs by recent ESP releases —
  demonstrates the descriptor tree is well-mapped by the community.

## Steam Platform Resources

### SteamDB — App 444200
- **URL:** `steamdb.info/app/444200/`
- **Content:** Version history, depot list, branch configurations
- **Relevance:** Track game version changes, find old manifests for decoder support.

### Proton/Steam Deck Compatibility
- ProtonDB reports for WoT Blitz
- Wine/Proton launch options
- Replay file locations inside Proton prefixes
- **Relevance:** Future cross-platform support.

## Tools Mentioned in Research

| Tool | Purpose | Availability |
|------|---------|--------------|
| Cheat Engine 7.7 | Memory scanning (approved) | Free |
| IDA Pro | Static binary analysis | Commercial |
| Ghidra | Static binary analysis (scripts in `tools/ghidra-scripts/`) | Free |
| ReClass.NET | Runtime class layout reconstruction | Free |
| x64dbg | Dynamic debugging | Free |

## Key URLs to Bookmark

- `github.com/DavaFramework` — DAVA Engine source (**404 as of 2026-07-31**) — dead
- `github.com/eigenein/wotbreplay-parser` — Rust replay parser (active)
- `github.com/Jylpah/blitz-replays` — Python replay tools (**successor** to blitz-tools)
- `steamdb.info/app/444200/` — SteamDB for WoT Blitz
- `unknowncheats.me` — Reverse engineering forum (DAVA Engine threads)
- `replays.wotinspector.com` — WOTInspector replay viewer/uploader
- Wargaming support: replay watch controls + sharing docs (`na.wargaming.net/support/.../wotb/article/16386/`)
- `pcgamingwiki.com/wiki/World_of_Tanks_Blitz` — game data locations reference
