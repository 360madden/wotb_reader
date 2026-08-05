[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$ReplayPath = '.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay',
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
    $ResultPath = Join-Path $RepoRoot '.data\od-049-fresh-result.json'
}

function Write-Log([string]$Msg) { Write-Host ("freshm1: " + $Msg) }

function Get-LogState {
    $logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    foreach ($log in $logs) {
        $lines = @(Get-Content -LiteralPath $log.FullName -Tail 1500 -ErrorAction SilentlyContinue)
        $lastMarker = $null
        $lastSceneEnds = $null
        $count = 0
        foreach ($line in $lines) {
            if ([string]$line -match 'START_REPLAY_LOCAL|Start replay event') {
                $count++
                if ([string]$line -match '^(\d{2}:\d{2}:\d{2})') { $lastMarker = $Matches[1] }
            }
            # The battle-accurate tick-0 anchor is LoadGameScene ENDS (the
            # world is live and positions exist), NOT the Start marker (which
            # fires when loading begins, and on a flaked first battle the
            # "fresh" marker is that battle's death). Exact-match staging
            # tolerates no anchor skew, so anchor to scene-ends.
            if ([string]$line -match 'LoadGameScene ends') {
                if ([string]$line -match '^(\d{2}:\d{2}:\d{2})') { $lastSceneEnds = $Matches[1] }
            }
        }
        return [pscustomobject]@{
            Log = $log.FullName
            Count = $count
            LastMarkerTime = $lastMarker
            LastSceneEndsTime = $lastSceneEnds
        }
    }
    return $null
}

function Convert-LogTimeToUtc([string]$Time) {
    $today = (Get-Date).Date
    $parsed = [datetime]::ParseExact($today.ToString('yyyy-MM-dd') + 'T' + $Time, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    return [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc).ToString('o')
}

function Get-LatestMarkerUtc {
    $state = Get-LogState
    if (-not $state -or -not $state.LastMarkerTime) { return $null }
    return Convert-LogTimeToUtc $state.LastMarkerTime
}

# Stop any stale game/host, then launch fresh via the OD launch script.
Get-Process -Name wotblitz, WotBTreader.Host.Web, x32dbg -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$before = Get-LogState
Write-Log ("before log=" + $(if ($before) { [IO.Path]::GetFileName($before.Log) } else { 'none' }) +
    " count=" + $(if ($before) { $before.Count } else { 0 }) +
    " last=" + $(if ($before) { $before.LastMarkerTime } else { 'none' }))

Write-Log "launching replay: $ReplayPath"
& (Join-Path $RepoRoot 'scripts\launch-offline-replay-for-od.ps1') -ReplayPath $ReplayPath *>&1 |
    ForEach-Object { Write-Log ("launch: " + $_) }
$launchExit = $LASTEXITCODE
Write-Log ("launch_exit=" + $launchExit)
if ($launchExit -ne 0) { exit $launchExit }

# Wait for a FRESH Start marker (new battle started by this launch), then
# anchor to that battle's LoadGameScene-ends (tick 0). On a flaked first
# battle the Start marker is the dead battle's; the surviving auto-loop
# battle's scene-ends follows within seconds, so keep polling until a scene
# ends arrives AFTER the fresh Start marker.
$deadline = (Get-Date).AddSeconds($MarkerWaitSeconds)
$anchorUtc = $null
while ((Get-Date) -lt $deadline) {
    $after = Get-LogState
    if ($after) {
        $fresh = $false
        if ($after.Log -eq $before.Log -and ($after.Count -gt $before.Count -or ($after.LastMarkerTime -and $after.LastMarkerTime -ne $before.LastMarkerTime))) {
            $fresh = $true
        }
        elseif ($after.Log -ne $before.Log -and $after.Count -gt 0) {
            $fresh = $true
        }
        if ($fresh -and $after.LastSceneEndsTime) {
            $anchorUtc = Convert-LogTimeToUtc $after.LastSceneEndsTime
            Write-Log ("fresh_marker count=" + $after.Count + " last=" + $after.LastMarkerTime +
                " scene_ends=" + $after.LastSceneEndsTime + " utc=" + $anchorUtc)
            break
        }
    }
    Start-Sleep -Seconds 2
}
if (-not $anchorUtc) {
    Write-Log 'FAILED_no_fresh_marker'
    exit 2
}

Write-Log ("running M1 anchored=" + $anchorUtc + " rounds=" + $MaxReadRounds + " topN=" + $StageTopN)
# od-048 is pwsh-7-targeted (PS7-only `(if ...)` expression syntax, and only
# pwsh surfaces HTTP 400 error bodies via ErrorDetails; Windows PowerShell 5.1
# swallows both -> invisible api_failed with no reason). The launch flow above
# stays on 5.1 (battle-tested), but the driver MUST run under pwsh 7.
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1') `
    -ReplayStartWallTimeUtc $anchorUtc `
    -MaxReadRounds $MaxReadRounds `
    -StageTopN $StageTopN `
    -StageDelaySeconds $StageDelaySeconds `
    -AutoWriteTraceOnVerdict `
    -ResultPath $ResultPath *>&1 | ForEach-Object { Write-Log ("m1: " + $_) }
$m1Exit = $LASTEXITCODE
Write-Log ("m1_exit=" + $m1Exit)
exit $m1Exit
