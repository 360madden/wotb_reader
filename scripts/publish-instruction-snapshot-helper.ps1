#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the instruction-first x86 helper with an exact Host.Web trust root.

.DESCRIPTION
  Builds Host.Web, embeds the Release apphost and managed-assembly hashes in a
  separate helper binary, and writes a local identity manifest only after the
  controlled publish succeeds. No game, replay, or private artifact is read.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-OwnerOnlyFileAcl([string]$Path) {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    # icacls instead of .NET Set-Acl: Set-Acl with a fresh security descriptor
    # throws PrivilegeNotHeldException (SeSecurityPrivilege) when the target
    # already has a protected owner-only ACL (same root cause as BLK-0026 in the
    # launch launcher). /inheritance:r + /grant:r yields exactly the single
    # owner FullControl rule; owner is unchanged (current user).
    & icacls $Path /inheritance:r /grant:r ("*" + $owner + ':F') | Out-Null
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$hostProject = Join-Path $RepoRoot 'src\WotBTreader.Host.Web\WotBTreader.Host.Web.csproj'
$hostOutput = Join-Path $RepoRoot 'src\WotBTreader.Host.Web\bin\Release\net10.0'
$hostExe = Join-Path $hostOutput 'WotBTreader.Host.Web.exe'
$hostDll = Join-Path $hostOutput 'WotBTreader.Host.Web.dll'
$helperProject = Join-Path $RepoRoot 'tools\InstructionSnapshotHelper\InstructionSnapshotHelper.csproj'
$publishDirectory = Join-Path $RepoRoot '.build\publish\instruction-snapshot-helper'
$helperExe = Join-Path $publishDirectory 'WotBTreader.InstructionSnapshotHelper.exe'
$manifestPath = Join-Path $publishDirectory 'identity.json'

& dotnet build $hostProject -c Release --no-restore 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'instruction_snapshot_helper_publish: FAILED_host_build'
    exit 1
}

$hostExeHash = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash
$hostDllHash = (Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash

& dotnet restore $helperProject -r win-x86 --locked-mode 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'instruction_snapshot_helper_publish: FAILED_restore'
    exit 1
}

& dotnet publish $helperProject -c Release -r win-x86 --self-contained true --no-restore `
    ("-p:ExpectedCoordinatorSha256=" + $hostExeHash) `
    ("-p:ExpectedCoordinatorAssemblySha256=" + $hostDllHash) `
    -o $publishDirectory 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $helperExe)) {
    Write-Host 'instruction_snapshot_helper_publish: FAILED_publish'
    exit 1
}

$manifest = [ordered]@{
    schema = 'wotbtreader.instruction-snapshot-helper.identity.v1'
    helperFile = 'WotBTreader.InstructionSnapshotHelper.exe'
    helperSha256 = (Get-FileHash -LiteralPath $helperExe -Algorithm SHA256).Hash
    coordinatorExeSha256 = $hostExeHash
    coordinatorAssemblySha256 = $hostDllHash
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$temporaryManifest = $manifestPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $temporaryManifest -Encoding UTF8
    Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath -Force
    Set-OwnerOnlyFileAcl -Path $manifestPath
}
finally {
    Remove-Item -LiteralPath $temporaryManifest -Force -ErrorAction SilentlyContinue
}

Write-Host 'instruction_snapshot_helper_publish: OK'
exit 0
