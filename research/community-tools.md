# Community Tools & Resources

## Replay Parsers

### wotbreplay-parser (Rust, by eigenein)
- **GitHub:** `github.com/eigenein/wotbreplay-parser`
- **Language:** Rust (crates.io package available)
- **Features:** Extracts metadata, team rosters, account IDs, battle results
- **Relevance:** Reference implementation for replay format. Can be used to
  validate our C# decoder against a known working parser.
- **Data model (2026-08-03 swarm, from README):** `battle_results.timestamp_secs`,
  `players[14]` with `account_id`, `info.nickname`, `info.team()` (TeamNumber
  One/Two), `info.platoon_id`.

### wotbreplay-inspector (Rust, by eigenein)
- **GitHub:** `github.com/eigenein/wotbreplay-inspector`
- **Language:** Rust
- **Features:** Companion project for inspecting WoT Blitz replay mechanics
  programmatically.
- **Relevance:** Second independent reference for the replay data model.

### evido/wotreplay-parser (C++, WoT PC)
- **GitHub:** `github.com/evido/wotreplay-parser`
- **Language:** C++ (CMake, LibPNG, Boost, LibXML2)
- **Features:** Decodes WoT **PC** replays into JSON packet lists; renders
  minimap PNGs (green allies / red enemies / blue recorder), animated GIF
  position tracks over time, and position/class heatmaps.
- **Relevance:** Demonstrates exactly the position-track rendering our overlay
  does; closest visual reference for minimap plots.

### rajesh-rahul/wot-replay-tools (Rust/WASM)
- **GitHub:** `github.com/rajesh-rahul/wot-replay-tools`
- **Language:** Rust & WebAssembly, runs client-side in the browser
- **Status:** WIP
- **Features:** HTML5 Canvas `.wotreplay` visualizer with tank position boxes.

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
- **Status (2026-08-03):** closed-source proprietary; provides Tankopedia/data
  feeds that third-party tools consume. No public parser.

### DAVA Engine Open-Source Roots (2026-08-03)
- DAVA Engine originated at **DAVA Consulting**, later Wargaming-maintained;
  early versions BSD-3-Clause open source
- `github.com/smile4u/dava.engine` — DAVA engine mirror
- `github.com/rifsxd/dava.engine.framework` — DAVA framework mirror
- `github.com/vorlie/DAVA-Resource-Studio` — DAVA resource tooling (Data dirs, DVPL wrapping)
- `github.com/DavaFramework` — **404 as of 2026-07-31** (dead)
- **Relevance:** shipped Blitz engine is a heavily customized fork; open-source
  DAVA gives naming/architecture conventions (Core, AppContext, screens, scene
  graph, components), not the exact binary layout.

### wg-toolkit-rs (lead, partially explored)
- Rust toolkit for Wargaming network protocols surfaced mid-swarm
- **Status:** not fully read yet — potential protocol/replay reference

### PDB lead (static analysis accelerant)
- Community (sa413x, May 2025): beta build with `.pdb` once available in the
  game's Discord; later reported removed
- If recoverable, the OD-STATIC track gets class names/offsets directly

### XMPP/TLS/Frida traffic capture (2026-08-03)
- Chat is XMPP over TLS (`jabber:client`, `wargaming.net/xmpp` extensions)
- Frida `ws2_32.dll` `send`/`recv` hook captures all traffic (full harness in
  UnknownCheats thread #702797); decrypted chat is searchable in memory
- **Relevance:** 401-refresh / login-flow work touches the same auth surface

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
- **Rotation adjacent to position** (thread #711725): community reads yaw from a
  float physically next to the XYZ block (`Vehicle → +0x68/0x6C/0x70`, yaw
  candidate +0x74) — re-test the quarantined `playerYaw` with this.
- **Steam vs Lesta** (thread #618977): only difference is process name and module
  name; offsets shared across `wotblitz.exe` and `tanksblitz.exe`.
- **Static pointer scans break between map loads** (thread #606655): DAVA reaches
  entities via register-indexed addressing; use the `AppContextImpl → ScreensFlow
  → AvatarContextBattle` chain instead.

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
- `github.com/eigenein/wotbreplay-inspector` — Rust replay inspector (active)
- `github.com/evido/wotreplay-parser` — C++ WoT PC replay renderer (minimap/GIF/heatmap)
- `github.com/Jylpah/blitz-replays` — Python replay tools (**successor** to blitz-tools)
- `github.com/smile4u/dava.engine` — DAVA engine mirror
- `github.com/rifsxd/dava.engine.framework` — DAVA framework mirror
- `github.com/vorlie/DAVA-Resource-Studio` — DAVA resource tooling
- `steamdb.info/app/444200/` — SteamDB for WoT Blitz
- `unknowncheats.me` — Reverse engineering forum (DAVA Engine threads)
- `replays.wotinspector.com` — WOTInspector replay viewer/uploader
- Wargaming support: replay watch controls + sharing docs (`na.wargaming.net/support/.../wotb/article/16386/`)
- `pcgamingwiki.com/wiki/World_of_Tanks_Blitz` — game data locations reference
