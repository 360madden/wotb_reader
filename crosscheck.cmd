@echo off
setlocal
REM ============================================================
REM  crosscheck.cmd — Operator-run replay-decode cross-validation.
REM  Runs the C# decoder and the independent Rust oracle
REM  (wotbreplay-inspector) on the same .wotbreplay and compares
REM  battle timestamp, participants, and packet clocks.
REM
REM  Usage:
REM    crosscheck.cmd                      # newest .data\launch replay
REM    crosscheck.cmd -Replay <file>       # a specific replay
REM    crosscheck.cmd -GoldenVector        # validate oracle vs parser fixtures
REM
REM  Exit: 0 agree / 1 disagree / 2 oracle missing / 3 decode failed.
REM  See docs/operations/replay-crosscheck.md for the full procedure.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"

REM PowerShell Core (pwsh) preferred, fall back to Windows PowerShell
where pwsh >nul 2>&1
if %ERRORLEVEL% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File ./scripts/invoke-replay-crosscheck.ps1 %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/invoke-replay-crosscheck.ps1 %*
)
exit /b %ERRORLEVEL%
