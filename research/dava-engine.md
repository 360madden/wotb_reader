# DAVA Engine — Open Source Analysis

## Status Update (2026-07-31): SUPERSEDED BY REFORGED

**The DAVA open-source angle is closed, and the engine itself is being retired.**

1. `github.com/DavaFramework` and `github.com/DavaFramework/dava.engine` **return
   404** (verified 2026-07-31). The engine source is not publicly accessible —
   the existing "blocker" is confirmed dead.
2. Wargaming announced the **Reforged** update: WoT Blitz migrates from DAVA to
   **Unreal Engine 5**. Announced for 2026-06-17, **postponed indefinitely**; the
   live client still runs DAVA. The Reforged / UE5 risk is tracked separately from
   this committed research index.

**Consequence:** Studying DAVA source to learn single-instance behavior, command-line
parsing, or file watching has no viable source. Community reverse engineering
(UnknownCheats threads) is the only path — and its findings are time-limited to the
DAVA era.

---

## Legacy Content (retained for context — DAVA-specific)

## Discovery

The DAVA Engine (DavaFramework) is **partially open source on GitHub**:
- Organization: `https://github.com/DavaFramework`
- The engine that powers WoT Blitz has public source code available

> ⚠️ **2026-07-31:** The above organization and repo are **404** (verified).
> The links below are retained only as a record of the earlier (unverified) claim.

This is significant because we can study:
- How the engine handles command-line arguments
- File system watching and asset loading
- Application lifecycle (single-instance detection)
- Scene management (loading/unloading replays)

## Key Source Files to Investigate

### Application Entry Point
The engine's `Application` class likely handles:
- Command-line argument parsing
- Single-instance mutex detection
- Window creation and message pump
- Scene initialization

### FileSystem Module
The engine's `FileSystem` module likely provides:
- File watching (`FileSystemWatcher`-like functionality)
- Asset path resolution
- Platform-agnostic file I/O

Potential location: `Modules/FileSystem/` or similar

### Scene Management
The engine's scene system controls:
- Scene loading and unloading
- Scene transitions (hangar → replay → hangar)
- Asset lifecycle management

This is what handles the "context switch" when loading a new replay.

## Engine Architecture (from community research)

Based on UnknownCheats analysis of DAVA Engine in WoT Blitz:
```
GameScene
  └── TankVisual (component-based)
       ├── Component
       ├── SilhouetteComponent
       └── VehicleGameLogic
            ├── Health pool
            ├── Team flags
            ├── Max HP
            └── Destruction state
```

Entity lists and object states are accessible via static pointers and offsets.

## What We Can Learn (2026-07-31 revision)

> ⚠️ **Superseded:** the original items below were written assuming the DAVA source
> was readable. The source is **404** and the engine is being retired (Reforged/UE5),
> so the old claims no longer hold. Current answers:

1. **Single-instance behavior:** Unknown; only a live test on `wotblitz.exe` answers it.
2. **Command-line parsing:** Confirmed pattern is positional replay path as argv[1]
   (file association); no official CLI doc.
3. **File watching:** No evidence the game watches the replays directory.
4. **Scene transitions:** Engine will be replaced by UE5 (Reforged) — see
   the separate Reforged / UE5 risk note.

## GitHub Repos to Clone/Analyze

- `github.com/DavaFramework/DavaEngine` (or similar) — **404 as of 2026-07-31**
- Look for: `Application.cpp`, `FileSystem.cpp`, `SceneManager.cpp`
- Search for: "mutex", "single instance", "argv", "replay", "scene transition"
