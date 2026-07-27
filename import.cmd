@echo off
REM ============================================================
REM  import.cmd — Import one or more .wotbreplay files into storage.
REM
REM  Drag-and-drop:  drag .wotbreplay files onto this script in
REM                  Explorer. You can drop multiple files at once.
REM
REM  Command line:   import.cmd <path-to-replay> [more-files...]
REM
REM  Examples:
REM    import.cmd C:\replays\battle.wotbreplay
REM    drag multiple .wotbreplay files from Explorer onto this script
REM
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
if "%~1"=="" (
    echo Usage: import.cmd ^<path-to-replay^> [more-files...]
    echo.
    echo You can drag-and-drop .wotbreplay files onto this script
    echo in Explorer to import them.
    echo.
    pause
    exit /b 1
)

cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    pause
    exit /b 1
)

set COUNT=0
set FAILED=0

:loop
if "%~1"=="" goto done
echo.
echo === Importing: %~nx1 ===
"%CLI%" import "%~1" --json --data-root "%~dp0.data"
if %ERRORLEVEL% neq 0 (
    set /a FAILED+=1
) else (
    set /a COUNT+=1
)
shift
goto loop

:done
echo.
echo === Done: %COUNT% imported, %FAILED% failed ===
pause
exit /b %FAILED%
