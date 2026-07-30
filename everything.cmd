@echo off
setlocal
REM ============================================================
REM  everything.cmd — Import a replay (optional), start the web
REM  host, then launch the HUD overlay.
REM
REM  STARTUP SEQUENCE (automatic):
REM   1. Import a .wotbreplay file (if provided as argument)
REM   2. Start the web host (REST API + Blazor dashboard + SignalR)
REM   3. Launch the HUD overlay
REM
REM  Usage:
REM    everything.cmd                              (serve + overlay only)
REM    everything.cmd path\to\replay.wotbreplay    (import + serve + overlay)
REM    everything.cmd 4000                         (custom port, serve + overlay)
REM    everything.cmd path\to\replay.wotbreplay 4000   (custom port, all steps)
REM
REM  After launch, open http://127.0.0.1:9182 in your browser.
REM  The HUD overlay auto-discovers the web host and loads sessions.
REM
REM  Run from any directory; paths are relative to this script.
REM  See docs/operations/cmd-wrapper-gotchas.md for failure modes.
REM ============================================================
cd /d "%~dp0"

REM ── Parse arguments ────────────────────────────────────────

set WEB_PORT=9182
set REPLAY_FILE=

:parse_args
if "%~1"=="" goto args_done

REM Check if this argument looks like a .wotbreplay file
set ARG=%~1
if /i "%ARG:~-12%"==".wotbreplay" (
    set REPLAY_FILE=%ARG%
) else (
    REM Treat as port number
    set WEB_PORT=%ARG%
)
shift
goto parse_args
:args_done

REM ── Step 1: Import a replay (optional) ─────────────────────

if not "%REPLAY_FILE%"=="" (
    echo.
    echo === Step 1: Importing replay ===
    echo   File: %REPLAY_FILE%
    call "%~dp0import.cmd" "%REPLAY_FILE%"
    if %ERRORLEVEL% neq 0 (
        echo WARNING: Import exited with code %ERRORLEVEL%.
        echo   Continuing with serve + overlay anyway.
    )
) else (
    echo.
    echo === Skipping import (no .wotbreplay file given) ===
    echo   To import later, use: import.cmd path\to\replay.wotbreplay
)

REM ── Step 2: Start the web host in a new window ─────────────

echo.
echo === Step 2: Starting web host on port %WEB_PORT% ===
start "WotB Treader - Web Host" cmd /c "set WEB_PORT=%WEB_PORT% && call serve.cmd"

REM Give the host time to publish and start listening
echo Waiting ~10s for host to publish and start...
ping -n 11 127.0.0.1 >nul 2>&1

REM ── Step 3: Launch the overlay in a new window ─────────────

echo.
echo === Step 3: Launching HUD overlay ===
start "WotB Treader - HUD" cmd /c "call overlay.cmd"

echo.
echo === Everything launched ===
echo     Web host: http://127.0.0.1:%WEB_PORT%
echo     HUD:      overlay window (semi-transparent position plot)
echo.
echo To play a replay:
echo   - Import via CLI:    import.cmd path\to\replay.wotbreplay
echo   - Or drag .wotbreplay files onto the overlay window
echo.
echo Close the web host window (Ctrl+C) to stop.
echo.
pause
exit /b 0
