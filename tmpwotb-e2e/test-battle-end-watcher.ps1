# Offline validation for the FRESH30 battle-end watcher (OD-RECOVERY-051).
# Extracts Test-BlitzBattleEnded + Get-NewestBlitzLog from od-048 and runs
# them against real blitz-log fixtures copied to .data/blitz-logs/.
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$src = Get-Content -Raw (Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1')
$start = $src.IndexOf('function Test-BlitzBattleEnded')
$end = $src.IndexOf('function Get-SurvivorBandWidth')
if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
    Write-Error 'could not locate the battle-end functions in od-048'
    exit 2
}
$fn = $src.Substring($start, $end - $start)
# The functions read $RepoRoot from the enclosing scope (fixture dir fallback).
$script:RepoRoot = $RepoRoot
$sb = [scriptblock]::Create($fn)
. $sb

$fail = 0
function Assert([bool]$Cond, [string]$Label) {
    if ($Cond) { Write-Host ("PASS " + $Label) }
    else { Write-Host ("FAIL " + $Label); $script:fail++ }
}

# Test 1: FRESH30 anchor (battle ended 23:19:05, after anchor) -> True
$t1 = Test-BlitzBattleEnded -AnchorUtc '2026-08-06T23:16:47Z'
Assert ($t1 -eq $true) ("T1 FRESH30 anchor -> battle-ended=True (got " + $t1 + ")")

# Test 2: anchor AFTER the battle end (23:20:00) -> no lines at/after -> False
$t2 = Test-BlitzBattleEnded -AnchorUtc '2026-08-06T23:20:00Z'
Assert ($t2 -eq $false) ("T2 post-end anchor -> False (got " + $t2 + ")")

# Test 3: newest-log smoke probe with a pre-battle anchor (informational)
$t3 = Test-BlitzBattleEnded -AnchorUtc '2026-08-06T11:55:00Z'
Write-Host ("INFO T3 newest-log probe -> " + $t3)

if ($fail -gt 0) { exit 1 }
Write-Host 'ALL_OK'
exit 0
