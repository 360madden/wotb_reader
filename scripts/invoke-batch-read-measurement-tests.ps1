# Gate entry point for the Item-7 batch read-pass measurement Pester tests.

$result = Invoke-Pester `
    -Script (Join-Path $PSScriptRoot 'batch-read-measurement.Tests.ps1') `
    -PassThru

if ($null -eq $result -or $result.FailedCount -gt 0) {
    exit 1
}

exit 0
