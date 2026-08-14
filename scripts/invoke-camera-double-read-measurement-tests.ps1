# Gate entry point for the Item-7 Branch-B camera double-read Pester tests.

$result = Invoke-Pester `
    -Script (Join-Path $PSScriptRoot 'camera-double-read-measurement.Tests.ps1') `
    -PassThru

if ($null -eq $result -or $result.FailedCount -gt 0) {
    exit 1
}

exit 0
