@echo off
REM ============================================================
REM  treader.cmd — General passthrough to the WotB Treader CLI.
REM  Forwards all arguments directly. Use for doctor, compare,
REM  export, inspect, reprocess, and any future commands.
REM
REM  Usage:   treader.cmd <command> [args...]
REM  Examples:
REM    treader.cmd doctor --json
REM    treader.cmd compare list
REM    treader.cmd compare inspect <id>
REM    treader.cmd export positions <battle-session-id>
REM    treader.cmd inspect <decode-run-id>
REM    treader.cmd reprocess <artifact-id>
REM
REM  Also see: import.cmd, watch.cmd, sessions.cmd (shortcuts)
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    exit /b 1
)

"%CLI%" --data-root "%~dp0.data" %*
exit /b %ERRORLEVEL%
