@echo off
REM ============================================================
REM  export.cmd — Export session events or positions as JSON.
REM
REM  Usage:   export.cmd sessions <battle-session-id>
REM           export.cmd positions <battle-session-id>
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
call "%~dp0treader.cmd" export %*
exit /b %ERRORLEVEL%
