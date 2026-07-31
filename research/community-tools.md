# Community Tools & Resources

## Replay Parsers

### wotbreplay-parser (Rust, by eigenein)
- **GitHub:** `github.com/eigenein/wotbreplay-parser`
- **Language:** Rust (crates.io package available)
- **Features:** Extracts metadata, team rosters, account IDs, battle results
- **Relevance:** Reference implementation for replay format. Can be used to
  validate our C# decoder against a known working parser.

### blitz-tools (Python, by Jylpah)
- **GitHub:** `github.com/Jylpah/blitz-tools`
- **Language:** Python
- **Features:** Bulk upload/analyze replays, integrates with Tankopedia data,
  computes statistical summaries and win-rate histograms
- **Relevance:** Shows what data is extractable from replays. Database schema
  could inform our storage design.

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

### Cheat Engine Community
- Pointer scanning tutorials
- Pattern scanning vs pointer scanning discussion
- `ReadProcessMemory` best practices
- **Relevance:** Techniques we can apply directly with our existing tools.

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

- `github.com/DavaFramework` — DAVA Engine source
- `github.com/eigenein/wotbreplay-parser` — Rust replay parser
- `github.com/Jylpah/blitz-tools` — Python replay tools
- `steamdb.info/app/444200/` — SteamDB for WoT Blitz
- `unknowncheats.me` — Reverse engineering forum (DAVA Engine threads)
