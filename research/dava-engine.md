# DAVA Engine — Open Source Analysis

## Discovery

The DAVA Engine (DavaFramework) is **partially open source on GitHub**:
- Organization: `https://github.com/DavaFramework`
- The engine that powers WoT Blitz has public source code available

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

## What We Can Learn

1. **Single-instance behavior:** If the `Application` class implements a named mutex,
   we can find it in the open source and understand how the game detects existing instances.

2. **Command-line parsing:** The engine's argument parsing reveals what flags and
   file arguments are supported — including replay paths.

3. **File watching:** If the engine has built-in file watching, we can determine
   which directories are monitored and whether dropping replays triggers auto-load.

4. **Scene transitions:** Understanding how scene loading works helps optimize
   the restart cycle (Approach E).

## GitHub Repos to Clone/Analyze

- `github.com/DavaFramework/DavaEngine` (or similar)
- Look for: `Application.cpp`, `FileSystem.cpp`, `SceneManager.cpp`
- Search for: "mutex", "single instance", "argv", "replay", "scene transition"
