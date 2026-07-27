@echo off
REM ============================================================
REM  serve.cmd — Publish and start the web host on loopback.
REM  The dashboard is at http://127.0.0.1:9182
REM  Press Ctrl+C to stop.
REM
REM  STARTUP SEQUENCE: import replays first, then serve, then overlay.
REM  Keep this window running — the overlay needs it.
REM
REM  Data is stored under .data\ in the repo root.
REM  Publish output goes to .build\publish\ (separate from data).
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"

REM Default paths
if not defined WEB_PORT set WEB_PORT=9182
set DATA_ROOT=%~dp0.data
set PUBLISH_DIR=%~dp0.build\publish

echo === Publishing web host ===
dotnet publish src/WotBTreader.Host.Web -c Release -o "%PUBLISH_DIR%" --no-restore
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

echo.
echo === Starting web host on http://127.0.0.1:%WEB_PORT% ===
echo     Dashboard: http://127.0.0.1:%WEB_PORT%
echo     Data root:  %DATA_ROOT%
echo     Press Ctrl+C to stop.
echo.

set Web__Port=%WEB_PORT%
set Paths__ApplicationDataRoot=%DATA_ROOT%
cd /d "%PUBLISH_DIR%"
WotBTreader.Host.Web.exe
exit /b %ERRORLEVEL%
