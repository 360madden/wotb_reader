@echo off
setlocal
REM ============================================================
REM  validate.cmd — Full validation: restore, format, build,
REM  tests, vulnerability audit, and repository scan.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"

REM PowerShell Core (pwsh) preferred, fall back to Windows PowerShell
where pwsh >nul 2>&1
if %ERRORLEVEL% equ 0 (
    pwsh ./scripts/validate.ps1 -AuditPackages
) else (
    powershell ./scripts/validate.ps1 -AuditPackages
)
exit /b %ERRORLEVEL%
