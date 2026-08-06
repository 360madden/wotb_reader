[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [int]$MaxReadRounds = 70,
    [int]$StageTopN = 2,
    [int]$StageDelaySeconds = 2,
    [string]$ResultPath = '',
    [int]$MarkerWaitSeconds = 180
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $RepoRoot '.data\od-049-hangar-result.json'
}

function Write-Log([string]$Msg) { Write-Host ("hangarm1: " + $Msg) }

function Get-LogState {
    $logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    foreach ($log in $logs) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 1200 -ErrorAction SilentlyContinue)
        $lastMarker = $null
        $count = 0
        foreach ($line in $lines) {
            if ([string]$line -match 'START_REPLAY_LOCAL|Start replay event') {
                $count++
                if ([string]$line -match '^(\d{2}:\d{2}:\d{2})') { $lastMarker = $Matches[1] }
            }
        }
        return [pscustomobject]@{ Log = $log.FullName; Count = $count; LastMarkerTime = $lastMarker }
    }
    return $null
}

function Get-LatestMarkerUtc {
    $state = Get-LogState
    if (-not $state -or -not $state.LastMarkerTime) { return $null }
    # Log first column is UTC; anchor DATE must come from the current UTC
    # date, not local (local breaks after 20:00 local when UTC has rolled to
    # the next day - FRESH10 live proof: staged=0 with elapsed_s=86403.9).
    $utcToday = ([datetime]::UtcNow).Date
    $parsed = [datetime]::ParseExact($utcToday.ToString('yyyy-MM-dd') + 'T' + $state.LastMarkerTime, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
    if ($asUtc -gt [datetime]::UtcNow) { $asUtc = $asUtc.AddDays(-1) }
    return $asUtc.ToString('o')
}

$before = Get-LogState
Write-Log ("before log=" + $(if ($before) { [IO.Path]::GetFileName($before.Log) } else { 'none' }) +
    " count=" + $(if ($before) { $before.Count } else { 0 }) +
    " last=" + $(if ($before) { $before.LastMarkerTime } else { 'none' }))

# 1. Play the replay from the hangar (reuses the running game window).
Write-Log 'playing from hangar'
& (Join-Path $RepoRoot 'scripts\play-replay-from-hangar.ps1') *>&1 |
    ForEach-Object { Write-Log ("hangar: " + $_) }
$hangarExit = $LASTEXITCODE
Write-Log ("hangar_exit=" + $hangarExit)

# 2. Wait for a FRESH Start marker.
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
