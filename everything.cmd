@echo off
REM ============================================================
REM  everything.cmd — One-command launch of the full stack.
REM
REM  Starts the web host in one window, waits for it to be ready,
REM  then launches the HUD overlay in another window.
REM
REM  STARTUP SEQUENCE: This automates steps 2 and 3.
REM  Import replays first with import.cmd or watch.cmd (step 1).
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
start "WotB Treader - Web Host" cmd /c "set WEB_PORT=%WEB_PORT% && cd /d %~dp0 && call serve.cmd"

REM Give the host a few seconds to publish and start listening
echo Waiting ~10s for host to publish and start...
ping -n 11 127.0.0.1 >nul 2>&1

REM ── Step 3: Launch the overlay in a new window ──────────────
echo.
echo === Launching HUD overlay ===
start "WotB Treader - HUD" cmd /c "cd /d %~dp0 && call overlay.cmd"

echo.
echo === Both windows launched ===
echo     Web host: http://127.0.0.1:%WEB_PORT%
echo     HUD:      overlay window (position plot + dashboard)
echo.
echo Close the web host window (Ctrl+C) to stop, or close this window.
echo.
pause
exit /b 0
