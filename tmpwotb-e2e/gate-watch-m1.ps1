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
function Get-LogAnchorDateUtc([string]$LogPath) {
    # The anchor DATE comes from the LOG FILE, not the wall clock: the blitz
    # log's first column is UTC, and the file's own name embeds the real
    # local write date (blitz-logs_YYYYMMDDHHMMSS.txt). UtcNow.Date is only
    # correct for a same-day log; parsing yesterday's log today would anchor a
    # full day off (the same failure class as the FRESH10 local-date bug).
    # Priority: filename date -> LastWriteTime -> UtcNow, each normalized to
    # the UTC date.
    if (-not [string]::IsNullOrWhiteSpace($LogPath) -and (Split-Path $LogPath -Leaf) -match 'blitz-logs_(\d{8})(\d{6})') {
        try {
            $fileLocal = [datetime]::ParseExact($Matches[1] + ' ' + $Matches[2], 'yyyyMMdd HHmmss', [Globalization.CultureInfo]::InvariantCulture)
            return ([datetime]::SpecifyKind($fileLocal, [DateTimeKind]::Local)).ToUniversalTime().Date
        }
        catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        try {
            $item = Get-Item -LiteralPath $LogPath -ErrorAction Stop
            return ($item.LastWriteTime).ToUniversalTime().Date
        }
        catch { }
    }
    return ([datetime]::UtcNow).Date
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
                    # Log first column is UTC; anchor DATE comes from the
                    # LOG FILE (filename date -> LastWriteTime), NOT the wall
                    # clock - a stale log parsed today would anchor a full
                    # day off (FRESH10 failure class).
                    $anchorDateUtc = Get-LogAnchorDateUtc -LogPath $log.FullName
                    $parsed = [datetime]::ParseExact($anchorDateUtc.ToString('yyyy-MM-dd') + 'T' + $timeOnly, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
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
