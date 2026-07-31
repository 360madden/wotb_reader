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

Invoke-CheckedNative dotnet @('restore', $solution, '--locked-mode') 'Locked restore'
Invoke-CheckedNative dotnet @('format', $solution, '--verify-no-changes', '--no-restore') 'Format verification'
Invoke-CheckedNative dotnet @('build', $solution, '-c', 'Release', '--no-restore') 'Release build'
Invoke-CheckedNative dotnet @('test', $solution, '-c', 'Release', '--no-build') 'Test suite'

if ($AuditPackages) {
    Invoke-CheckedNative dotnet @(
        'list',
        $solution,
        'package',
        '--vulnerable',
        '--include-transitive'
    ) 'Package vulnerability audit'
}

& (Join-Path $PSScriptRoot 'scan-repository.ps1')

Invoke-CheckedNative python @(
    (Join-Path $PSScriptRoot 'python\offline_check.py'),
    '--check-fresh'
) 'Offline pack link + file-tree freshness check'

Invoke-CheckedNative python @(
    '-c',
    "import json,sys; json.load(open(sys.argv[1], encoding='utf-8'))",
    (Join-Path $PSScriptRoot '..\tools\external\tools.lock.json')
) 'Tool registry JSON validity'
