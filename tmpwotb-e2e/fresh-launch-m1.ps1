[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    # Dead Rail content hash 59c3b92eb221 (same battle FRESH9 played); the
    # old .data\launch\a9aed... staging copy is gone, and the picker would
    # otherwise choose the human-named Churchill I replay (different battle).
    [string]$ReplayPath = "$env:LOCALAPPDATA\wotblitz\DAVAProject\replays\36f5abcfa07e4763adcd31af50300fd0.wotbreplay",
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

function Get-LogAnchorDateUtc([string]$LogPath) {
    # The anchor DATE comes from the LOG FILE, not the wall clock: the blitz
    # log's first column is UTC ("01:44:12 [info] 20:44:12 -5" where 20:44 is
    # the engine's UTC-5 offset), and the file's own name embeds the real
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

function Convert-LogTimeToUtc([string]$Time, [string]$LogPath = '') {
    $anchorDateUtc = Get-LogAnchorDateUtc -LogPath $LogPath
    $parsed = [datetime]::ParseExact($anchorDateUtc.ToString('yyyy-MM-dd') + 'T' + $Time, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
    # Rollover guard: a future-dated anchor means the marker line was written
    # just before UTC midnight (the file-date and the marker's UTC date can
    # straddle it), so pull back a day.
    if ($asUtc -gt [datetime]::UtcNow) { $asUtc = $asUtc.AddDays(-1) }
    return $asUtc.ToString('o')
}

function Get-LatestMarkerUtc {
    $state = Get-LogState
    if (-not $state -or -not $state.LastMarkerTime) { return $null }
    return Convert-LogTimeToUtc -Time $state.LastMarkerTime -LogPath $state.Log
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
            $anchorUtc = Convert-LogTimeToUtc $after.LastSceneEndsTime -LogPath $after.Log
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
    -AttachSmokeOnFirstRound `
    -ResultPath $ResultPath *>&1 | ForEach-Object { Write-Log ("m1: " + $_) }
$m1Exit = $LASTEXITCODE
Write-Log ("m1_exit=" + $m1Exit)
exit $m1Exit
