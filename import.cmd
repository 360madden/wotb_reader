@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  import.cmd — Import one or more .wotbreplay files into storage.
REM
REM  No arguments:  scans for .wotbreplay files in your replays
REM                 folder and shows a numbered picker.
REM
REM  Drag-and-drop:  drag .wotbreplay files onto this script in
REM                  Explorer. You can drop multiple files at once.
REM
REM  Command line:   import.cmd <path-to-replay> [more-files...]
REM
REM  Examples:
REM    import.cmd                                    (interactive picker)
REM    import.cmd C:\replays\battle.wotbreplay
REM    drag multiple .wotbreplay files from Explorer onto this script
REM
REM  Set REPLAYS_DIR to customise the scanned folder:
REM    set REPLAYS_DIR=C:\Games\WoTB\replays
REM
REM  Data is stored under .data\ in the repo root.
REM  Run from any directory; paths are relative to this script.
REM ============================================================

cd /d "%~dp0"
set CLI=src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe

if not exist "%CLI%" (
    echo CLI not built. Run build.cmd first.
    pause
    exit /b 1
)

REM ── If arguments were passed, import them directly ──────────
if not "%~1"=="" goto import_files

REM ── No arguments: interactive file picker ───────────────────

REM Pick the scan directory: REPLAYS_DIR env var, then Documents, then current dir
set SCAN_DIR=%REPLAYS_DIR%
if "%SCAN_DIR%"=="" set SCAN_DIR=%USERPROFILE%\Documents

echo.
echo === Scanning for .wotbreplay files in: !SCAN_DIR! ===
echo.

REM Count and list files
set N=0
for %%f in ("!SCAN_DIR!\*.wotbreplay") do (
    set /a N+=1
    set "FILE_!N!=%%f"
    echo     [!N!]  %%~nxf   (%%~zf bytes, %%~tf^)
)

if !N! EQU 0 (
    echo No .wotbreplay files found.
    echo.
    echo Set REPLAYS_DIR to your replays folder if they're elsewhere:
    echo     set REPLAYS_DIR=C:\Games\WoTB\replays
    echo.
    pause
    exit /b 1
)

echo.
echo     [a]  Import ALL !N! files
echo     [q]  Quit
echo.
set CHOICE=
set /p CHOICE="Pick a number (or a/q): "

if "!CHOICE!"=="" (
    echo Please enter a number.
    pause
    exit /b 1
)

if /i "!CHOICE!"=="q" (
    echo Quit.
    exit /b 0
)

if /i "!CHOICE!"=="a" (
    echo Importing all !N! files...
    set COUNT=0
    set FAILED=0
    for /l %%i in (1,1,!N!) do (
        echo.
        echo === Importing [%%i/!N!]: !FILE_%%i! ===
        "%CLI%" import "!FILE_%%i!" --json --data-root "%~dp0.data"
        if !ERRORLEVEL! neq 0 (
            set /a FAILED+=1
        ) else (
            set /a COUNT+=1
        )
    )
    echo.
    echo === Done: !COUNT! imported, !FAILED! failed ===
    pause
    exit /b !FAILED!
)

REM Validate numeric choice
set "NUM=!CHOICE!"
for /f "delims=0123456789" %%v in ("!NUM!") do set "NUM="
if "!NUM!"=="" (
    echo Invalid choice: !CHOICE!
    pause
    exit /b 1
)
if !NUM! LSS 1 (
    echo Invalid choice: !CHOICE!
    pause
    exit /b 1
)
if !NUM! GTR !N! (
    echo Invalid choice: !CHOICE!
    pause
    exit /b 1
)

REM Import the single selected file
echo.
echo === Importing: !FILE_%NUM%! ===
"%CLI%" import "!FILE_%NUM%!" --json --data-root "%~dp0.data"
set RESULT=!ERRORLEVEL!
echo.
if !RESULT! EQU 0 (
    echo === Import succeeded ===
) else (
    echo === Import failed ===
)
pause
exit /b !RESULT!

REM ── Direct import from command-line arguments ───────────────
:import_files
set COUNT=0
set FAILED=0

:loop
if "%~1"=="" goto done
echo.
echo === Importing: %~nx1 ===
"%CLI%" import "%~1" --json --data-root "%~dp0.data"
if !ERRORLEVEL! neq 0 (
    set /a FAILED+=1
) else (
    set /a COUNT+=1
)
shift
goto loop

:done
echo.
echo === Done: !COUNT! imported, !FAILED! failed ===
pause
exit /b !FAILED!
