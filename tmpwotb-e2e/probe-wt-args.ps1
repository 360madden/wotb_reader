[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh-result.json'
$AutoTraceSeconds = 25
$AutoTraceResultPath = 'C:\work\wotb_reader\.data\od-048-autotrace-test.json'

$wtScript = 'C:\work\wotb_reader\scripts\x64dbg-write-trace.ps1'
Write-Host ('array-form:')
$wtArgs = @(
    '-FamilyFile', $ResultPath,
    '-AutoWriteTrace',
    '-TraceSeconds', [string]$AutoTraceSeconds,
    '-ResultPath', $AutoTraceResultPath
)
Write-Host ('  element count=' + $wtArgs.Count)
try {
    & $wtScript @wtArgs -DryRun
    Write-Host ('  array-form OK exit=' + $LASTEXITCODE)
}
catch {
    Write-Host ('  array-form THREW: ' + $_.Exception.Message)
}

Write-Host ('direct-form:')
try {
    & $wtScript -FamilyFile $ResultPath -AutoWriteTrace -TraceSeconds $AutoTraceSeconds -ResultPath $AutoTraceResultPath -DryRun
    Write-Host ('  direct-form OK exit=' + $LASTEXITCODE)
}
catch {
    Write-Host ('  direct-form THREW: ' + $_.Exception.Message)
}
