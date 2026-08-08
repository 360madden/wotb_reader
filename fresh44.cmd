@echo off
setlocal
REM ============================================================
REM  fresh44.cmd - FRESH44 cross-battle live round (one command).
REM  Runs the od-049 campaign on the SECOND independent 11.19.0
REM  replay (savanna, 2026-08-02; OD-RECOVERY-058) to prove the
REM  FRESH43 correlate match repeats cross-battle (BLK-0019).
REM
REM  Usage:
REM    fresh44.cmd                      # full round: check -> launch -> report
REM    fresh44.cmd -CheckOnly           # preflight only (never launches the game)
REM    fresh44.cmd -KeepGame            # keep the game window after the round
REM
REM  Exit: 0 checks+round complete / 1 replay missing or hash
REM  mismatch / 2 preflight failure / 3 driver failed / 4 other.
REM  See docs/operations/handoffs/2026-08-07-fresh43-game-code-fill-site-hit.md
REM  (OD-RECOVERY-058 amendment) for the second-replay evidence.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"

REM PowerShell Core (pwsh) preferred, fall back to Windows PowerShell
where pwsh >nul 2>&1
if %ERRORLEVEL% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File ./scripts/invoke-fresh44-crossbattle.ps1 %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/invoke-fresh44-crossbattle.ps1 %*
)
exit /b %ERRORLEVEL%
