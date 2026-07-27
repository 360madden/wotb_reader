@echo off
REM ============================================================
REM  test.cmd — Run all tests (assumes already built).
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
dotnet test WotBTreader.sln -c Release --no-build
exit /b %ERRORLEVEL%
