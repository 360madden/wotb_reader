@echo off
setlocal
REM ============================================================
REM  everything.cmd — Start the web host and HUD overlay.
REM
REM  Launches the two background services the overlay needs:
REM  1. Web host (serve) — REST API + Blazor dashboard + SignalR
REM  2. HUD overlay — transparent position plot over the game
REM
REM  To actually play a replay, use the overlay's "Pick & Launch"
REM  button or drag a .wotbreplay file onto the overlay window.
REM  Those actions handle import, host startup, AND game launching
REM  for the specific replay you choose — not a random one.
REM
REM  Usage:   everything.cmd
REM           everything.cmd 4000    (custom port)
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"

REM Default port — customisable as first argument
if "%~1"=="" (
    set WEB_PORT=9182
) else (
    set WEB_PORT=%~1
)

REM ── Step 2: Start the web host in a new window ──────────────
echo.
echo === Starting web host on port %WEB_PORT% ===
start "WotB Treader - Web Host" cmd /c "set WEB_PORT=%WEB_PORT% && call serve.cmd"

REM Give the host a few seconds to publish and start listening
echo Waiting ~10s for host to publish and start...
ping -n 11 127.0.0.1 >nul 2>&1

REM ── Step 3: Launch the overlay in a new window ──────────────
echo.
echo === Launching HUD overlay ===
start "WotB Treader - HUD" cmd /c "call overlay.cmd"

echo.
echo === Both windows launched ===
echo     Web host: http://127.0.0.1:%WEB_PORT%
echo     HUD:      overlay window (position plot + dashboard)
echo.
echo To play a replay, click "Pick ^& Launch" in the overlay
echo or drag a .wotbreplay file onto the overlay window.
echo.
echo Close the web host window (Ctrl+C) to stop, or close this window.
echo.
pause
exit /b 0
