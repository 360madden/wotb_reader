[CmdletBinding()]
param(
    [switch] $AuditPackages
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\WotBTreader.sln'

dotnet restore $solution --locked-mode
dotnet format $solution --verify-no-changes --no-restore
dotnet build $solution -c Release --no-restore
dotnet test $solution -c Release --no-build

if ($AuditPackages) {
    dotnet list $solution package --vulnerable --include-transitive
}

& (Join-Path $PSScriptRoot 'scan-repository.ps1')
