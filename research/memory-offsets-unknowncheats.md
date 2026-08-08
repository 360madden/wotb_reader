# Memory Offsets — UnknownCheats Research

Source: [UnknownCheats thread #689828](https://unknowncheats.me/forum/other-mmorpg-and-strategy/689828)
and related community discussions.

## Status (historical candidate family; current-build root refuted)

The layouts below are an attributed community hypothesis, not verified current
offsets. The repository's earlier “verified 2026-07-31” wording was not backed by
preserved post/version evidence and must not be used as promotion provenance.

Hash-bound static analysis of `11.19.0.10`
(`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`)
established:

- historical module root `0x03E91978` is **not a root** in this executable:
  non-pointer/string bytes, no relocation, and zero `.text` references;
- the exact `VehicleGameLogic` vtable is present at current-build RVA
  `0x0327DA50`; its slot `+0x04` resolves to RVA `0x0031B560`, the getter
  `MOV EAX,[ECX+0x04]`, so the old `+0x04` relationship remains a useful
  **entity-return clue**;
- 17 of the 79 `VehicleGameLogic` virtual methods call that getter and access
  23 distinct returned-entity offsets, but none accesses `+0x68`, `+0x6C`, or
  `+0x70`;
- a full executable scan found 111,693 generic `[reg+0x04]` loads, 68
  structurally related candidates, and only one complete chained
  `+0x68/+0x6C/+0x70` match. Decompilation identifies that match
  (`FUN_00c1ad60`, RVA `0x0081AD60`) as a matrix/pose interpolation-copy
  structure, not a `VehicleGameLogic`-anchored entity position;
- 662 same-base triple fallbacks demonstrate that these displacements alone
  are highly ambiguous. The strongest float-looking fallback also belongs to
  a 4x4 matrix populated by its caller.

Therefore this page supplies search vocabulary and a family shape only. Do not
read these offsets live, publish them, or call them current without a new
hash-bound entity/data-flow proof. Reproducible static triage is implemented by
`tools/ghidra-scripts/FindVehiclePositionFamily.java`.

**Caveat — Reforged:** DAVA-era offsets are time-limited. Wargaming is migrating to
Unreal Engine 5 (Reforged, announced 2026-06-17, postponed); when it ships, these
offsets are invalidated wholesale. The Reforged / UE5 migration risk is tracked
separately from this committed research index.

## Historical entity-list claim

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

## Historical class-structure claims

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

## Historical member claims

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

## Reload-timer claim

The cited community family also associates reload cooldowns with vehicle
descriptor sub-structures. No preserved release/version evidence in this repo
verifies that claim for the installed executable.

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

## Community technique notes (unverified here)

- **Tundra/foliage removal done externally** by patching D3D render parameters or
  memory flags — no DLL injection needed (avoids integrity checks). Consistent with
  this project's external-only tooling policy.
- Community discussions describe reload timers under `VehicleDescr`-like
  structures, but this repo has not preserved version-bound proof.
- Treat `VehicleGameLogic → entity +0x68/+0x6C/+0x70` as a stale candidate
  family. Current-build static evidence does not reproduce the complete
  position relationship.

## Application to our pipeline

- Keep the old root only as a regression test for static root rejection.
- Keep the `VehicleGameLogic`/entity names as RTTI, vtable, and source-string
  anchors.
- Follow the proven `VehicleGameLogic` entity getter and type-10 application
  data flow; do not scan or capture the stale member triple unchanged.
- `memory-offsets/*.json` remains unchanged until a stable resolver plus
  entity-bound live values satisfy the evidence and approval gates.
