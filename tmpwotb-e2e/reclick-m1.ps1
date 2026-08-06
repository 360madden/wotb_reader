[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [int]$MaxReadRounds = 70,
    [int]$StageTopN = 2,
    [int]$StageDelaySeconds = 2,
    [string]$ResultPath = '',
    [int]$MarkerWaitSeconds = 120
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $RepoRoot '.data\od-049-reclick-result.json'
}

function Write-Log([string]$Msg) { Write-Host ("reclick: " + $Msg) }

# Snapshot the newest blitz-log's current marker count + last marker time BEFORE
# the click, so we only react to a FRESH marker after it.
function Get-LogState {
    $logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    foreach ($log in $logs) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 800 -ErrorAction SilentlyContinue)
        $lastMarker = $null
        $count = 0
        foreach ($line in $lines) {
            if ([string]$line -match 'START_REPLAY_LOCAL|Start replay event') {
                $count++
                if ([string]$line -match '^(\d{2}:\d{2}:\d{2})') {
                    $lastMarker = $Matches[1]
                }
            }
        }
        return [pscustomobject]@{ Log = $log.FullName; Count = $count; LastMarkerTime = $lastMarker }
    }
    return $null
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

function Get-LatestMarkerUtc {
    $state = Get-LogState
    if (-not $state -or -not $state.LastMarkerTime) { return $null }
    # Log first column is UTC; anchor DATE comes from the LOG FILE (filename
    # date -> LastWriteTime), NOT the wall clock - a stale log parsed today
    # would otherwise anchor a full day off (FRESH10 failure class).
    $anchorDateUtc = Get-LogAnchorDateUtc -LogPath $state.Log
    $parsed = [datetime]::ParseExact($anchorDateUtc.ToString('yyyy-MM-dd') + 'T' + $state.LastMarkerTime, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
    if ($asUtc -gt [datetime]::UtcNow) { $asUtc = $asUtc.AddDays(-1) }
    return $asUtc.ToString('o')
}

$before = Get-LogState
Write-Log ("before_click log=" + $(if ($before) { [IO.Path]::GetFileName($before.Log) } else { 'none' }) +
    " count=" + $(if ($before) { $before.Count } else { 0 }) +
    " last=" + $(if ($before) { $before.LastMarkerTime } else { 'none' }))

# 1. Re-click Watch Offline (no-op if the dialog is absent; the clicker waits
#    for a fresh marker and stops clicking once it appears).
$clicker = Join-Path $RepoRoot 'scripts\click-watch-offline.ps1'
& $clicker *>&1 | ForEach-Object { Write-Log ("click: " + $_) }
$clickExit = $LASTEXITCODE
Write-Log ("click_exit=" + $clickExit)

# 2. Wait for a FRESH Start marker (count or last-marker time changed).
$deadline = (Get-Date).AddSeconds($MarkerWaitSeconds)
$markerUtc = $null
while ((Get-Date) -lt $deadline) {
    $after = Get-LogState
    if ($after -and $after.Log -eq $before.Log) {
        if ($after.Count -gt $before.Count -or ($after.LastMarkerTime -and $after.LastMarkerTime -ne $before.LastMarkerTime)) {
            $markerUtc = Get-LatestMarkerUtc
            Write-Log ("fresh_marker count=" + $after.Count + " last=" + $after.LastMarkerTime + " utc=" + $markerUtc)
            break
        }
    }
    Start-Sleep -Seconds 2
}
if (-not $markerUtc) {
    Write-Log 'FAILED_no_fresh_marker'
    exit 2
}

# 3. Run M1 anchored to the fresh marker.
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
exit $m1Exit
