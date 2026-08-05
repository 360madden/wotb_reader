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

$ResultPath = 'C:\family-result.json'
$AutoTraceSeconds = 25
$AutoTraceResultPath = 'C:\autotrace.json'

Write-Host 'array-splat:'
$wtArgs = @(
    '-FamilyFile', $ResultPath,
    '-AutoWriteTrace',
    '-TraceSeconds', [string]$AutoTraceSeconds,
    '-ResultPath', $AutoTraceResultPath
)
Test-Binding @wtArgs

Write-Host 'array-splat as string literals:'
$wtArgs2 = @('-FamilyFile', 'C:\family-result.json', '-AutoWriteTrace', '-TraceSeconds', '25', '-ResultPath', 'C:\autotrace.json')
Test-Binding @wtArgs2

Write-Host 'direct:'
Test-Binding -FamilyFile $ResultPath -AutoWriteTrace -TraceSeconds $AutoTraceSeconds -ResultPath $AutoTraceResultPath
