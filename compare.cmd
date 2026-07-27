@echo off
REM ============================================================
REM  compare.cmd — List or inspect comparison runs.
REM
REM  Usage:   compare.cmd list
REM           compare.cmd inspect <comparison-run-id>
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
call "%~dp0treader.cmd" compare %*
exit /b %ERRORLEVEL%
