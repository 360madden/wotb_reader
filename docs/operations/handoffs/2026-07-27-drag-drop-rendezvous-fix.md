# Handoff: Drag-and-drop fix, Blitz replay folder, rendezvous path, HUD groundwork

**Date:** 2026-07-27
**Status:** In progress — HUD rework started but reverted

## Completed changes

### 1. `!` in drag-and-drop paths (import.cmd)
Fixed a bug where `enabledelayedexpansion` would eat `!` characters from drag-and-drop file paths, causing `ERROR_FILE_NOT_FOUND`. Wrapped the echo + CLI invocation in `setlocal disabledelayedexpansion`/`endlocal` in the `:import_files` section. ERRORLEVEL is preserved across `endlocal` in cmd.exe.

### 2. Blitz replay folder auto-discovery (import.cmd)
Interactive picker now checks `%LOCALAPPDATA%\wotblitz\DAVAProject\replays` (the actual game replay directory) before falling back to Documents.

### 3. Rendezvous path mismatch (RendezvousLocator.cs + overlay.cmd)
The overlay read the rendezvous file from `%LOCALAPPDATA%/WotBTreader/rendezvous/` but the web host wrote to `.data/rendezvous/`. Added `WOTBTREADER_DATA_ROOT` environment variable support — `RendezvousLocator` checks this env var and uses `{root}/rendezvous/web.json` if set. `overlay.cmd` and `everything.cmd` now set it to `%~dp0.data`.

### 4. LaunchGameCommand groundwork (MainViewModel.cs)
Added optional `Func<SessionRow?, bool>? launchGame` delegate parameter and `LaunchGameCommand` to the ViewModel. The command is wired up but no-op until a UI launcher is connected. Backward-compatible — existing callers work unchanged.

## Reverted changes (HUD redesign)
`MainWindow.xaml` and `MainWindow.xaml.cs` were partially rewritten for a transparent/borderless/topmost HUD with P/Invoke game window tracking and game launching. Reverted because it didn't compile cleanly (CS8600 nullability + CA2101 P/Invoke marshaling issues). The code is available in git history if needed.

## What still needs to be done

- **HUD redesign**: MainWindow needs to become transparent, borderless, topmost. The reverted code in git history has P/Invoke for `FindWindow`/`GetWindowRect`/`SetWindowPos` and a `LaunchGameWithSelectedReplay` method. Needs nullability fixes and a proper `FindReplaySource` that actually matches session to replay file.
- **Game launching**: The `GameLauncherService` pattern should be a separate injectable service. Hardcoded game paths (`C:\Games\World_of_Tanks_Blitz\wotblitz.exe`) should use `GameIntegration.GameInstallationDiscovery` instead.
- **Dashboard tab**: The original dashboard WebView2 tab needs to be preserved in the HUD redesign.

## Test status

- 73/73 tests pass (CLI, Overlay, Storage.Sqlite)
- Full solution builds with 0 errors

## Imported data

10 real replays imported into `.data/` — all decoded successfully (14 participants each, maps: Copperfield, Yamato Harbor, Falls Creek, Desert Sands, Normandy, Mines). Web host verified at http://127.0.0.1:9182.
