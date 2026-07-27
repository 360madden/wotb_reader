@echo off
REM ============================================================
REM  overlay.cmd — Launch the WotB Treader overlay window.
REM  The overlay discovers the web host automatically via
REM  the rendezvous file, so serve.cmd must already be running.
REM
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
set OVERLAY=src\WotBTreader.Overlay\bin\Release\net10.0-windows\WotBTreader.Overlay.exe

if not exist "%OVERLAY%" (
    echo Overlay not built. Run build.cmd first.
    exit /b 1
)

start "" "%OVERLAY%"
exit /b %ERRORLEVEL%
