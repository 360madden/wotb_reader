@echo off
setlocal
REM ============================================================
REM  overlay.cmd — Launch the WotB Treader HUD overlay window.
REM  The overlay discovers the web host automatically via
REM  the rendezvous file, so serve.cmd must already be running.
REM
REM  STARTUP SEQUENCE: 1) import replays  2) serve  3) overlay
REM  Or just run everything.cmd to launch it all at once.
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
set OVERLAY=src\WotBTreader.Overlay\bin\Release\net10.0-windows\WotBTreader.Overlay.exe

if not exist "%OVERLAY%" (
    echo Overlay not built. Run build.cmd first.
    exit /b 1
)

REM Pass the data root so the overlay finds the web host's rendezvous record
set WOTBTREADER_DATA_ROOT=%~dp0.data

start "" "%OVERLAY%"
exit /b %ERRORLEVEL%
