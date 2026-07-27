# Handoff — Minimap projection, game path auto-discovery, compare create

**Date:** 2026-07-27
**Status:** Complete — all three deferred items resolved

## What this session did

Three deferred items from previous handoffs were resolved across two commits:

### 1. Game path auto-discovery (`5807970`)

`MainWindow.xaml.cs` no longer hardcodes `C:\Games\World_of_Tanks_Blitz\wotblitz.exe`.
`FindGameExecutablePath()` searches in order:
1. `WOTB_GAME_PATH` env var
2. Default discovery roots (system drive, Steam x2)
3. Hardcoded fallback

`GetGameDiscoveryRoots()` mirrors `GameInstallationDiscovery.GetGameRoots()`
without depending on GameIntegration — preserves the Overlay's architectural
isolation as a loopback web client.

### 2. `compare create` CLI command (`5807970`)

`CliCommandRouter` now supports `compare create <left-session-id> <right-session-id>`:
1. Loads both session projections
2. Converts `CanonicalEvent` → `TelemetryEvent` with accurate decoder provenance
3. Runs `ITelemetryComparator.CompareAsync`
4. Persists via `IComparisonRunRepository.AddAsync`
5. Returns run ID + summary

DI auto-resolves `ITelemetryComparator` (already registered via `AddCaptureLogs`).

### 3. Minimap projection (`67b652a`)

Accurate HUD overlay of the game minimap using fixed world boundaries:

**Backend (10 files):**
- `MapBoundary` record (Core): MapId, MinX/MaxX/MinZ/MaxZ
- `ISessionQueryRepository.GetMapBoundariesAsync()`: new contract method
- `SqliteSessionQueryRepository`: `GROUP BY map_id` over `position_samples JOIN battle_sessions`, computing MIN/MAX raw_x/raw_z per map
- `GET /api/v1/maps/boundaries` endpoint
- `MapBoundaryResponse` in web + overlay contracts

**Overlay (7 files):**
- `PlotTransform.Fit()` overload with optional world bounds — falls back to per-session extents when unavailable
- `PositionPlot`: four new dependency properties (`WorldMinX/MaxX/MinZ/MaxZ`)
- `MainViewModel`: fetches boundaries once, caches by MapId, applies to current session
- `SessionRow`: added `MapId` field
- `MainWindow.xaml`: PositionPlot binds world boundary properties

**Fixes applied:**
- Null-forgiving operator noise removed from PositionPlot
- `_boundariesFetched` sentinel prevents retry loop when DB has no positions
- 2 fake implementations + 3 test constructor calls updated for new contracts

## Validation

| Check | Result |
|-------|--------|
| Full solution build (Release) | 0 errors, 0 warnings |
| Full test suite (12 projects) | **233 passed, 0 failed, 2 skipped** |

## Remaining

- **Live HUD smoke test**: Verify transparent window, position dots (now accurately projected), and Launch button against a real WoT Blitz installation. Requires display + WoT Blitz installed.
- **Validate + push**: Run `validate.cmd` (or `scripts/validate.ps1`) then push to `origin/main`.

## Commits in this session

| Commit | Description | Files |
|--------|-------------|-------|
| `5807970` | Game path auto-discovery + compare create | 2 files |
| `b3e7c85` | Roadmap update + handoff | 2 files |
| `67b652a` | Minimap projection with map boundary computation | 19 files |
