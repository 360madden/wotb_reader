@echo off
REM ============================================================
REM  sessions.cmd — List decoded battle sessions.
REM
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    exit /b 1
)

"%CLI%" sessions --json --data-root "%~dp0.data"
exit /b %ERRORLEVEL%
