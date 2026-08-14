# Gate entry point for the Sol-only Codex agent-policy Pester tests.

$result = Invoke-Pester `
    -Script (Join-Path $PSScriptRoot 'codex-agent-policy.Tests.ps1') `
    -PassThru

if ($null -eq $result -or $result.FailedCount -gt 0) {
    exit 1
}

exit 0
