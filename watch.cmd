@echo off
REM ============================================================
REM  watch.cmd — Watch a directory for new .wotbreplay files
REM  and auto-import them. Press Ctrl+C to stop.
REM
REM  Usage:   watch.cmd <directory>
REM  Example: watch.cmd C:\replays
REM
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
if "%~1"=="" (
    echo Usage: watch.cmd ^<directory^>
    echo Example: watch.cmd C:\replays
    exit /b 1
)

cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    exit /b 1
)

"%CLI%" watch "%~1" --data-root "%~dp0.data"
exit /b %ERRORLEVEL%
