[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$ReplayPath = '.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay',
    # FRESH21: 50 rounds (was 70) so the correlate + auto-trace fire with
    # more battle tail. FRESH20's 70 rounds pushed the trace start to 11s
    # before battle end (STOP_gate=Denied, exit 5); 50 rounds saves ~40s and
    # the trace window is separately capped to the tail by od-048.
    [int]$MaxReadRounds = 50,
    [int]$StageTopN = 2,
    [int]$StageDelaySeconds = 2,
    # FRESH15i: wait until the match officially begins (loading + attendance
    # is ~50s) before the FIRST staging scan, so the staged sample value
    # matches the live in-battle position field instead of staging decoys
    # that hold the spawn-era value. Mirrors the attach-smoke gate.
    [double]$StageMinBattleSeconds = 55.0,
    # FRESH18: loading + all-players-in-attendance lag between the Start
    # marker and the decoded trajectory's tick 0 (match begin). The staging
    # scan targets tick (elapsed - attendance) and the correlate maps
    # wall->tick from (marker + attendance), so the needed shift is ~0.
    [double]$AttendanceLatencySeconds = 50.0,
    # FRESH19: correlate shift-sweep half-width (passed through). FRESH18
    # proved the 50s attendance estimate is per-replay -- the z axis wanted a
    # shift ~20-30s beyond the old 30s sweep. 90s lets the scorer REACH the
    # true shift so the band-floor gate can judge it honestly instead of
    # refusing everything as edge-aligned.
    [int]$MaxTimeShiftSeconds = 90,
    # FRESH19: the offline replay viewer auto-loops after the battle ends; the
    # second LoadGameScene reload hits the OD-044-class flake (frozen roster
    # screen, 'Not Responding'). After the campaign concludes (auto-trace, if
    # any, already ran in-process inside M1), the game is stopped so the
    # operator is never left with a frozen window. -KeepGame opts out.
    [switch]$KeepGame,
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
    [switch]$StageViewpointOnly,
    # FRESH15g: the attach-smoke's detach-while-paused leaves the game
    # permanently frozen ~1/3 of the time (x64dbg/WOW64 limitation - `run` is
    # broken in both command-bar and script channels, so the debuggee can only
    # resume via the detach cleanup, which is flaky). On an exit-6 smoke
    # failure the whole campaign is relaunched (the launch script kills the
    # stale game+host) up to this many total attempts.
    [int]$MaxCampaignAttempts = 3
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
    # The anchor DATE comes from the LOG FILE's LastWriteTime, not the wall
    # clock and NOT the filename: the filename embeds the GAME's local time
    # (blitz-logs_YYYYMMDDHHMMSS.txt is written at the game's own UTC offset,
    # e.g. 19:12:46 at -5 = 00:12:46Z the NEXT day). Converting that
    # game-local timestamp with the OS timezone (UTC-4 here) lands an hour
    # off - which crosses the UTC midnight boundary and anchored FRESH31's
    # 00:13Z marker to the PREVIOUS day (elapsed_s=86408.2 ->
    # staging_budget_exhausted -> session burned before staging). The file is
    # written continuously by the live game, so LastWriteTime.ToUniversalTime()
    # is OS-correct AND current; the filename date is only a fallback.
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        try {
            $item = Get-Item -LiteralPath $LogPath -ErrorAction Stop
            return ($item.LastWriteTime).ToUniversalTime().Date
        }
        catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($LogPath) -and (Split-Path $LogPath -Leaf) -match 'blitz-logs_(\d{8})(\d{6})') {
        try {
            $fileLocal = [datetime]::ParseExact($Matches[1] + ' ' + $Matches[2], 'yyyyMMdd HHmmss', [Globalization.CultureInfo]::InvariantCulture)
            return ([datetime]::SpecifyKind($fileLocal, [DateTimeKind]::Local)).ToUniversalTime().Date
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
                # FRESH31: bound the future-rollback to >60s (a sub-minute
                # extraction race is clock skew, not a yesterday marker), and
                # only accept a marker written within the last 120s so a
                # failed-click fallback to an OLD session's log cannot anchor
                # the run (fail-closed: FAILED_no_marker instead of a burn).
                if ($asUtc -gt [datetime]::UtcNow.AddSeconds(60)) { $asUtc = $asUtc.AddDays(-1) }
                if ($asUtc -lt [datetime]::UtcNow.AddSeconds(-120)) { continue }
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

# 3. Run M1 anchored to the marker, slim staging, auto write-trace on
#    verdict. FRESH15g: wrapped in a campaign loop - an exit-6 attach-smoke
#    failure (game left permanently frozen by the detach) relaunches the whole
#    session; the launch script's own stale-game+host kill cleans up first.
$m1Exit = 0
for ($attempt = 1; $attempt -le $MaxCampaignAttempts; $attempt++) {
    Write-Log ("campaign attempt=" + $attempt + "/" + $MaxCampaignAttempts)
    if ($attempt -gt 1) {
        # Relaunch (kills the frozen game + host from the failed attempt).
        & (Join-Path $RepoRoot 'scripts\launch-offline-replay-for-od.ps1') -ReplayPath $ReplayPath *>&1 |
            ForEach-Object { Write-Log ("relaunch: " + $_) }
        $launchExit = $LASTEXITCODE
        Write-Log ("relaunch_exit=" + $launchExit)
        if ($launchExit -ne 0) { exit $launchExit }
        # FRESH19/20: the relaunched game writes a NEW blitz log + a NEW 'Start
        # replay event' marker DURING the launch script's watch/click phase --
        # BEFORE the script returns -- so a time bound against the relaunch
        # start wrongly rejects the CURRENT marker (FRESH20 regression). The
        # discriminator is the marker's AGE: the current replay's marker is
        # seconds old; a previous attempt's is minutes old. Also scan the WHOLE
        # file: the marker sits near the top of the game's log and leaves a
        # -Tail 400 window once the log grows. Re-enumerate logs and poll up to
        # ~40s for a marker written within the last 120s.
        $markerUtc = $null
        for ($wait = 0; $wait -lt 20 -and -not $markerUtc; $wait++) {
            $logs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\blitz-logs_*.txt') -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
            foreach ($log in $logs) {
                $lines = @(Get-Content -LiteralPath $log.FullName -ErrorAction SilentlyContinue)
                for ($i = $lines.Count - 1; $i -ge 0; $i--) {
                    $line = [string]$lines[$i]
                    if ($line -match 'START_REPLAY_LOCAL|Start replay event') {
                        if ($line -match '^(\d{2}:\d{2}:\d{2})') {
                            $timeOnly = $Matches[1]
                            $anchorDateUtc = Get-LogAnchorDateUtc -LogPath $log.FullName
                            $parsed = [datetime]::ParseExact($anchorDateUtc.ToString('yyyy-MM-dd') + 'T' + $timeOnly, 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                            $asUtc = [datetime]::SpecifyKind($parsed, [DateTimeKind]::Utc)
                            # FRESH31: bound the future-rollback to >60s (clock
                            # skew vs yesterday-marker disambiguation).
                            if ($asUtc -gt [datetime]::UtcNow.AddSeconds(60)) { $asUtc = $asUtc.AddDays(-1) }
                            # Only accept a RECENT marker (the current replay
                            # start): anything older than 120s is a previous
                            # attempt's stale anchor.
                            if ($asUtc -lt [datetime]::UtcNow.AddSeconds(-120)) { continue }
                            $markerUtc = $asUtc.ToString('o')
                            Write-Log ("marker_found(relaunch) log=" + $log.Name + " -> utc=" + $markerUtc)
                            break
                        }
                    }
                }
                if ($markerUtc) { break }
            }
            if (-not $markerUtc -and $wait -lt 19) { Start-Sleep -Seconds 2 }
        }
        if (-not $markerUtc) {
            Write-Log 'FAILED_no_marker(relaunch)'
            exit 2
        }
    }
    Write-Log ("running M1 anchored=" + $markerUtc + " rounds=" + $MaxReadRounds + " topN=" + $StageTopN + " attachSmoke=" + $AttachSmokeOnFirstRound.IsPresent)
    # Hashtable splat (NOT array splatting of '-Name value' pairs, which
    # misaligns argument binding around switches - the exact failure od-048's
    # own wtArgs comment documents). Switches are added conditionally.
    $m1Args = @{
        ReplayStartWallTimeUtc = $markerUtc
        MaxReadRounds          = $MaxReadRounds
        StageTopN              = $StageTopN
        StageDelaySeconds      = $StageDelaySeconds
        StageMinBattleSeconds  = $StageMinBattleSeconds
        AttendanceLatencySeconds = $AttendanceLatencySeconds
        AutoWriteTraceOnVerdict = $true
        AutoTraceSeconds       = $AutoTraceSeconds
        ResultPath             = $ResultPath
    }
    if ($AttachSmokeOnFirstRound) { $m1Args.AttachSmokeOnFirstRound = $true }
    if ($StageViewpointOnly) { $m1Args.StageViewpointOnly = $true }
    $m1Args.MaxTimeShiftSeconds = $MaxTimeShiftSeconds
    & (Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1') @m1Args *>&1 | ForEach-Object { Write-Log ("m1: " + $_) }
    $m1Exit = $LASTEXITCODE
    Write-Log ("m1_exit=" + $m1Exit)
    if ($m1Exit -ne 6) { break }
    if ($attempt -lt $MaxCampaignAttempts) {
        Write-Log ("campaign attempt " + $attempt + " failed with exit 6 (attach-smoke; game likely frozen by the detach) - relaunching")
    }
}
# FRESH19: the replay viewer loops after the battle ends and the second
# LoadGameScene reload hits the OD-044-class flake (frozen roster). M1's
# auto-trace (if any) already ran in-process before this point, so stopping
# the game now cannot lose evidence -- it only prevents the frozen-window
# end-state the operator keeps hitting.
if (-not $KeepGame) {
    Write-Log 'stopping game after campaign (replay-loop flake prevention)'
    Stop-Process -Name 'wotblitz' -Force -ErrorAction SilentlyContinue
}
exit $m1Exit
