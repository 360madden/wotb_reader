@echo off
REM ============================================================
REM  build.cmd — Build the WotB Treader solution (Release)
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
dotnet build WotBTreader.sln -c Release
exit /b %ERRORLEVEL%
