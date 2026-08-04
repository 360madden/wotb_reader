# Memory Offsets — UnknownCheats Research

Source: [UnknownCheats thread #689828](https://unknowncheats.me/forum/other-mmorpg-and-strategy/689828)
and related community discussions.

## Status (verified 2026-07-31)

The entity-list pointer chain below is **still current** in community releases for
recent 11.x builds (11.19-era). The community tool stack is now Cheat Engine, Frida
(`ws2_32.dll` hooks for encrypted traffic), `pymem` (Python), and Ghidra/IDA — see
[community-tools.md](community-tools.md).

**Caveat — Reforged:** DAVA-era offsets are time-limited. Wargaming is migrating to
Unreal Engine 5 (Reforged, announced 2026-06-17, postponed); when it ships, these
offsets are invalidated wholesale. The Reforged / UE5 migration risk is tracked
separately from this committed research index.

## Entity List Pointer Chain

```
Base Module (tanksblitz.exe / wotblitz.exe) + 0x03E91978
  └── +0x0   → AppContextImpl
  └── +0x240 → ScreensFlow
  └── +0x98  → AvatarContextBattle (initialized only during battle)
  └── +0x60  → Context
  └── +0x10  → EntityList
```

### Entity List Layout
- Index `0x00` → Local player's tank (`VehicleGameLogic`)
- Index `0x08` → Platoon member's tank (if applicable)
- Up to 14 tanks, spaced at +0x8 intervals

## Class Structures

### Component
```cpp
struct Component {
    char    _pad0[0xC];
    DWORD   comp_info;       // +0x8
};
```

### Vehicle
```cpp
struct Vehicle {
    char    _pad0[0xB0];
    UINT8   Team;            // +0xB0
    char    _pad1[0x6B];
    int     MaxHP;           // +0x11C
};
```

### VehicleGameLogic
```cpp
struct VehicleGameLogic {
    char    _pad0[0x4];
    Vehicle* vehicle;         // +0x04
    char    _pad1[0x1B0];
    int     HP;               // +0x1B8
};
```

## Key Offsets

| Data | Chain | Offset |
|------|-------|--------|
| Position X | VehicleGameLogic → Vehicle | +0x68 |
| Position Y | VehicleGameLogic → Vehicle | +0x6C |
| Position Z | VehicleGameLogic → Vehicle | +0x70 |
| Player Name | Vehicle | +0x80 |
| Max HP | VehicleGameLogic → VehicleDescr (+0xA8) | +0x34 |
| Turret/Aim | VehicleGameLogic → Vehicle (+0x8) → +0x60 → +0x10 | rotational relative to hull |
| Spread | VehicleDescr | +0xD0 (float, visual only) |
| Team | Vehicle | +0xB0 |

## Reload Timers (2026 verification)

Recent open-source ESP tables extract per-tank reload cooldowns directly from the
vehicle descriptor structs (`VehicleGameLogic → VehicleDescr +0xA8`), letting external
overlays render live reload status. Position/turret/team offsets above remain the
canonical community set for 11.x.

## Other Structures

### GameCamera
```cpp
struct GameCamera {
    char    _pad[0x140];
    float   ViewMatrix;
};
```

### GameScene
```cpp
struct GameScene {
    char        _pad[0x128];
    GameCamera* gamecamera;  // +0x128
};
```

### SilhouetteComponent (Outline ESP)
```cpp
struct SilhouetteComponent {
    char    _pad0[0x11];
    bool    active;          // +0x11
    char    _pad1[0x02];
    Vector3 RGB;             // +0x14
};
```

### TankVisual
```cpp
struct TankVisual {
    char        _pad0[0x4];
    int         player_mask;          // +0x04
    char        _pad1[0xC];
    GameScene*  gamescene;            // +0x14
    char        _pad2[0x14];
    Component** componentlist_end;    // +0x28
    Component** componentlist;        // +0x2C
};
```

## Additional UC Threads

1. **#711725** — [Release] Tanks Blitz (Lesta) ESP Player's: external ESP, 2D/3D boxes, reload timers, distance counters
2. **#702797** — Parsing encrypted requests via ws2_32.dll hook, Frida memory scanning, XMPP/chat structures
3. **#606655** — Finding player list during battle, dynamic pointer reallocation, filtering by tank structures

## Community Technique Notes (verified 2026-07)

- **Tundra/foliage removal done externally** by patching D3D render parameters or
  memory flags — no DLL injection needed (avoids integrity checks). Consistent with
  this project's external-only tooling policy.
- **Reload timers** are read from `VehicleDescr` sub-structs by community ESP tools.
- **Position/rotation data** at `VehicleGameLogic → Vehicle → +0x68/0x6C/0x70` is
  confirmed by multiple independent community releases for 11.x.

## 2026-08-03 Swarm Additions (verified against full thread reads)

See [online-research-swarm-2026-08-03.md](online-research-swarm-2026-08-03.md) for the full
write-up, struct dump (Errorc0de), helper functions, and pattern signatures.

### Rotation is stored adjacent to position (playerYaw relevance)

Thread #711725 (post #7, author `24235234`, Lesta 26.2.1.28, confirmed working
on Steam/WG too):

> "The tank's position is stored in `0x? VehicleGameLogic --> 0x8 Vehicle -->
> 0x68 / 0x6C / 0x70` tank position. **Rotation is stored nearby.**"

Community ESP reads yaw from a float physically next to the XYZ block. Our
quarantined `playerYaw` hypothesis (conflicting representations) should be
re-tested around `Vehicle + 0x74` with the yaw-convention question in mind
(float yaw radians vs matrix/Euler).

### Turret vs hull rotation (representation conflict)

From the same post:

> "After where the tower is looking, I used `0x? VehicleGameLogic --> 0x8 Vehicle
> --> 0x60 TBVehicleFilter2 --> 0x10 TBVehicleFilterHelper --> somewhere there is
> the position and rotation along Y and X relative to the heir of the hull
> rotation from 0x8 Vehicle"

This documents the **hull-yaw vs turret-yaw separation** — exactly the kind of
representation conflict that got `playerYaw` quarantined. `TBVehicleFilter2` at
`Vehicle + 0x60` holds turret rotation relative to hull rotation.

### PDB lead (static analysis accelerant)

Thread #689828 (May 2025, `sa413x`): *"You can find the beta build with the
.pdb file in the game's Discord server."* Enesterio reported it later removed.
If a beta `.pdb` for `wotblitz.exe`/`tanksblitz.exe` is recoverable anywhere,
the OD-STATIC track gets class names and offsets directly.

### Lesta 26.x versioning

Lesta `tanksblitz.exe` uses a separate 26.x scheme (e.g., 26.2.1.28) vs WG
11.x (e.g., 11.19.0.10). Community confirms the two builds share structures;
the #618977 moderator noted the only difference between Steam and Lesta cheat
files is process name and module name.

### Player-list pointer fragility

Thread #606655: static pointer scans break between map loads because the DAVA
engine reaches entities via register-indexed addressing (`register + i*8 +
offset`), which CE pointer scans handle poorly. The working approach is the
`AppContextImpl → ScreensFlow → AvatarContextBattle` chain, not string/pointer
scans — matches our `GuardedMemoryReader` traversal strategy.

## Application to Our Pipeline

- The entity list base address (`BaseModule + 0x03E91978`) is version-specific but gives us a starting point for v11.19
- Vehicle position offsets (+0x68, +0x6C, +0x70 via Vehicle pointer) can be used with our `GuardedMemoryReader` 
- **Rotation is reported adjacent to position (candidate +0x74)** — re-test the quarantined `playerYaw` against this with the yaw-convention question open
- **Turret rotation at `TBVehicleFilter2` (Vehicle+0x60) → `TBVehicleFilterHelper` (+0x10)** — documents the hull/turret yaw split
- Player name at Vehicle+0x80 is public Wargaming statistics — no longer a privacy concern per project conventions
- The `GameScene` structure is a potential target for finding replay state
- Base address offset likely changes per version — our `memory-offsets/*.json` files will need updating for 11.19
