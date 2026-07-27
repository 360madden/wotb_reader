@echo off
REM ============================================================
REM  validate.cmd — Full validation: restore, format, build,
REM  tests, vulnerability audit, and repository scan.
REM  Run from any directory; paths are relative to this script.
REM ============================================================
cd /d "%~dp0"
pwsh ./scripts/validate.ps1 -AuditPackages
exit /b %ERRORLEVEL%
