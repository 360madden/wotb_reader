<#
.SYNOPSIS
    Computes the SHA-256 hash of the installed WoT Blitz executable and
    updates the versioned offset file with the hash and discovery metadata.

.DESCRIPTION
    Finds the wotblitz.exe from the installed game (checks default Lesta/Microsoft
    Store paths, then falls back to a provided path). Outputs the hash and
    can optionally update the corresponding memory-offsets/<version>.json file.

.PARAMETER ExePath
    Path to wotblitz.exe. If omitted, auto-discovers from known install locations.

.PARAMETER UpdateOffsetFile
    If set, updates the offset JSON with the computed hash and discovery timestamp.

.PARAMETER OffsetDir
    Path to the memory-offsets directory. Defaults to repo-relative auto-discovery.

.EXAMPLE
    .\tools\compute-exe-hash.ps1
    # Finds the exe, prints the hash.

.EXAMPLE
    .\tools\compute-exe-hash.ps1 -UpdateOffsetFile
    # Finds the exe, prints the hash, and updates the offset file.
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$UpdateOffsetFile,
    [string]$OffsetDir
)

$ErrorActionPreference = 'Stop'

# -- Path discovery -----------------------------------------------------------

function Find-WotBlitzExe {
    $candidates = @(
        "C:\Games\World_of_Tanks_Blitz\wotblitz.exe",
        "C:\Program Files\World_of_Tanks_Blitz\wotblitz.exe",
        "C:\Program Files (x86)\World_of_Tanks_Blitz\wotblitz.exe",
        "C:\Program Files (x86)\Steam\steamapps\common\World of Tanks Blitz\wotblitz.exe",
        (Join-Path $env:LOCALAPPDATA "World_of_Tanks_Blitz\wotblitz.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $version = (Get-Item $candidate).VersionInfo.ProductVersion
            Write-Host "Found: $candidate (v$version)" -ForegroundColor Green
            return @{ Path = $candidate; Version = $version }
        }
    }

    # Check Microsoft Store packages
    $storeRoot = Join-Path $env:ProgramFiles "WindowsApps"
    if (Test-Path $storeRoot) {
        $packages = Get-ChildItem $storeRoot -Directory -Filter "*WoTBlitz*" -ErrorAction SilentlyContinue
        foreach ($pkg in $packages) {
            $exe = Join-Path $pkg.FullName "wotblitz.exe"
            if (Test-Path $exe) {
                $version = (Get-Item $exe).VersionInfo.ProductVersion
                Write-Host "Found (Store): $exe (v$version)" -ForegroundColor Green
                return @{ Path = $exe; Version = $version }
            }
        }
    }

    return $null
}

# -- Main ---------------------------------------------------------------------

Write-Host "=== WoT Blitz Executable Hash Computer ===" -ForegroundColor Cyan
Write-Host ""

if (-not $ExePath) {
    $discovered = Find-WotBlitzExe
    if (-not $discovered) {
        Write-Error "Could not find wotblitz.exe. Provide -ExePath or ensure the game is installed."
        exit 1
    }
    $ExePath = $discovered.Path
    $GameVersion = $discovered.Version
}
else {
    if (-not (Test-Path $ExePath)) {
        Write-Error "File not found: $ExePath"
        exit 1
    }
    $GameVersion = (Get-Item $ExePath).VersionInfo.ProductVersion
}

Write-Host "Executable : $ExePath"
Write-Host "Version    : $GameVersion"
Write-Host ""

# Compute SHA-256
Write-Host "Computing SHA-256..." -NoNewline
$hash = (Get-FileHash -Path $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host " done."
Write-Host ""

Write-Host "SHA-256: $hash" -ForegroundColor Yellow
Write-Host "Length : $($hash.Length) chars"
Write-Host ""

# -- Update offset file -------------------------------------------------------

if ($UpdateOffsetFile) {
    if (-not $OffsetDir) {
        # Auto-discover repo root
        $repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
        $OffsetDir = Join-Path $repoRoot "memory-offsets"
    }

    if (-not (Test-Path $OffsetDir)) {
        Write-Error "Offset directory not found: $OffsetDir"
        exit 1
    }

    $offsetFile = Join-Path $OffsetDir "$GameVersion.json"
    $normalizedVersion = $GameVersion

    # Try partial version match if exact not found (e.g. 11.19.0.10 -> 11.19.0.10.json)
    if (-not (Test-Path $offsetFile)) {
        $matches = Get-ChildItem $OffsetDir -Filter "*.json" |
            Where-Object { $_.BaseName -like "$($GameVersion.Split('.')[0..2] -join '.')*" } |
            Where-Object { $_.BaseName -ne "schema" -and $_.BaseName -ne "scanner-state" }

        if ($matches.Count -eq 1) {
            $offsetFile = $matches[0].FullName
            $normalizedVersion = $matches[0].BaseName
            Write-Host "Matched offset file: $normalizedVersion.json" -ForegroundColor Yellow
        }
        elseif ($matches.Count -gt 1) {
            Write-Error "Multiple offset files match version $GameVersion. Be explicit."
            $matches | ForEach-Object { Write-Host "  $_" }
            exit 1
        }
    }

    if (-not (Test-Path $offsetFile)) {
        Write-Error "No offset file found for version $GameVersion. Create one first: memory-offsets/$GameVersion.json"
        exit 1
    }

    Write-Host "Updating offset file: $offsetFile" -ForegroundColor Cyan

    $json = Get-Content $offsetFile -Raw | ConvertFrom-Json
    $json.executableSha256 = $hash
    $json.discoveredAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    # If notes contains the placeholder hash message, update it
    if ($json.notes -like "*Hash is empty*") {
        $json.notes = $json.notes -replace [regex]::Escape("Hash is empty - compute with tools/compute-exe-hash.ps1 and update before runtime use. "), ""
    }

    $json | ConvertTo-Json -Depth 6 | Set-Content $offsetFile -Encoding UTF8

    Write-Host "Updated:" -ForegroundColor Green
    Write-Host "  executableSha256: $hash"
    Write-Host "  discoveredAtUtc:  $($json.discoveredAtUtc)"
    Write-Host ""

    # Validate JSON is still parsable
    try {
        $null = Get-Content $offsetFile -Raw | ConvertFrom-Json
        Write-Host "JSON validation: OK" -ForegroundColor Green
    }
    catch {
        Write-Error "JSON validation FAILED after update: $_"
        exit 1
    }
}

Write-Host "Done." -ForegroundColor Cyan
