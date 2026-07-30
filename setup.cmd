@echo off
setlocal
REM ============================================================
REM  setup.cmd — First-run setup: check prerequisites, restore,
REM  build, and run tests. Run once after cloning the repo.
REM
REM  Run from any directory; paths are relative to this script.
REM  See docs/operations/cmd-wrapper-gotchas.md for failure modes.
REM ============================================================
cd /d "%~dp0"

echo.
echo === WotB Treader — First-Run Setup ===
echo.

REM ── [1/4] Check prerequisites ──────────────────────────────

echo [1/4] Checking .NET SDK...
dotnet --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: .NET SDK not found. Install .NET 10 SDK from:
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

for /f "tokens=1 delims=." %%v in ('dotnet --version') do (
    if %%v LSS 10 (
        echo WARNING: .NET %%v detected. This project requires .NET 10.0.302+.
        echo   Update: https://dotnet.microsoft.com/download/dotnet/10.0
        echo.
    )
)

REM ── [2/4] Restore packages ─────────────────────────────────

echo [2/4] Restoring packages...
dotnet restore WotBTreader.sln
if %ERRORLEVEL% neq 0 (
    echo ERROR: Package restore failed.
    pause
    exit /b %ERRORLEVEL%
)

REM ── [3/4] Build solution ───────────────────────────────────

echo [3/4] Building solution...
dotnet build WotBTreader.sln -c Release
if %ERRORLEVEL% neq 0 (
    echo ERROR: Build failed.
    pause
    exit /b %ERRORLEVEL%
)

REM ── [4/4] Run tests ────────────────────────────────────────

echo [4/4] Running tests...
dotnet test WotBTreader.sln -c Release --no-build

echo.
echo === Setup complete! ===
echo.
echo Next steps:
echo   1. Import a replay:       import.cmd path\to\replay.wotbreplay
echo   2. Start the dashboard:   serve.cmd
echo   3. Launch the HUD:        overlay.cmd
echo.
echo   Or run everything at once:
echo     everything.cmd path\to\replay.wotbreplay
echo.
echo   Open http://127.0.0.1:9182 in your browser.
echo.
pause
exit /b 0
