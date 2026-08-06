[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [int]$MaxReadRounds = 70,
    [int]$StageTopN = 2,
    [int]$StageDelaySeconds = 2,
    [string]$ResultPath = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $RepoRoot '.data\od-049-gatewatch-result.json'
}

function Write-Log([string]$Msg) { Write-Host ("gatewatch: " + $Msg) }
function Get-Rendezvous {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
}
function Get-Gate {
    param([object]$Rendezvous)
    if (-not $Rendezvous) { return $null }
    try {
        return Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/state') -TimeoutSec 5 -Headers @{
            'X-WotBTreader-Capability' = [string]$Rendezvous.capability
        }
    } catch { return $null }
}
function Get-LatestStartMarkerUtc {
    # Read the newest blitz-log; return the LAST 'Start replay event'/'START_REPLAY_LOCAL'
    # marker's leading timestamp as UTC ISO. Returns $null when absent.
    $logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    foreach ($log in $logs) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 800 -ErrorAction SilentlyContinue)
        for ($i = $lines.Count - 1; $i -ge 0; $i--) {
            $line = [string]$lines[$i]
            if ($line -match 'START_REPLAY_LOCAL|Start replay event') {
                if ($line -match '^(\d{2}:\d{2}:\d{2})') {
                    $timeOnly = $Matches[1]
                    # Log first column is UTC; anchor DATE must come from the
                    # current UTC date, not local (local breaks after 20:00
                    # local when UTC has rolled to the next day - FRESH10
                    # live proof: staged=0 with elapsed_s=86403.9).
                    $utcToday = ([datetime]::UtcNow).Date
                    $parsed = [datetime]::ParseExact($utcToday.ToString('yyyy-MM-dd') + 'T' + $timeOnly, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                    $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
                    if ($asUtc -gt [datetime]::UtcNow) { $asUtc = $asUtc.AddDays(-1) }
                    return $asUtc.ToString('o')
                }
            }
        }
    }
    return $null
}

# Watch for a FRESH verified transition. First poll establishes the baseline;
# the FIRST transition from non-verified -> verified after the baseline fires M1.
$rendezvous = Get-Rendezvous
if (-not $rendezvous) { Write-Log 'FAILED_no_rendezvous'; exit 1 }

$baseline = $null
$deadline = (Get-Date).AddSeconds(600)
$launched = $false
while ((Get-Date) -lt $deadline) {
    $gate = Get-Gate -Rendezvous $rendezvous
    $state = if ($gate) { [string]$gate.verificationState } else { 'no-host' }
    if ($null -eq $baseline) {
        $baseline = $state
        Write-Log ("baseline_gate=" + $baseline)
        if ($baseline -eq 'OfflineReplayVerified') {
            # Gate is already verified; wait for it to go non-verified first so the
            # next verification is a FRESH transition (a fresh battle window).
            Write-Log 'gate_already_verified_waiting_for_transition'
        }
    }
    elseif ($state -eq 'OfflineReplayVerified' -and $baseline -ne 'OfflineReplayVerified') {
        Write-Log ("FRESH_VERIFIED at " + (Get-Date).ToString('o'))
        $markerUtc = Get-LatestStartMarkerUtc
        if (-not $markerUtc) { Write-Log 'WARN_no_marker_using_now'; $markerUtc = ([DateTime]::UtcNow).ToString('o') }
        Write-Log ("marker_utc=" + $markerUtc)
        Write-Log ("running M1 anchored=" + $markerUtc + " rounds=" + $MaxReadRounds + " topN=" + $StageTopN)
        & (Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1') `
            -ReplayStartWallTimeUtc $markerUtc `
            -MaxReadRounds $MaxReadRounds `
            -StageTopN $StageTopN `
            -StageDelaySeconds $StageDelaySeconds `
            -AutoWriteTraceOnVerdict `
            -ResultPath $ResultPath *>&1 | ForEach-Object { Write-Log ("m1: " + $_) }
        $m1Exit = $LASTEXITCODE
        Write-Log ("m1_exit=" + $m1Exit)
        $launched = $true
        exit $m1Exit
    }
    Start-Sleep -Seconds 2
}
Write-Log 'FAILED_no_fresh_verified'
exit 2
