@echo off
REM ============================================================
REM  doctor.cmd — Run health checks to verify the environment.
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
call "%~dp0treader.cmd" doctor --json
exit /b %ERRORLEVEL%
