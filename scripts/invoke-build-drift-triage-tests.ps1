# Gate entry point for the RECOVERY build-drift triage Pester tests.

$result = Invoke-Pester `
    -Script (Join-Path $PSScriptRoot '..\RECOVERY\invoke-build-drift-triage.Tests.ps1') `
    -PassThru

if ($null -eq $result -or $result.FailedCount -gt 0) {
    exit 1
}

exit 0
