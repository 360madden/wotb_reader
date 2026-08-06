[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$ReplayPath = '.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay',
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
    $ResultPath = Join-Path $RepoRoot '.data\od-049-autoloop-result.json'
}

function Write-Log([string]$Msg) {
    Write-Host ("autoloop: " + $Msg)
}

# 1. Launch the replay (managed launch -> gate verified)
Write-Log "launching replay: $ReplayPath"
& (Join-Path $RepoRoot 'scripts\launch-offline-replay-for-od.ps1') -ReplayPath $ReplayPath *>&1 |
    ForEach-Object { Write-Log ("launch: " + $_) }
$launchExit = $LASTEXITCODE
Write-Log ("launch_exit=" + $launchExit)
if ($launchExit -ne 0) { exit $launchExit }

# 2. Extract the latest 'Start replay event' / START_REPLAY_LOCAL marker UTC from
#    the newest blitz log. The blitz-log's leading timestamp IS the UTC wall
#    clock (the host lifecycle parser reads it with AssumeUniversal), e.g.
#    15:38:27 [info] 10:38:27 -5 [replay] Start replay event -> UTC 15:38:27.
$logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)
$markerUtc = $null
foreach ($log in $logs) {
    $lines = @(Get-Content -LiteralPath $log.FullName -Tail 400 -ErrorAction SilentlyContinue)
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = [string]$lines[$i]
        if ($line -match 'START_REPLAY_LOCAL|Start replay event') {
            if ($line -match '^(\d{2}:\d{2}:\d{2})') {
                $timeOnly = $Matches[1]
                # Log first column is UTC; anchor DATE must come from the
                # current UTC date, not local (local breaks after 20:00 local
                # when UTC has rolled to the next day - FRESH10 live proof:
                # staged=0 with elapsed_s=86403.9).
                $utcToday = ([datetime]::UtcNow).Date
                $parsed = [datetime]::ParseExact($utcToday.ToString('yyyy-MM-dd') + 'T' + $timeOnly, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                # The leading timestamp is UTC; ToUniversalTime is a no-op on a
                # Kind=Unspecified value, so stamp it explicitly.
                $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
                if ($asUtc -gt [datetime]::UtcNow) { $asUtc = $asUtc.AddDays(-1) }
                $markerUtc = $asUtc.ToString('o')
                Write-Log ("marker_found log=" + $log.Name + " line=" + $line.Trim() + " -> utc=" + $markerUtc)
                break
            }
        }
    }
    if ($markerUtc) { break }
}
if (-not $markerUtc) {
    Write-Log 'FAILED_no_marker'
    exit 2
}

# 3. Run M1 anchored to the marker, slim staging, auto write-trace on verdict.
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
