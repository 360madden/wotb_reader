<#
.SYNOPSIS
    Automated offset discovery pipeline for WoT Blitz.
    Orchestrates Cheat Engine scanning, result parsing, and offset file updates.

.DESCRIPTION
    The full pipeline:
    1. Verify prerequisites (game running, tools available)
    2. Launch Cheat Engine with the auto-discover Lua script
    3. Wait for CE to produce candidate JSON output
    4. Parse the CE results
    5. Compute SHA-256 of the game executable
    6. Update memory-offsets/<version>.json with discovered offsets
    7. Optionally validate via the GameHarness web API

.PARAMETER GameVersion
    Target game version (e.g. "11.19.0.10"). Auto-detected if omitted.

.PARAMETER CeExePath
    Path to Cheat Engine executable. Auto-detected from known locations.

.PARAMETER LuaScript
    Path to the Cheat Engine Lua script. Defaults to multiscan.lua.

.PARAMETER SkipValidation
    If set, skips the GameHarness validation step.

.PARAMETER DryRun
    If set, checks prerequisites and prints the plan without executing.

.EXAMPLE
    .\tools\discover-offsets.ps1
    # Full automatic pipeline with version auto-detection.

.EXAMPLE
    .\tools\discover-offsets.ps1 -DryRun
    # Check prerequisites only.

.EXAMPLE
    .\tools\discover-offsets.ps1 -GameVersion 11.19.0.10 -SkipValidation
    # Discover offsets, skip the GameHarness API validation.
#>

[CmdletBinding()]
param(
    [string]$GameVersion,
    [string]$CeExePath,
    [string]$LuaScript,
    [switch]$SkipValidation,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Get-Item $PSScriptRoot).Parent.FullName

# ── Path discovery ──────────────────────────────────────────────────────────

function Get-CheatEnginePath {
    $candidates = @(
        "C:\Program Files\Cheat Engine 7.7\cheatengine-x86_64.exe",
        "C:\Program Files\Cheat Engine 7.5\cheatengine-x86_64.exe",
        "C:\Program Files\Cheat Engine\cheatengine-x86_64.exe",
        "C:\Program Files (x86)\Cheat Engine 7.7\cheatengine-x86_64.exe",
        "C:\Program Files (x86)\Cheat Engine 7.5\cheatengine-x86_64.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    # Search PATH
    $fromPath = Get-Command cheatengine-x86_64.exe -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }

    return $null
}

function Get-GameExePath {
    $candidates = @(
        "C:\Games\World_of_Tanks_Blitz\wotblitz.exe",
        "C:\Program Files\World_of_Tanks_Blitz\wotblitz.exe",
        (Join-Path $env:LOCALAPPDATA "World_of_Tanks_Blitz\wotblitz.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return @{ Path = $candidate; Version = (Get-Item $candidate).VersionInfo.ProductVersion }
        }
    }
    return $null
}

# ── Prerequisites check ─────────────────────────────────────────────────────

function Test-Prerequisites {
    Write-Host "=== Prerequisites Check ===" -ForegroundColor Cyan

    # 1. Game running
    $gameProcess = Get-Process wotblitz -ErrorAction SilentlyContinue
    if (-not $gameProcess) {
        Write-Host "[FAIL] wotblitz.exe is not running. Start the game with a replay first." -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] wotblitz.exe is running (PID: $($gameProcess.Id))" -ForegroundColor Green

    # 2. Cheat Engine
    if (-not $CeExePath) {
        $CeExePath = Get-CheatEnginePath
    }
    if (-not $CeExePath -or -not (Test-Path $CeExePath)) {
        Write-Host "[FAIL] Cheat Engine not found. Install CE 7.5+ or provide -CeExePath." -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Cheat Engine: $CeExePath" -ForegroundColor Green

    # 3. Game executable
    $gameExe = Get-GameExePath
    if (-not $gameExe) {
        Write-Host "[FAIL] Game executable not found at known paths." -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Game exe: $($gameExe.Path) (v$($gameExe.Version))" -ForegroundColor Green

    # 4. Lua script
    if (-not $LuaScript) {
        $LuaScript = Join-Path $RepoRoot "tools\cheat-engine\multiscan.lua"
    }
    if (-not (Test-Path $LuaScript)) {
        Write-Host "[FAIL] Lua script not found: $LuaScript" -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Lua script: $LuaScript" -ForegroundColor Green

    # 5. Offset file
    $version = if ($GameVersion) { $GameVersion } else { $gameExe.Version }
    $offsetFile = Join-Path $RepoRoot "memory-offsets\$version.json"
    if (Test-Path $offsetFile) {
        Write-Host "[PASS] Offset file exists: $offsetFile" -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] Offset file not found: $offsetFile (will be created)" -ForegroundColor Yellow
    }

    return $true
}

# ── Main ────────────────────────────────────────────────────────────────────

Write-Host @"
╔══════════════════════════════════════════════════════╗
║   WoT Blitz — Automated Offset Discovery Pipeline    ║
╚══════════════════════════════════════════════════════╝
"@ -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Prerequisites)) {
    Write-Host ""
    Write-Error "Prerequisites not met. Fix the issues above and retry."
    exit 1
}

if ($DryRun) {
    Write-Host ""
    Write-Host "Dry run complete. All prerequisites met." -ForegroundColor Green
    Write-Host "Run without -DryRun to execute the discovery pipeline."
    exit 0
}

# ── Phase 1: Cheat Engine Scan ──────────────────────────────────────────────

$gameExe = Get-GameExePath
$version = if ($GameVersion) { $GameVersion } else { $gameExe.Version }
$offsetFile = Join-Path $RepoRoot "memory-offsets\$version.json"
$ceOutputFile = Join-Path $RepoRoot "tools\cheat-engine\discovered-offsets-multiscan.json"

Write-Host ""
Write-Host "=== Phase 1: Cheat Engine Auto-Discovery ===" -ForegroundColor Cyan
Write-Host "Target version: $version"
Write-Host "Lua script    : $LuaScript"
Write-Host "CE output     : $ceOutputFile"
Write-Host ""

# NOTE: Cheat Engine's auto-launch from command line with Lua script execution
# requires the user to have CE configured to auto-load scripts, or we need to
# use CE's command-line interface. CE does NOT support fully headless Lua
# execution from CLI. The user must:

Write-Host "ACTION REQUIRED:" -ForegroundColor Yellow
Write-Host "  1. Cheat Engine should already be attached to wotblitz.exe"
Write-Host "  2. In CE, press Ctrl+Alt+L to open the Lua Engine"
Write-Host "  3. Paste and execute the multiscan.lua script"
Write-Host "  4. Run: autoDiscover()" -ForegroundColor White
Write-Host "  5. Wait for completion, then run: saveDiscovered()"
Write-Host "  6. The output will be at: $ceOutputFile"
Write-Host ""
Write-Host "Press ENTER after CE has completed and the output file exists..." -ForegroundColor Yellow
Read-Host

if (-not (Test-Path $ceOutputFile)) {
    Write-Error "CE output file not found: $ceOutputFile"
    Write-Host "Did you run saveDiscovered() in Cheat Engine?" -ForegroundColor Yellow
    exit 1
}

# ── Phase 2: Parse CE output ────────────────────────────────────────────────

Write-Host ""
Write-Host "=== Phase 2: Parse CE Results ===" -ForegroundColor Cyan

try {
    $ceData = Get-Content $ceOutputFile -Raw | ConvertFrom-Json
    Write-Host "Field: $($ceData.fieldName)"
    Write-Host "Candidates: $($ceData.totalCandidates)"
    Write-Host "Module base: $($ceData.moduleBase)"
}
catch {
    Write-Error "Failed to parse CE output: $_"
    exit 1
}

# ── Phase 3: Compute executable hash ────────────────────────────────────────

Write-Host ""
Write-Host "=== Phase 3: Compute Executable Hash ===" -ForegroundColor Cyan

$hash = (Get-FileHash -Path $gameExe.Path -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SHA-256: $hash" -ForegroundColor Yellow

# ── Phase 4: Update offset file ─────────────────────────────────────────────

Write-Host ""
Write-Host "=== Phase 4: Update Offset File ===" -ForegroundColor Cyan

if (-not (Test-Path $offsetFile)) {
    # Create a new offset file from template
    $template = @{
        schemaVersion    = 1
        gameVersion      = $version
        executableSha256 = $hash
        discoveredAtUtc  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        offsets          = @{
            replayTime      = 0
            playerHP        = 0
            playerPositionX = 0
            playerPositionY = 0
            playerPositionZ = 0
            playerYaw       = 0
            cameraPitch     = 0
            aliveTankCount  = 0
        }
        fieldValidation  = @{}
        confidence       = "low"
        notes            = "Discovered via automated CE pipeline on $(Get-Date -Format 'yyyy-MM-dd'). Verify with x64dbg before promoting to medium/high."
    }

    # Add discovered candidates from CE
    if ($ceData.candidates -and $ceData.candidates.Count -gt 0) {
        $fieldName = $ceData.fieldName
        $ceOffset = 0
        if ($ceData.candidates[0].relativeOffset -match "0x([0-9a-fA-F]+)") {
            $ceOffset = [Convert]::ToInt64($matches[1], 16)
        }
        $template.offsets.$fieldName = $ceOffset

        $fieldValidation = @{
            status                     = "Candidate"
            evidence                   = @(
                @{
                    provenanceKind = "DynamicScan"
                    sourceTool     = "Cheat Engine 7.7 — multiscan.lua autoDiscover()"
                    notes          = "Discovered via timer-based multi-scan refinement on $(Get-Date -Format 'yyyy-MM-dd'). $($ceData.totalCandidates) candidates narrowed from initial scan."
                }
            )
            independentProcessLaunches = 0
            independentReplays        = 0
            harnessInvariantsPassed   = $false
            leadApproved              = $false
            decoderAuditorApproved    = $false
        }
        $template.fieldValidation[$fieldName] = $fieldValidation

        Write-Host "Discovered $fieldName offset: 0x$($ceOffset.ToString('X'))" -ForegroundColor Green
    }

    $template | ConvertTo-Json -Depth 5 | Set-Content $offsetFile -Encoding UTF8
    Write-Host "Created: $offsetFile" -ForegroundColor Green
}
else {
    # Merge into existing offset file
    $existing = Get-Content $offsetFile -Raw | ConvertFrom-Json
    $existing.executableSha256 = $hash
    $existing.discoveredAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTH:mm:ssZ")

    if ($ceData.candidates -and $ceData.candidates.Count -gt 0) {
        $fieldName = $ceData.fieldName
        $ceOffset = 0
        if ($ceData.candidates[0].relativeOffset -match "0x([0-9a-fA-F]+)") {
            $ceOffset = [Convert]::ToInt64($matches[1], 16)
        }
        $existing.offsets.$fieldName = $ceOffset

        if (-not $existing.fieldValidation) {
            $existing | Add-Member -NotePropertyName "fieldValidation" -NotePropertyValue @{} -Force -PassThru | Out-Null
        }

        $fv = @{
            status                     = "Candidate"
            evidence                   = @(
                @{
                    provenanceKind = "DynamicScan"
                    sourceTool     = "Cheat Engine 7.7 — multiscan.lua autoDiscover()"
                    notes          = "Discovered via automated CE pipeline on $(Get-Date -Format 'yyyy-MM-dd'). $($ceData.totalCandidates) candidates narrowed."
                }
            )
            independentProcessLaunches = 0
            independentReplays        = 0
            harnessInvariantsPassed   = $false
            leadApproved              = $false
            decoderAuditorApproved    = $false
        }
        $existing.fieldValidation = $existing.fieldValidation | Add-Member -NotePropertyName $fieldName -NotePropertyValue $fv -Force -PassThru
    }

    $existing.confidence = "low"
    $existing | ConvertTo-Json -Depth 6 | Set-Content $offsetFile -Encoding UTF8
    Write-Host "Updated: $offsetFile" -ForegroundColor Green
}

# ── Phase 5: Validate (optional) ────────────────────────────────────────────

if (-not $SkipValidation) {
    Write-Host ""
    Write-Host "=== Phase 5: Validate ===" -ForegroundColor Cyan

    # Run the Python offset checker if available
    $checkerScript = Join-Path $RepoRoot "scripts\python\offset_check.py"
    if (Test-Path $checkerScript) {
        $pythonCmd = Get-Command python -ErrorAction SilentlyContinue
        if (-not $pythonCmd) { $pythonCmd = Get-Command python3 -ErrorAction SilentlyContinue }
        if ($pythonCmd) {
            Write-Host "Running offset schema validator..."
            & $pythonCmd.Source $checkerScript 2>&1 | ForEach-Object { Write-Host "  $_" }
        }
        else {
            Write-Host "[WARN] Python not found — skipping schema validation" -ForegroundColor Yellow
        }
    }

    # Write post-discovery instructions
    Write-Host ""
    Write-Host "=== Next Steps ===" -ForegroundColor Cyan
    Write-Host "1. Verify offsets dynamically:" -ForegroundColor White
    Write-Host "   - Open Cheat Engine, attach to wotblitz.exe"
    Write-Host "   - Add each discovered offset as a manual address"
    Write-Host "   - Watch values change during replay playback"
    Write-Host ""
    Write-Host "2. Cross-battle validation:"
    Write-Host "   - Restart the game with a different replay"
    Write-Host "   - Re-verify all offsets are still valid"
    Write-Host ""
    Write-Host "3. Promote to Verified:"
    Write-Host "   - After 2+ process launches and 2+ replays confirm offsets"
    Write-Host "   - Use x64dbg to confirm struct base register"
    Write-Host "   - Update fieldValidation status to 'Verified'"
    Write-Host ""
    Write-Host "4. Test via web host:"
    Write-Host "   - serve.cmd + overlay.cmd"
    Write-Host "   - Check GET /api/v1/game/memory returns non-null values"
}

Write-Host ""
Write-Host "Pipeline complete." -ForegroundColor Green
