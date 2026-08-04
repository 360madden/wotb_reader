# Online Research Swarm — 2026-08-03

Swarm session: 16-agent first wave + follow-up waves of `researcher-web` agents,
plus direct full-thread reads of five UnknownCheats threads. All findings below
are **community-reported** (mostly Lesta `tanksblitz.exe` and WG `wotblitz.exe`
11.x/26.x era) and are **evidence candidates only** — none are runtime-promoted.

## TL;DR — what's most valuable to this project

1. **Rotation is stored adjacent to position.** Thread #711725 explicitly states
   "Rotation is stored nearby" the position floats at
   `VehicleGameLogic → Vehicle → +0x68/0x6C/0x70`. This is the single most
   relevant community statement to our quarantined `playerYaw` hypothesis — the
   community reads yaw from a float physically next to the XYZ block, matching
   our "adjacent float" scanning approach. Worth re-testing around `+0x74` with
   the yaw convention question in mind.
2. **Turret/hull rotation separation is documented.** `TBVehicleFilter2`
   (at `Vehicle → +0x60`) → `TBVehicleFilterHelper` (+0x10) holds "position and
   rotation along Y and X relative to the hull rotation". This is the
   hull-yaw-vs-turret-yaw distinction our representation conflict hinges on.
3. **A public PDB may have existed.** sa413x (May 2025): *"You can find the beta
   build with the .pdb file in the game's Discord server."* Enesterio later said
   it's gone. If a beta `.pdb` is recoverable anywhere, static analysis (our
   OD-STATIC track) becomes dramatically easier.
4. **Steam vs Lesta builds share structures.** Thread #618977 moderator note:
   "The only difference between these two files is the process name and module
   name." Community ESP works on both `wotblitz.exe` and `tanksblitz.exe` with
   identical offsets (confirmed by #711725 author). Our 11.19.0.10 findings
   remain valid for both distributions.
5. **Server-side authority is the hard wall.** NoSpread is client-visual only
   (spread computed server-side); ballistics/damage/spotting are server
   authoritative. External memory reading of positions/rotation remains the
   viable surface.

## Entity List Pointer Chain (verified current, community)

Source: [#711725](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/711725-tanks-blitz-lesta-esp-players.html)
(post #7 by `24235234`, 2025-08), for Lesta 26.2.1.28. Same for Steam/WG.

```
adr base = "tanksblitz.exe" + 0x03E91978        (wotblitz.exe for WG/Steam)
base
└── 0x0 AppContextImpl
    └── 0x240 ScreensFlow
        └── 0x98 AvatarContextBattle (initialized only in battle)
            └── 0x60 (null name pointer)
                └── 0x10 EntityList
                    ├── 0x00 VehicleGameLogic  (local player)
                    ├── 0x08 VehicleGameLogic  (platoon, if any)
                    ├── 0x10 .. 0x68           (up to 14 tanks, 0x8 stride)
```

- `EntityList[i]` → `+0x8` → `Vehicle`
- **Position:** `VehicleGameLogic → 0x8 Vehicle → 0x68 / 0x6C / 0x70` (XYZ floats)
- **Rotation: stored nearby** (exact offset not stated; community ESP reads it
  adjacent to position — candidate `+0x74` for yaw)
- **Turret aim:** `VehicleGameLogic → 0x8 Vehicle → 0x60 TBVehicleFilter2 →
  0x10 TBVehicleFilterHelper` — "position and rotation along Y and X relative
  to the hull rotation"
- **Tank class names:** `VehicleGameLogic → 0xA8 VehicleDescr` (name somewhere
  in descriptor; see GetTankName helper below for an older path)
- **Fake NoSpread:** `VehicleDescr → 0xD0` float (default 0.4–3.0; setting 0.0
  only changes client display; server spread unaffected)
- **Reload timers:** community ESP releases read per-tank reload cooldowns from
  `VehicleDescr` sub-structs

## Struct Dump (Errorc0de, shared by snobbob0, May 2025)

Source: [#689828](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/689828-world-tanks-blitz-dava-game-engine-structure-hints.html)
(post #6). "This is for Internal, although should not matter."

```cpp
struct Component {
    char    _pad0[0xC];
    DWORD   comp_info;      // 0x8
};

struct Vehicle {
    char    _pad0[0xB0];
    UINT8   Team;           // 0xB0
    char    _pad1[0x6B];
    int     MaxHP;          // 0x11C
};

struct VehicleGameLogic {
    char    _pad0[0x4];
    Vehicle* vehicle;       // 0x4
    char    _pad1[0x1B0];   // 0x8 ~ 0x1B7
    int     HP;             // 0x1B8
};

struct GameCamera {
    char    _pad[0x140];
    float   ViewMatrix;
};

struct GameScene {
    char        _pad[0x128];
    GameCamera*  gamecamera;   // 0x128
};

struct SilhouetteComponent {
    char    _pad0[0x11];
    bool    active;         // 0x11
    char    _pad1[0x02];
    Vector3 RGB;            // 0x14
};

struct TankVisual {
    char        _pad0[0x4];
    int         player_mask;          // 0x04
    char        _pad1[0xC];
    GameScene*  gamescene;            // 0x14
    char        _pad2[0x14];
    Component** componentlist_end;    // 0x28
    Component** componentlist;        // 0x2C
};
```

Helper functions (from the same post):

```cpp
string GetComponentName(Component* comp) {
    DWORD type_info = *(DWORD*)(comp->comp_info + 0x8);
    string ret((char*)(type_info + 0xC));
    return ret;
}
Component* GetComponentByName(TankVisual* tv, string compname) {
    int compcount = ((DWORD)tv->componentlist_end - (DWORD)tv->componentlist) / 4;
    for (int i = 0; i < compcount; i++)
        if (compname == GetComponentName(tv->componentlist[i]))
            return tv->componentlist[i];
    return NULL;
}
string GetPlayerName(DWORD Vehicle) { return string((char*)(Vehicle + 0x80)); }
string GetTankName(TankVisual* tv) {
    DWORD VehicleInstance = *(DWORD*)((DWORD)tv + 0x60);
    DWORD VehicleType = *(DWORD*)(VehicleInstance + 0x1C);
    string ret = string(*(char**)(VehicleType + 0x20));
    ret = ret.substr(ret.find(':') + 1);
    return ret;
}
int GetMaxHP(DWORD TankVisual) {
    DWORD VehicleDescr = *(DWORD*)(TankVisual + 0x64);
    return *(int*)(VehicleDescr + 0x34);
}
```

Notes:
- `GetTankName` uses `TankVisual + 0x60` → VehicleInstance; the newer #711725
  chain uses `VehicleGameLogic + 0xA8 → VehicleDescr`. Two different descriptor
  paths across versions/structures — consistent with our finding that descriptor
  offsets are version-sensitive.
- `GetComponentName` reads `comp_info + 0x8` (a `type_info`-like pointer) then
  name at `+0xC` — matches the OD-STATIC vtable/type_info work on main.

## Pattern Signatures (pymem, secretlay3r, May 2025)

Source: [#689828](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/689828-world-tanks-blitz-dava-game-engine-structure-hints.html)
(post #5). External pattern scan + patch (outline/ESP and nospread):

```python
espaddr     = patternhelper(pm, base, size, "8a 41 ? c3 cc cc cc cc cc cc cc cc cc cc cc cc 8d 41")
nospreadaddr= patternhelper(pm, base, size, "F3 0F ? ? ? ? ? ? F3 0F ? ? ? F2 0F ? ? ? ? ? ? F2 0F ? ? ? 0F 57 C0 F3 0F ? ? ? F3 0F ? ? ? F3 0F ? ? ? 0F")
# esp
pm.write_bytes(espaddr, b"\xB8\x01\x00\x00\x00\xC3", 6)
# nospread
pm.write_bytes(nospreadaddr, b"\x0F\x57\xC0\x90\x90\x90\x90\x90", 8)
```

Errorc0de's older silhouette signature (from #618977 era): `8b 40 04 ff d0 8b 4d dc`.

These are **patches**, not reads — we only read. Useful as pattern anchors when
locating the same functions in Ghidra for the static track.

## Player List / Pointer Scan Fragility

Source: [#606655](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/606655-finding-list-players-battle-world-tanks-blitz.html)
(2023).

- Static pointer scans for nicknames **break between map loads** — CE pointer
  scans have trouble when the address is reached via register-indexed
  addressing like `register + i*8 + offset`.
- Advice: use "find what accesses", and look for the tank instead of the
  nickname. This is why the community settles on the
  `AppContextImpl → ScreensFlow → AvatarContextBattle` chain rather than
  scanning for strings.
- **Relevance:** our `GuardedMemoryReader` external reads must also traverse
  the context chain, not hunt heap addresses.

## X-Ray / Outline Cheats (external, no injection)

Sources: [#618977](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/618977-wotblitz-steam-tanksblitz-lesta-ray-cheat.html)
and [#354495](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/354495-world-tanks-blitz-ray-cheat.html).

- External X-ray tools patch the silhouette/render function so outlines render
  whenever a tank is spotted (sixth-sense gated — unspotted enemies never
  render, server-side spotting).
- Works on Steam + Lesta; **only difference is process name and module name**
  (moderator-verified).
- Tundra/foliage removal is done externally via D3D11 render-parameter patching
  (DrNseven's D3D11-Wallhack approach) or pure game-value changes — no DLL
  injection. Consistent with our external-only policy.

## Network / Chat Protocol (XMPP over TLS)

Source: [#702797](https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/702797-parsing-encrypted-requests-ws2-32-dll-hook.html)
(2025, Lesta `tanksblitz.exe`, but author notes WG differs only by a few lines).

- Chat uses **XMPP** (`jabber:client` namespace; Wargaming extensions under
  `http://wargaming.net/xmpp`): messages carry `userid`, `nickname`, `clanid`,
  `clantag`, `muc-privileges role`.
- Traffic is **TLS-encrypted** (handshake bytes `16 03 01`); server responses
  are temporarily written decrypted into game memory — a plain memory scan for
  a known substring (`@wotblitz-ru.loc/wotbi`) finds live chat messages without
  TLS interception.
- A **Frida `ws2_32.dll` `send`/`recv` hook** captures all traffic (full JS +
  Python harness in thread); TLS records need SSL-layer handling to decrypt
  (not done in thread).
- Config endpoint observed: `cdn-ptl-static-2.tanksblitz.ru`
  `/tb-static/conf/regions_12.0.0_ruby.yaml` (Lesta 12.0.0 era region config).

**Relevance:** our 401-refresh / login-flow work touches the same auth surface.
The XMPP memory-scan trick shows decrypted-at-rest traffic is searchable — a
potential evidence path if we ever need chat/login telemetry (privacy rules
notwithstanding: never log chat content; this is only relevant to protocol
shape).

## DAVA Engine Open-Source Roots

- DAVA Engine was originally developed by **DAVA Consulting**, later
  Wargaming-maintained; early versions are **BSD-3-Clause open source**.
- Mirrors/forks on GitHub:
  - `github.com/smile4u/dava.engine`
  - `github.com/rifsxd/dava.engine.framework`
  - `github.com/vorlie/DAVA-Resource-Studio` (resource tooling: Data dirs, DVPL
    wrapping)
  - `github.com/DavaFramework` — **404/dead as of 2026-07-31**
- The shipped Blitz engine is a heavily customized fork (D3D11 pipeline, custom
  `.dvpl` asset packing) — open-source DAVA gives **naming/architecture
  conventions** (Core, AppContext, screens, scene graph, components), not the
  exact binary layout.

## DAVAProject User-Data Layout (community + our logs)

- `%LOCALAPPDATA%\wotblitz\DAVAProject\`
- Logs: `blitz-logs_YYYYMMDDHHMMSS.txt` (timestamped session files) with
  `[info]/[warning]/[error]` levels; markers `START_REPLAY_LOCAL`,
  `STOP_REPLAY_LOCAL`, `LoadGameScene`, `GameCore::OnBackground`
- `shader_cache\` — precompiled shader blobs (clearing it is a known fix for
  startup crashes)
- `cache\localizations\` + `dlc_prof.json` — DLC/resource-pack tracking
- **Microsoft Store/UWP variant:**
  `%LOCALAPPDATA%\Packages\7458BE2C.WorldofTanksBlitz_x4tje2y229k00\LocalState\DAVAProject`
- Window title: "World of Tanks Blitz"; borderless fullscreen windowed by
  default; D3D11
- SteamDB app 444200 (`steamdb.info/app/444200/`) for depot/version tracking

## Replay Parser Ecosystem (new leads)

- **`eigenein/wotbreplay-parser`** (Rust, active) — parses `.wotbreplay`;
  data model confirmed from README: `battle_results.timestamp_secs`,
  `players[14]` with `account_id`, `info.nickname`, `info.team()` (TeamNumber
  One/Two), `info.platoon_id`. **Reference implementation for validating our
  C# decoder.**
- **`eigenein/wotbreplay-inspector`** (Rust) — companion project for inspecting
  replay mechanics.
- **`evido/wotreplay-parser`** (C++) — WoT **PC** replay parser that renders
  minimap PNGs, animated GIF position tracks, and heatmaps (green allies / red
  enemies / blue recorder). Demonstrates exactly the position-track rendering
  our overlay does; Blitz equivalent not confirmed.
- **`rajesh-rahul/wot-replay-tools`** (Rust/WASM) — browser-based `.wotreplay`
  visualizer (WIP).
- **WOTInspector** (`replays.wotinspector.com`) — closed-source cloud parser;
  shot/trajectory/damage analysis + heatmaps; companion mods upload replays.
- **`Jylpah/blitz-replays`** (Python, active; successor to blitz-tools) —
  uploads to WOTInspector; client-side parsing still on roadmap.

## Community Tool Stack (confirmed 2025-2026)

| Tool | Use | Notes |
|------|-----|-------|
| Cheat Engine | scans, find-what-writes, pointer scans | pointer scans fragile vs register-indexed arrays |
| Frida | `ws2_32.dll` send/recv hooks; TLS record capture | full harness in #702797 |
| pymem (Python) | external read/scan/pattern patch | used for outline + nospread patches |
| Ghidra / IDA | static analysis of `wotblitz.exe` | our OD-STATIC track |
| ReClass.NET | runtime layout reconstruction | free |
| x64dbg | dynamic debugging | free |

## Leads Not Yet Fully Explored (rate-limited swarm)

- **`wg-toolkit-rs`** — Rust toolkit for Wargaming network protocols surfaced
  mid-search; not fully read. Potential protocol/replay reference.
- **PDB lead** — beta build with `.pdb` reportedly once in the game's Discord;
  if recoverable, static analysis gets class names/offsets directly.
- Reforged/UE5 status — swarm could not re-confirm beyond the repo's
  "announced 2026-06-17, postponed" note; treat as unchanged.

## Key URLs to Bookmark

- `unknowncheats.me/forum/other-mmorpg-and-strategy/711725-...` — ESP release + current entity chain
- `unknowncheats.me/forum/other-mmorpg-and-strategy/689828-...` — DAVA structure hints + struct dump
- `unknowncheats.me/forum/other-mmorpg-and-strategy/606655-...` — player list / pointer fragility
- `unknowncheats.me/forum/other-mmorpg-and-strategy/618977-...` — X-ray (Steam/Lesta)
- `unknowncheats.me/forum/other-mmorpg-and-strategy/702797-...` — XMPP/TLS/Frida traffic
- `github.com/eigenein/wotbreplay-parser` — Rust replay parser
- `github.com/eigenein/wotbreplay-inspector` — replay inspector companion
- `github.com/evido/wotreplay-parser` — C++ PC replay renderer (minimap/GIF/heatmap)
- `github.com/smile4u/dava.engine` + `rifsxd/dava.engine.framework` + `vorlie/DAVA-Resource-Studio`
- `steamdb.info/app/444200/` — SteamDB WoT Blitz
- `pcgamingwiki.com/wiki/World_of_Tanks_Blitz` — game data locations
