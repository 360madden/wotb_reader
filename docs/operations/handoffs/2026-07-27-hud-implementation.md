# Handoff: Transparent HUD implementation

**Date:** 2026-07-27
**Status:** Implemented — live smoke test pending

## Completed

### Transparent HUD window
`MainWindow.xaml` + `MainWindow.xaml.cs` redesigned as a transparent, borderless,
always-on-top HUD that sits over the WoT Blitz game during replay playback.

**XAML properties:**
- `WindowStyle="None"` — no title bar or chrome
- `AllowsTransparency="True"` — see-through background
- `Background="Transparent"` — game visible underneath
- `Topmost="True"` — stays above the game window
- `MouseLeftButtonDown` — drag-to-move (no title bar to grab)

**Layout:**
- `PositionPlot` spans the full window (transparent canvas, team-coloured dots)
- Floating semi-transparent dark panel (`#CC111111`) on the right side containing:
  - 🚀 Launch button — launches wotblitz.exe with the selected replay
  - ↻ Refresh button — reloads session list from web host
  - 🌐 Dashboard button — opens http://127.0.0.1:9182 in a browser
  - Session list with map name, time, participant/position counts
  - ✕ Close button

### Game window tracking
P/Invoke `FindWindowW`/`GetWindowRect`/`SetWindowPos` with a 500ms timer.
The overlay tracks the WoT Blitz game window and repositions itself to match.

### Game launching
`LaunchGameWithSelectedReplay` method:
1. Finds the most recently modified `.wotbreplay` in `%LOCALAPPDATA%\wotblitz\DAVAProject\replays\`
2. Launches `wotblitz.exe` with the replay file as an argument
3. Starts the window tracking timer

### WebView2 removed
Due to the documented incompatibility between `AllowsTransparency=True` and
WebView2, the embedded dashboard tab was replaced with a button that opens
the dashboard in a browser via `Process.Start`.

## Build & test status

- Overlay: 0 errors, 0 warnings, 41/41 tests pass
- Full solution: needs verification

## Live smoke test (manual)

```cmd
# Terminal 1: start web host
serve.cmd

# Terminal 2: launch HUD (or double-click in Explorer)
overlay.cmd
```

Expected: transparent window appears, discovers web host via rendezvous,
loads session list, position dots render. Launch button starts wotblitz.exe.

## Git

Commits in this session:
1. `0e81b0c` — fix: drag-and-drop `!` paths, rendezvous mismatch, Blitz folder
2. `c0294ca` — docs: document HUD design intent across 4 files
3. `0a37d9f` — docs: WebView2+transparency incompatibility, coordinate gaps
4. `e5f7b7f` — feat(overlay): transparent borderless topmost HUD with game tracking and launching
