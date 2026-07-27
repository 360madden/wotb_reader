# Handoff — Game path auto-discovery + compare create

**Date:** 2026-07-27
**Status:** Complete — both features implemented and validated

## What this session did

Two deferred items from previous handoffs are now resolved:

### 1. Game path auto-discovery (`5807970`)

`MainWindow.xaml.cs` no longer hardcodes `C:\Games\World_of_Tanks_Blitz\wotblitz.exe`.
Instead, `FindGameExecutablePath()` searches in order:

1. `WOTB_GAME_PATH` environment variable (user override)
2. Default discovery roots (mirrors `GameInstallationDiscovery`):
   - `{SystemDrive}\Games\World_of_Tanks_Blitz`
   - `%ProgramFiles(x86)%\Steam\steamapps\common\World of Tanks Blitz`
   - `%ProgramFiles%\Steam\steamapps\common\World of Tanks Blitz`
3. Hardcoded fallback (`C:\Games\World_of_Tanks_Blitz\wotblitz.exe`)

`GetGameDiscoveryRoots()` is a lightweight replica of
`GameInstallationDiscovery.GetGameRoots()` — no dependency on
GameIntegration, preserving the Overlay's architectural isolation.

### 2. `compare create` CLI command (`5807970`)

`CliCommandRouter` now supports `compare create <left-session-id> <right-session-id>`:

1. Loads both session projections via `ISessionQueryRepository`
2. Converts `CanonicalEvent` → `TelemetryEvent` via `ConvertToTelemetryEvents()`
   with accurate provenance (`DecoderId` + `SourceArtifactId` from projection)
3. Runs `ITelemetryComparator.CompareAsync` with `ComparisonOptions.Default`
4. Persists the result via `IComparisonRunRepository.AddAsync`
5. Returns the new comparison run ID, comparator info, and summary counts

DI wiring: `ITelemetryComparator` was added to `CliCommandRouter`'s constructor.
Since it's already registered in the container via `AddCaptureLogs()`, no
Bootstrap changes were needed — DI auto-resolves.

### 3. Roadmap updated

Roadmap now reflects all completed work including the HUD features and
compare create. Deferred/future items section added.

## Validation

| Check | Result |
|-------|--------|
| Full solution build (Release) | 0 errors, 0 warnings |
| Full test suite (12 projects) | **233 passed, 0 failed, 2 skipped** |
| CLI tests (15) | All pass |
| Overlay tests (41) | All pass |

## Architecture notes

- The overlay's game path discovery is intentionally a lightweight replica of
  `GameInstallationDiscovery.GetGameRoots()`. Adding a project reference from
  Overlay → GameIntegration would violate the architecture (Overlay is a
  loopback web client; no parser/storage refs).
- `CanonicalEvent` → `TelemetryEvent` conversion lives in `CliCommandRouter`
  as a private method. If other consumers need it (e.g. the web dashboard),
  consider extracting to a shared utility in `WotBTreader.Application`.

## Remaining deferred

- **Minimap projection**: Map boundaries are null in installed-game metadata.
  Needs position-extrema computation across many replays.
- **Live HUD smoke test**: Verify transparent window, game tracking, and
  Launch button against a real WoT Blitz installation.
