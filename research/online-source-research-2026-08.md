# Online RE Source Index — 2026-08 swarm

Comprehensive index of online sources with information valuable for reverse
engineering World of Tanks Blitz, gathered by a multi-wave research swarm in
August 2026. Organized by domain; every entry has a URL and the concrete
technical value it provides. Cross-reference: `community-tools.md`,
`memory-offsets-unknowncheats.md`, `dava-engine.md`.

> **Verification status legend**
> `[verified]` — I fetched and read the page/source myself.
> `[reported]` — reported by a research agent; URL confirmed reachable, claims not
> re-read in full here. Treat byte-level claims as leads, not ground truth.

---

## 1. Replay file formats

### 1.1 WoT PC `.wotreplay` (BigWorld reference format)

- **Monstrofil/replays_unpack** — `[verified]`
  https://github.com/Monstrofil/replays_unpack
  Pure-Python parser for BigWorld-engine replays (WoT PC / WoWS / WoWP).
  Decodes container → decrypts (Blowfish, hardcoded WG key) → zlib-decompresses
  → splits into BigWorld packets → dispatches entity methods/properties against
  the shipped `.def` files. Subclass `ReplayPlayer` to extract custom data.
  **This is the single best reference for the BigWorld replay protocol.**
  - `docs/Introduction.md` — `[verified]` base container format:
    - 8-byte header: magic `uint32` + block count `uint32`
    - each block: `uint32` data length + data; first block is a JSON struct
    - remainder of file = compressed + encrypted replay stream
    - (via http://wiki.vbaddict.net/pages/File_Replays)
  - `docs/Packets.md` — `[verified]` BigWorld packet-ID map:
    - `0x0` BasePlayerCreate · `0x1` CellPlayerCreate · `0x2` EntityControl
    - `0x3` EntityEnter · `0x4` EntityLeave · `0x5` EntityCreate
    - `0x7` EntityProperty · `0x8` EntityMethod · `0x22` NestedProperty /
      BattleStats (12.6+) · `0x27` Map (pre-12.6)
    - `0x0a` Position · `0x16` Version · `0x2b` PlayerPosition
  - `docs/packets/0x8.md` — `[verified]` EntityMethod layout:
    `struct EntityMethod { int32 entityID; int32 messageID; BinaryIStream& data; }`
    messageID is generated at build time — the hard part per-patch.
  - `docs/Getting exposed index for properties and methods.md` — algorithm for
    computing property/method indices from `.def` files (the packet-decoding key).
  - `tools/extract_constants.py` — runs inside the game's Python 2.7 sandbox to
    dump player-property maps from real game scripts; CI auto-adds new WoWS
    patches from data drops.
- **vbaddict wiki: File_Replays** — `[reported]`
  http://wiki.vbaddict.net/pages/File_Replays
  Original source of the magic/block-count header description.
- **evido/wotreplay-parser** (C++) — `[reported]` https://github.com/evido/wotreplay-parser
- **Phalynx/WoT-Replay-To-JSON** (Python) — `[reported]` https://github.com/Phalynx/WoT-Replay-To-JSON
- **Aimdrol/WoT-Replay-Analyzer** (exe tool) — `[reported]` https://github.com/Aimdrol/WoT-Replay-Analyzer
- **kosh04/wotreplay** (Go CLI) — `[reported]` https://github.com/kosh04/wotreplay
- **rglos/CSharpWoTReplayParserPoC** (C#) — `[reported]` https://github.com/rglos/CSharpWoTReplayParserPoC
- **rajesh-rahul/wot-battle-results-parser** (Rust) — `[reported]`
  https://github.com/rajesh-rahul/wot-battle-results-parser
- **benvanstaveren/2402016** (Perl gist) — `[reported]` https://gist.github.com/benvanstaveren/2402016
- **thesilvervestgroup/wot-replay-parser** (PHP) — `[reported]`
  https://github.com/thesilvervestgroup/wot-replay-parser

### 1.2 WoT Blitz `.wotbreplay`

- **eigenein/wotbreplay-parser** (Rust) — `[verified]`
  https://github.com/eigenein/wotbreplay-parser
  Opens `.wotbreplay`, reads typed battle results: `timestamp_secs`, per-player
  `account_id`, `nickname`, `team`, `platoon_id`. README example parses a real
  14-player Blitz replay. Research agent reports the crate opens `.wotbreplay`
  **ZIP archives** — i.e. Blitz replays may be ZIP containers (see §7 lead #1;
  verify against our own decoder, which reads pickle/protobuf streams).
- **eigenein/wotbreplay-inspector** (Rust CLI) — `[reported]`
  https://github.com/eigenein/wotbreplay-inspector
  `dump-data` subcommand dumps raw packet data incl. `UpdateArena` clock ticks.
- **Jylpah/blitz-tools** → **Jylpah/blitz-replays** (Python) — `[verified]`
  https://github.com/Jylpah/blitz-replays (successor; blitz-tools is unmaintained)
  `blitz-replays upload|analyze|parse` + `blitz-data tankopedia|maps`.
  Uploads replays to replays.wotinspector.com; parses battle results JSON
  client-side; extracts tankopedia/maps from game files or WG API.
- **WOTInspector Replays — Blitz** — `[reported]`
  https://replays.wotinspector.com/en/blitz/
  Largest public Blitz replay DB. Upload via email (`replays@wotinspector.com`
  from the game client), Discord bots (`@wotbot`, `@SerBot`), Map Inspector app,
  or web drag-and-drop. Useful as a corpus of real replay files.
- **Version history** — `[reported]` native replays introduced in **Update 4.10
  (May 2018)**; format stabilized through 6.x–8.x; 9.x–11.x added richer battle
  results JSON (multi-mode battles, 10v10 in v11.3). Replays are strictly
  version-locked (consistent with our 11.18/11.19 finding).
  - wot-blitz.fandom.com/wiki/Game_Updates — `[reported]`
    https://wot-blitz.fandom.com/wiki/Game_Updates — exhaustive update log.

---

## 2. BigWorld engine / protocol knowledge

- **Monstrofil/replays_unpack** is the practical reimplementation of the
  BigWorld client/server protocol as used in replays — `.def`-driven packet
  decoding, no hand-maintained packet IDs (§1.1).
- **BigWorld Technology docs archives** — `[reported]`
  - http://bigworldtech.com (historical) and kb.bigworldtech.com (knowledge base)
  - Documented: server kit (loginapp/baseapp/cellapp), entity defs, stream
    classes (`BinaryOStream`/`BinaryIStream`), message headers, fixed/var-length
    packets, fragmentation, entity property replication.
  - BigWorld had **native replay recording** support in the engine — the WoT
    replay stream is that feature's output.
- **hedger/BWXML** — `[reported]` https://github.com/hedger/BWXML
  Unpacks/re-packs BigWorld binary XML (`.def`/config files). Classic tool.
- **StranikS-Scan/WorldOfTanks-Decompiled** — `[reported]`
  https://github.com/StranikS-Scan/WorldOfTanks-Decompiled
  Decompiled WoT client Python scripts across many versions — exposes core
  engine logic and UI controllers. WoT PC, but the Python/entity layer is
  BigWorld-shared and highly transferable.
- **Mikeyzy/WoT_ModDevTools** — `[reported]` https://github.com/Mikeyzy/WoT_ModDevTools
  Client unpackers + `.wotmod` packaging scripts for the embedded Python runtime.

---

## 3. DAVA engine / on-disk data formats

### 3.1 DVPL container

- **Tankerch/dvpl_converter** (Go) — `[reported]` https://github.com/Tankerch/dvpl_converter
  README documents the **20-byte DVPL footer spec**: input size, compressed
  size, crc32, compression type, big-endian `"DVPL"` magic. Compression: none /
  LZ4 / LZ4HC / Deflate (RFC1951).
- **rifsxd/pydvpl** (Python 3.10+) — `[reported]` https://github.com/rifsxd/pydvpl
  SmartDLC `.dvpl` unpack with verification, multi-threaded.
- **lanylow/dvpl** (Python) — `[reported]` https://github.com/lanylow/dvpl
  Drag-and-drop DVPL compress/decompress (LZ4, LZ4HC, Deflate).

### 3.2 DVPM / DVPD packages

- **Pyogenics/WOTBDVPFormat** — `[reported]` https://github.com/Pyogenics/WOTBDVPFormat
  Spec (from `dvpm.py`):
  - meta section, magic `"met3"` (file counts, archive descriptors, LZ4-encoded
    path strings)
  - file table: `FileEntries` with 64-bit offsets, compressed/uncompressed
    sizes, CRC32, compression flags (`0` none, `1` LZ4, `2` LZ4_HC,
    `3` RFC1951)
  - 44-byte footer ending with magic `"DVPM"`

### 3.3 DAVA scene/model formats

- **vorlie/DAVA-Resource-Studio** — `[verified]`
  https://github.com/vorlie/DAVA-Resource-Studio
  Windows desktop toolkit (Rust + Tauri + React) for browsing/editing/repacking
  WoT Blitz DAVA/DVPL resources. Concrete facts confirmed by reading it:
  - game `Data` directory presented as virtual FS with transparent `.dvpl` reads
  - `.sc2` = DAVA scene/effect assets (`Data/3d/...` vehicles, maps, hangars,
    VFX) — engine-specific, not text
  - `.scg` = engine graph data; `.dx11.dds` = DX11 texture variant; `.pvr`
    PowerVR; `.anim/.animation/.actions` animation; `.bnk`/`.pck` = Wwise
    audio banks/packages; `.heightmap` terrain; `.lka/.mkm/.model` engine assets
  - text resources: `.yaml/.yml/.json`, `.material` (YAML-like schema),
    `.sl/.slh` DAVA shader sources
  - **shader cache path confirmed**: `%LOCALAPPDATA%\wotblitz\DAVAProject\shader_cache`
  - refuses mutations while `wotblitz.exe` is running
  - crates: `dvpl` (pack/unpack/CRC/classification), `resourcefs` (VFS with
    staged writes), `game` (install detection) — Rust source is crib-able.
- **Pyogenics/WOTBSCPGFormat** — `[reported]`
  https://github.com/Pyogenics/WOTBSCPGFormat
  `.scg`/`.sc2` (SFV2 / SCPG) model formats under `/packs/3d/Tanks/`; vertex
  formats (PolygonGroup); `gmConverter3D` reference parsers.
- **theorzr/wg-toolkit-rs** (Rust) — `[reported]` https://github.com/theorzr/wg-toolkit-rs
  Packed-XML codec, tank model codecs (visual tree, vertices, indices), compiled
  space codecs (`.bin`: BWTB, BWST, BWT2, BWSG, BWCS, BWAL), VFS package index.
  **Note: these are BigWorld-era codecs — Blitz's DAVA equivalents differ.**
- **mikeoverbay/PKGexplorer** (C#) — `[reported]` https://github.com/mikeoverbay/PKGexplorer
  WoT `.pkg` archive explorer with XML/model viewers (PC-focused).

### 3.4 DAVA engine source

- `github.com/dava/dava.engine` → **404, does not exist** `[verified]`.
  The UnknownCheats thread hint ("search dava.engine in GitHub, first repo")
  does not resolve to a canonical DAVA source mirror today. Treat any public
  "DAVA engine source" as unconfirmed until located.
- UnknownCheats 689828 thread (below) notes a **beta build with `.pdb` symbols
  is/was distributed via the game's official Discord** — the most promising
  source of real symbol names for `wotblitz.exe`. `[reported]`

---

## 4. Memory layout / offsets (Windows client)

### 4.1 DAVA engine class structures — UnknownCheats 689828 `[verified]`
https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/689828-world-tanks-blitz-dava-game-engine-structure-hints.html
(March–May 2025; community-maintained summary thread.)

- Structs posted by `snobbob0` (credits `Errorc0de`):
  ```
  struct Vehicle { ... UINT8 Team; /* 0xB0 */ int MaxHP; /* 0x11C */ };
  struct VehicleGameLogic { Vehicle* vehicle; /* 0x4 */ int HP; /* 0x1B8 */ };
  struct GameCamera { float ViewMatrix; /* 0x140 */ };
  struct GameScene { GameCamera* gamecamera; /* 0x128 */ };
  struct SilhouetteComponent { bool active; /* 0x11 */ Vector3 RGB; /* 0x14 */ };
  struct TankVisual { int player_mask; /* 0x4 */ GameScene* gamescene; /* 0x14 */
                      Component** componentlist_end; /* 0x28 */
                      Component** componentlist; /* 0x2C */ };
  ```
  (Note the thread itself points out the list/end pointers are swapped in the
  original paste — verify before trusting.)
- Component names resolved via RTTI: `comp_info + 0x8` → `type_info + 0xC`.
- Helper functions: `GetPlayerName(Vehicle + 0x80)`, `GetTankName(TankVisual
  + 0x60 → VehicleInstance + 0x1C → VehicleType + 0x20)`, `GetMaxHP(TankVisual
  + 0x64 → VehicleDescr + 0x34)`.
- AOB byte-patch script (pymem) for outline ESP + no-spread:
  - outline pattern: `8a 41 ? c3 cc cc cc cc cc cc cc cc cc cc cc cc 8d 41`
    (patch 6 bytes `B8 01 00 00 00 C3`)
  - no-spread pattern: `F3 0F ? ? ? ? ? ? F3 0F ? ? ? F2 0F ? ? ? ? ? ? F2 0F ? ? ? 0F 57 C0 F3 0F ? ? ? F3 0F ? ? ? F3 0F ? ? ? 0F`
- No-spread is **visual only**; damage/position validation is server-side.
- Tundra (bush removal) via D3D11 hook works but is situational (renders tanks
  through map objects only when spotted).

### 4.2 Lesta Tanks Blitz ESP thread — UnknownCheats 711725 `[reported]`
https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/711725-tanks-blitz-lesta-esp-players.html
(Aug 2025–Mar 2026; game version 26.2.1.28)

- Entity list chain (external, `tanksblitz.exe`):
  ```
  base = "tanksblitz.exe" + 0x03E91978
    └ 0x0  AppContextImpl
        └ 0x240 ScreensFlow
            └ 0x98 AvatarContextBattle (battle only)
                └ 0x60 (container)
                    └ 0x10 EntityList  (VehicleGameLogic*, 0x8 stride, ≤14)
  ```
- Local/platoon tank at index 0x0 / 0x8.
- Position: `VehicleGameLogic → 0x8 Vehicle → 0x68/0x6C/0x70` (XYZ floats).
- Turret/rotation filter: `Vehicle → 0x60 TBVehicleFilter2 → 0x10
  TBVehicleFilterHelper`.
- Vehicle descriptor: `VehicleGameLogic → 0xA8 VehicleDescr`; client spread at
  `VehicleDescr + 0xD0` (0.4–3.0 → 0.0 is visual only).

### 4.3 Player list thread — UnknownCheats 606655 `[reported]`
https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/606655-finding-list-players-battle-world-tanks-blitz.html
(Oct 2023–Mar 2025)
- Nickname-string lists are unstable across map transitions; scan entity
  structures (health/coords) instead — index-based `register + i*8 + offset`
  dynamic allocation defeats static CE pointer scans.

### 4.4 Network sniffing / Frida — UnknownCheats 702797 `[reported]`
https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/702797-world-tanks-blitz-parsing-incoming-outcoming-encrypted-requests.html
(May–Jul 2025)
- Burp fails (TLS + custom packets). Harvest unencrypted XML chat/server
  responses from memory with pymem + `VirtualQueryEx` scanning
  (`PAGE_READWRITE`/`PAGE_READONLY` for `@wotblitz-ru.loc/wotbi`, `<message>`).
- Frida hooking of `ws2_32.dll` `send`/`recv` works; TLS handshake signatures
  observed (`16 03 01 ...`).

### 4.5 WoT HEAT (DAVA-derivative) — UnknownCheats 755512 `[reported]`
https://www.unknowncheats.me/forum/other-fps-games/755512-world-tanks-heat-reversal-structs-offsets.html
(Jun 2026)
- BroEngine/DAVA-derivative used by newer WG/Lesta titles:
  - `engine::Engine::InstanceHolder::m_instance`; `ServiceManager` + 0x88;
    `ServiceManager::find` + 0x898 (+0x920); services: RenderService,
    WorldService, StringPoolService, InputService.
  - ECS: `WorldService + 0xC0` → `engine::ecs::World`; `World + 0x70` →
    `EntityManager`; components via `EntityManager::ComponentManager`.
  - Camera: `CameraTransformsComponent` — Fov 0x04, View 0x14, Proj 0x54,
    **ViewProj 0x94 (world-to-screen)**, World 0xD4 (`_41,_42,_43` = cam pos).
  - Chams: hook `EffectiveVisibilityTargetProcessSystem` in
    `plugin_cw_core.dll` (setter RVA 0x341860); `eff_vis_flag_off=0x28`,
    `eff_vis_value_off=0x04`.
- **Value:** shows the DAVA family's post-BigWorld direction — useful if the
  UE5 migration draws on these newer engine services.

---

## 5. Anti-cheat / risk profile

- **No third-party anti-cheat** on WoT Blitz (no Easy Anti-Cheat, BattlEye,
  GameGuard, no kernel driver, no strong packing of `wotblitz.exe`). `[reported]`
  Sources:
  - https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/606471-chance-world-tanks-blitz-hacks.html
  - https://www.unknowncheats.me/forum/other-mmorpg-and-strategy/618977-wotblitz-steam-tanksblitz-ray-cheat.html
- External `ReadProcessMemory` tools operate without interference. Risk for an
  offline replay reader ≈ zero (no AC agent to detect file reads).
- WoT PC enforcement is server-side telemetry + delayed ban waves (not client):
  - https://worldoftanks.eu/en/news/general-news/ban-wave-september-2024/
  - https://worldoftanks.com/en/news/general-news/forbidden-mods/
- Currency/health are server-authoritative; client edits are cosmetic/visual
  only. `[reported]`

---

## 6. Networking / login / APIs

- **WG support doc: ports 20010 / 20013 / 20020** — `[reported]`
  (Wargaming connectivity KB) BigWorld TCP service ports to whitelist.
- **WG login flow**: openid redirect via `wargaming.net/id`, access token
  exchange, then HTTPS API — `[reported]` (general WG pattern; WoT PC
  documented community-wide).
- **Official WoT Blitz developer API** — `[verified]` (agent detail)
  https://developers.wargaming.net/reference/
  - `GET /wotb/encyclopedia/vehicles/` — tankopedia vehicles (tier/nation/type)
  - `GET /wotb/encyclopedia/info/` — version tags for encyclopedia
  - `GET /wotb/account/list/` — account search by name prefix
  - `GET /wotb/account/info/` — player stats by `account_id`
  - `GET /wotb/tanks/stats/` — per-vehicle stats
  - Regions: ru | eu | com | asia | china | BOTS. **Use: map `tank_id` →
    name, resolve player IDs in replay battle results.**

---

## 7. Open leads / things to verify

1. **Is `.wotbreplay` a ZIP container?** Research agent (and the design of
   `eigenein/wotbreplay-parser`, which opens ZIPs) says yes; our decoder reads
   pickle/protobuf streams. Reconciling these two models is the highest-value
   next experiment: hex-dump a real `.wotbreplay` and check for `PK\x03\x04`.
2. **Blowfish key value** for WoT replay payloads: located in
   `replays_unpack` crypto module (hardcoded WG key) — extract and compare with
   Blitz (may be unencrypted ZIP; see #1).
3. **Lesta client layout** (tanksblitz.exe): replay path + extension
   (`.tbreplay`?) under the Lesta launcher; whether WG and Lesta formats are
   interchangeable. Partially evidenced by §4.2.
4. **`.pdb` availability**: beta build PDB distributed via the game's official
   Discord (per 689828) — huge for symbol-driven RE if still obtainable.
5. **DAVA engine source**: canonical mirror not found (`dava/dava.engine`
   404s). Watch Russian hosting (gitflic, etc.) and the `vorlie` toolchain for
   format headers.
6. **UE5 Reforged**: §8 — everything DAVA will be invalidated when it ships.

---

## 8. UE5 Reforged migration status (as of Aug 2026)

- Announced **2025-01-16**; **released/dated 2026-06-17**; **postponed on
  2026-06-17** after the Preview Weekend, by 3–6 months. `[reported]`
  - https://wotblitz.com/en/news/updates/world-of-tanks-blitz-reforged-update-postponed/
  - https://wargaming.com/en/news/world-of-tanks-blitz-reforged-update/
  - https://na.wotblitz.com/en/news/common/reforged-date/
  - https://eu.wotblitz.com/en/content/reforged-update/
- File structure shifts DAVA → UE: `.pak/.ucas/.utoc`, `.uasset/.umap`; legacy
  `%LOCALAPPDATA%\wotblitz\DAVAProject\` paths deprecated. `[reported]`
- Replay format: legacy `.wotbreplay` **not natively replayable** by the UE5
  client without a compatibility wrapper; replay/telemetry systems being
  restructured. `[reported]` — treat as high-confidence risk, verify when
  Reforged ships.
- Precedent: WoT PC 1.0 Core-engine migration (Mar 2018) forced complete
  rewrite of asset extractors — analysis writeup:
  https://peteronprogramming.wordpress.com/2018/04/03/interleaving-small-reads-of-multiple-files-why-world-of-tanks-1-0-has-abysmal-loading-times-on-hdds/
- Confirms the project's existing Reforged STRATEGIC RISK note and the
  "prioritize pipeline work that survives migration" guidance.

---

## 9. Data / community ecosystem

- **Blitz Hangar** — `[reported]` https://blitzhangar.com/en
  Tankopedia knowledge base, armor models, weakspots.
- **BlitzMods** — `[reported]` https://blitz-mods.com/
  Active mod portal (skins, sounds, hangars); Steam + Android + Lesta support.
- **BlitzModder** — `[reported]` https://github.com/BlitzModder
  Historical open-source mod manager framework (BMPC/BMAndroid/BMiOS).
- **Dataminers**: Telegram `@wotblitzpost` / `@blitzmodspublic`, Discord leak
  servers; community scripts parse game JSON dumps for hidden stats. `[reported]`
- **Mobile**: package `com.wargaming.blitz`, native `libblitz.so` (DAVA C++,
  not IL2CPP/Unity); GameGuardian LUA scripts target client-side floats only.
  Formats (DVPL/DVPM/SC2) are shared across mobile and Windows. `[reported]`
  - https://github.com/Pyogenics/WOTBDVPFormat
  - https://gameguardian.net/forum/topic/38514-help-on-world-of-tanks-blitz/
  - https://gameguardian.net/forum/topic/32703-lua-script-for-world-tank-of-blizt/

---

## 10. Implications for Treader

1. **Battle-results extraction**: `eigenein/wotbreplay-parser` (Rust) and
   `Jylpah/blitz-replays` (Python) both parse battle results (account IDs,
   nicknames, teams, platoons, timestamps) from `.wotbreplay` today — our
   decoder's battle-results path can be cross-checked against them.
2. **BigWorld packet decode**: `Monstrofil/replays_unpack` is the reference
   implementation (`.def`-driven, Blowfish, zlib, packet-ID table) — mirror its
   structure rather than reinventing.
3. **ZIP-vs-pickle question** (§7 #1) is now testable: one real `.wotbreplay`
   hex dump settles whether the container is ZIP (metadata + JSON) with a
   pickle/protobuf inner stream — likely **both** are true (ZIP outer,
   BigWorld-ish inner stream), which would reconcile the two models.
4. **Memory offsets** (§4) remain unvalidated community claims from 2025 —
   consistent with the project's policy: keep candidate offsets evidence-only.
5. **Reforged risk** is now source-backed with dates; keep DAVA work
   migration-aware.
