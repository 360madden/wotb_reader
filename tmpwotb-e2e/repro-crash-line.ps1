# Reproduce the post-correlate 'Count' crash with a short campaign and dump
# the exact failing line + stack (the plain error rendering loses the line).
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'
try {
    & 'C:\work\wotb_reader\scripts\od-048-monitor-correlate-session.ps1' `
        -StageViewpointOnly -MaxReadRounds 8 `
        -ResultPath 'C:\work\wotb_reader\.data\od-049-fresh19-repro8-result.json'
}
catch {
    Write-Host ('CRASH_MSG: ' + $_.Exception.Message)
    Write-Host ('CRASH_LINE: ' + $_.InvocationInfo.ScriptLineNumber + ' in ' + $_.InvocationInfo.ScriptName)
    Write-Host '--- STACK ---'
    Write-Host $_.ScriptStackTrace
    exit 1
}
Write-Host 'NO_CRASH'
exit 0
