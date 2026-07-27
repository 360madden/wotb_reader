@echo off
REM ============================================================
REM  import.cmd — Import a .wotbreplay file into storage.
REM
REM  Usage:   import.cmd <path-to-replay>
REM  Example: import.cmd C:\replays\battle.wotbreplay
REM
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
if "%~1"=="" (
    echo Usage: import.cmd ^<path-to-replay^>
    echo Example: import.cmd C:\replays\battle.wotbreplay
    exit /b 1
)

cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    exit /b 1
)

"%CLI%" import "%~1" --json --data-root "%~dp0.data"
exit /b %ERRORLEVEL%
