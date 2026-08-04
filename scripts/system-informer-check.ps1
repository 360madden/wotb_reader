#Requires -Version 5.1
<#
.SYNOPSIS
  Resolve and report the installed System Informer launcher (advisory).

.DESCRIPTION
  System Informer is an approved operator-side supporting tool for offset
  discovery sessions (process memory map view, Suspend/Resume freeze,
  module-base verification, handle explorer). It is NOT a hard dependency:
  the live pipeline (scanner, x64dbg, GameIntegration suspend) covers the
  same capabilities, so a missing install never fails a session -- the OD
  session drivers call this script purely to warn the operator.

  Resolution order: tools/external/tools.lock.json installed_path (if it
  differs from the probe roots), then the standard install roots
  (C:\Program Files\SystemInformer, C:\tools\SystemInformer, C:\work\tools\SystemInformer).
  Prints one line: system_informer: present|missing path=<path> version=<v>
  and exits 0 when present, 1 when missing (never used as a gate).

.EXITCODES
  0  Launcher resolved (present)
  1  Not found (advisory only)
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

$candidates = @(
    # Registered installed_path (authoritative, from tools.lock.json).
    'C:\Program Files\SystemInformer\SystemInformer.exe',
    # Pre-registration / manual roots.
    'C:\tools\SystemInformer\SystemInformer.exe',
    'C:\work\tools\SystemInformer\SystemInformer.exe'
)

# Prefer the registry's installed_path when it points at a live launcher.
# Registry read is best-effort: an unreadable/unmatching registry must not
# fail the check -- the probe roots below still resolve the launcher.
$registryPath = Join-Path $RepoRoot 'tools\external\tools.lock.json'
$registry = Get-Content -LiteralPath $registryPath -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -ErrorAction SilentlyContinue
$entry = $null
if ($registry -and $registry.tools) {
    $entry = $registry.tools | Where-Object { $_.name -eq 'System Informer' } | Select-Object -First 1
}
if ($entry -and $entry.installed_path) {
    $registryCandidate = Join-Path ([string]$entry.installed_path) 'SystemInformer.exe'
    if (Test-Path -LiteralPath $registryCandidate) {
        $candidates = @($registryCandidate) + $candidates
    }
}

$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    Write-Host 'system_informer: missing (advisory -- install via winget: winget install --id=WinsiderSS.SystemInformer -e)'
    exit 1
}

# Version read is best-effort; a version-less launcher is still present.
$version = (Get-Item -LiteralPath $exe -ErrorAction SilentlyContinue).VersionInfo.ProductVersion
if (-not $version) { $version = '' }

Write-Host ("system_informer: present path=" + $exe + " version=" + $version)
exit 0
