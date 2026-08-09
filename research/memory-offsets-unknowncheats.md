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

## Current-build semantic result (OD-RECOVERY-069)

Treating the community material as a relationship family was fruitful even
though its offsets were stale. A hash-bound replay/entity map located the real
type-10 replay handler at RVA `0x00FE31C0` and followed it through
`BlitzServerMessageHandler` into the engine entity-movement path. The engine
resolver compares the type-10 entity ID to `[entity+0x1C]`, which proves the
identifier member that OD-RECOVERY-068 could only hypothesize.

At movement-application RVA `0x022FA780`, instruction RVA `0x022FA78D`
(`F30F7E00`) executes with `ESI` holding the resolved entity and `EAX` pointing
to the packet-derived XYZ vector. This is a useful **candidate family change**:
the next proof is an entity-bound instruction event, not a stale position
member. The downstream `BW::AvatarFilterHelper` stores the vector in an
8-entry ring (`0x38` stride, position at record `+0x18`), confirming position
semantics but also showing why that storage is unsuitable as a stable polling
offset.

No current position displacement, stable root, player identity, or publishable
offset follows from the community post alone. Live work remains gated on a
two-source capture that reads `[ESI+0x1C]` and 12 bytes from `EAX` within the
same held debug event. OD-RECOVERY-070 passed that synthetic proof, and
OD-RECOVERY-071 then captured 49 valid live hits in a positively verified
offline replay. Seven decoded vehicle entities matched exactly at Float32
precision, including the replay viewpoint entity. The stale community offsets
were therefore useful as a relationship clue even though none was reusable.
The result proves the new event-based player-position path for one static
window. OD-RECOVERY-072 then repeated the unchanged target on the other
content-distinct replay during movement. The replay viewpoint produced six
distinct triples with exact matches in the downsampled decoded trajectory.
Thus the community family ultimately led to a cross-replay-repeatable
event-based moving player-position read, but still not to reusable historical
offsets or a stable polling root.

## Stable-family convergence (OD-RECOVERY-073/074)

The relationship-first treatment produced a second useful result. Static data
flow from the proven entity resolver reaches a current-build module root and a
bounded entity-ID lookup rather than resurrecting any historical absolute
address:

- `[wotblitz.exe + 0x04095C88]` points to `GameCore`;
- `GameCore +0x0C` points to `AppController`;
- `AppController +0x124` points to `SessionController`;
- `SessionController +0x118` points to `AccountController`;
- `AccountController +0x128` points to the active `PlaybackController`;
- `PlaybackController +0x120` points to the replay `BWServerConnection`;
- the embedded `BWEntities` object starts at connection `+0x04`;
- the requested replay entity ID is resolved through the cache and three
  bounded map trees, with `[entity+0x1C]` revalidated;
- the AvatarFilter/AvatarFilterHelper vtables identify the supported movement
  filter before the newest 8-entry ring record is double-collected.

`TraceEntityRegistryPosition.java` pins 67 relationships to executable SHA-256
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` and
reports `replay-resolver-layout-proven`. The implementation exposes only a decoded
replay entity ID to the caller; PID, module base, root, tree layout, vtables,
and member offsets remain server-owned exact-build policy.

Bounded live checks proved that the original main `BWApp` connection does not
own the replay entity map and refuted an inferred `AppContext+0x118` owner.
The corrected root found the requested entity in the primary replay map for
24/24 requests, then stopped safely at an unverified vehicle-helper subtype.
This proves that the relationship family led to the replay-owned entity map in
one process, not that the helper ring is readable or that continuous polling
is reliable.

This confirms the right way to use stale community material: preserve object
names, ownership relations, and likely member roles as search hypotheses, then
re-derive every current-build address and displacement from hash-bound code.
The historical root and `+0x68/+0x6C/+0x70` position triple remain refuted.
The corrected resolver is a fresh current-build family. Another live poll is
not justified merely by broadening its helper allowlist. Current static
reference tracing names the new candidate as
`WGVehicleFilterHelper::vftable` at RVA `0x0325658C`, locates the constructor
vtable store at RVA `0x010139F1`, and finds the factory assignment to
`filter+0x08`. The position-store slot and ring compatibility remain to be
proved offline and covered by focused synthetic tests.
