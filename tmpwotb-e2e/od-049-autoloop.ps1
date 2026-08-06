[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$ReplayPath = '.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay',
    [int]$MaxReadRounds = 70,
    [int]$StageTopN = 2,
    [int]$StageDelaySeconds = 2,
    [string]$ResultPath = '',
    # M2 pre-flight gate (FRESH9/FRESH14 chunk 2): after the first monitor
    # round proves the game readable, od-048 runs the x64dbg attach-smoke
    # against the LIVE game (hex attach -> pause -> verify -> bpm -> detach
    # -> verify resume) and fails closed (exit 6) on a red smoke BEFORE the
    # correlate + trace window is spent. The live round must run with this on.
    [switch]$AttachSmokeOnFirstRound,
    # Auto-trace green-window seconds (passed through to the auto-invoked
    # x64dbg-write-trace.ps1). Budget from the choreography table: 70 rounds
    # leaves ~31s on Dead Rail; 25 is the recommended first attempt.
    [int]$AutoTraceSeconds = 25,
    # Viewpoint-first pivot (passed through to od-048): stage ONLY the
    # viewpoint player and trace its first strong survivor - no top movers,
    # no XYZ family assembly, no alternate-entity decoys.
    [switch]$StageViewpointOnly
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

$markerUtc = $null
foreach ($log in $logs) {
    $lines = @(Get-Content -LiteralPath $log.FullName -Tail 400 -ErrorAction SilentlyContinue)
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
Write-Log ("running M1 anchored=" + $markerUtc + " rounds=" + $MaxReadRounds + " topN=" + $StageTopN + " attachSmoke=" + $AttachSmokeOnFirstRound.IsPresent)
# Hashtable splat (NOT array splatting of '-Name value' pairs, which
# misaligns argument binding around switches - the exact failure od-048's
# own wtArgs comment documents). Switches are added conditionally.
$m1Args = @{
    ReplayStartWallTimeUtc = $markerUtc
    MaxReadRounds          = $MaxReadRounds
    StageTopN              = $StageTopN
    StageDelaySeconds      = $StageDelaySeconds
    AutoWriteTraceOnVerdict = $true
    AutoTraceSeconds       = $AutoTraceSeconds
    ResultPath             = $ResultPath
}
if ($AttachSmokeOnFirstRound) { $m1Args.AttachSmokeOnFirstRound = $true }
if ($StageViewpointOnly) { $m1Args.StageViewpointOnly = $true }
& (Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1') @m1Args *>&1 | ForEach-Object { Write-Log ("m1: " + $_) }
$m1Exit = $LASTEXITCODE
Write-Log ("m1_exit=" + $m1Exit)
exit $m1Exit
