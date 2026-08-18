[CmdletBinding()]
param(
    [switch] $AuditPackages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\WotBTreader.sln'

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [Parameter(Mandatory)]
        [string] $Description
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

Invoke-CheckedNative -FilePath dotnet -ArgumentList @('restore', $solution, '--locked-mode') -Description 'Locked restore'
Invoke-CheckedNative -FilePath dotnet -ArgumentList @('format', $solution, '--verify-no-changes', '--no-restore') -Description 'Format verification'
Invoke-CheckedNative -FilePath dotnet -ArgumentList @('build', $solution, '-c', 'Release', '--no-restore') -Description 'Release build'
Invoke-CheckedNative -FilePath dotnet -ArgumentList @('test', $solution, '-c', 'Release', '--no-build') -Description 'Test suite'

if ($AuditPackages) {
    Invoke-CheckedNative -FilePath dotnet -ArgumentList @(
        'list',
        $solution,
        'package',
        '--vulnerable',
        '--include-transitive'
    ) -Description 'Package vulnerability audit'
}

& (Join-Path $PSScriptRoot 'scan-repository.ps1')

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'codex-agent-config-check.ps1')
) -Description 'Codex allowed-models agent configuration gate'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-codex-agent-policy-tests.ps1')
) -Description 'Codex allowed-models agent-policy Pester smoke tests'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'install-psscriptanalyzer.ps1')
) -Description 'PSScriptAnalyzer install'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-scriptanalyzer.ps1')
) -Description 'Script hygiene gate (PSScriptAnalyzer)'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-od-replay-completion-tests.ps1')
) -Description 'Completion-marker Pester smoke tests'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-od-replay-selection-tests.ps1')
) -Description 'Replay-selection + staging-refusal Pester smoke tests'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-camera-double-read-measurement-tests.ps1')
) -Description 'Camera double-read measurement Pester smoke tests'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-batch-read-measurement-tests.ps1')
) -Description 'Batch read-pass + witness support Pester smoke tests'

Invoke-CheckedNative -FilePath powershell -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    (Join-Path $PSScriptRoot 'invoke-build-drift-triage-tests.ps1')
) -Description 'Build-drift triage Pester smoke tests'

Invoke-CheckedNative -FilePath python -ArgumentList @(
    (Join-Path $PSScriptRoot 'python\offline_check.py'),
    '--check-fresh'
) -Description 'Offline pack link + file-tree freshness check'

Invoke-CheckedNative -FilePath python -ArgumentList @(
    (Join-Path $PSScriptRoot 'python\offset_check.py'),
    '--check-schema'
) -Description 'Offset-table schema + chains validation'

Invoke-CheckedNative -FilePath python -ArgumentList @(
    '-c',
    "import json,sys; json.load(open(sys.argv[1], encoding='utf-8'))",
    (Join-Path $PSScriptRoot '..\tools\external\tools.lock.json')
) -Description 'Tool registry JSON validity'

# Advisory (non-gating): System Informer launcher presence. The repo gate
# cannot require the tool -- CI has no local install -- but the operator
# sees the advisory line in doctor/validate output when a session would run
# without the supporting tool.
& (Join-Path $PSScriptRoot 'system-informer-check.ps1')
Write-Host 'Tool presence check complete (System Informer advisory only).'
