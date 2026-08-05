[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$wtScript = 'C:\work\wotb_reader\scripts\x64dbg-write-trace.ps1'
$wtArgs = @{
    FamilyFile     = 'C:\work\wotb_reader\.data\od-049-fresh-result.json'
    AutoWriteTrace = $true
    TraceSeconds   = 25
    ResultPath     = 'C:\work\wotb_reader\.data\od-048-autotrace-test.json'
    DryRun         = $true
}
try {
    & $wtScript @wtArgs
    Write-Host ('HASHTABLE OK exit=' + $LASTEXITCODE)
}
catch {
    Write-Host ('HASHTABLE THREW: ' + $_.Exception.Message)
}
