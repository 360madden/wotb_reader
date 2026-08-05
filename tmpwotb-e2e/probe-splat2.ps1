[CmdletBinding()]
param()
Set-StrictMode -Version Latest

function Test-Binding {
    param(
        [string]$FamilyFile = '',
        [switch]$AutoWriteTrace,
        [int]$TraceSeconds = 120,
        [string]$ResultPath = ''
    )
    Write-Host ('  bound FamilyFile=' + $FamilyFile + ' AutoWriteTrace=' + $AutoWriteTrace.IsPresent + ' TraceSeconds=' + $TraceSeconds + ' ResultPath=' + $ResultPath)
}

Write-Host 'array-splat as string literals:'
$wtArgs2 = @('-FamilyFile', 'C:\family-result.json', '-AutoWriteTrace', '-TraceSeconds', '25', '-ResultPath', 'C:\autotrace.json')
Test-Binding @wtArgs2

Write-Host 'hashtable-splat:'
$h = @{
    FamilyFile    = 'C:\family-result.json'
    AutoWriteTrace = $true
    TraceSeconds  = 25
    ResultPath    = 'C:\autotrace.json'
}
Test-Binding @h
