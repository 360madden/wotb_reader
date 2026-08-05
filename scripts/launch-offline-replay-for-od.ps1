#Requires -Version 5.1
<#
.SYNOPSIS
  Canonical OD offline-replay launch: folder .wotbreplay -> import -> managed launch -> Watch Offline.

.DESCRIPTION
  Owner-proven source of truth for which replay to play is:
    %LOCALAPPDATA%\wotblitz\DAVAProject\replays\*.wotbreplay
  (top-level originals only -- never wotbtreader-staging\ GUID copies).

  Managed launch stages under:
    ...\replays\wotbtreader-staging\{guid}.wotbreplay
  so temporary copies are not mixed with originals. Flat GUID clones the game
  may drop into the parent replays folder are scavenged on stage dispose.

  Flaw this script replaces:
  - File-association alone can play a replay, but Host.Web never receives managed
    lifecycle evidence, so the gate stays Denied/Unknown and discover APIs refuse.
  - Reusing a Host already in Denied (lifecycle_evidence_timeout) blocks the next attempt.
  - Stale capability tokens (~5 min rendezvous lease) cause 401 if not re-read.
  - Launching a replay before the game UI is ready drops into the hangar.

  Correct sequence (this script):
  1. Stop stale wotblitz / Host.Web / CE (clear Denied).
  2. Start Host.Web with research lease (evidence 120s / lifecycle 120s).
  3. Pick a .wotbreplay from the game replays folder (or -ReplayPath).
  4. Import via CLI -> content-addressed artifact id.
  5. POST /api/v1/game/launch (managed) with a freshly read capability.
  6. Wait for window + settle so WATCH OFFLINE can appear.
  7. Run scripts/click-watch-offline.ps1 (dual: OfflineReplayVerified + dialog gone).

  Never logs private full paths, tokens, or account ids. Basename + sha12 only.

.EXITCODES
  0  OfflineReplayVerified after Watch Offline
  1  Missing replay / CLI / host
  2  Managed launch failed
  3  Game window never appeared
  4  Watch Offline script failed
  5  Unexpected error
#>
[CmdletBinding()]
param(
    [string]$ReplayPath,
    [string]$RepoRoot,
    # Hands-off then soft-focus after first HWND (splash dies if churned).
    [int]$SettleSeconds = 8,
    [int]$HostWaitSeconds = 60,
    [int]$WindowWaitSeconds = 90,
    [int]$WatchTimeoutSeconds = 120,
    [switch]$SkipWatchOffline,
    [switch]$KeepExistingHost
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

function Write-Od([string]$Message) {
    Write-Host ("od_launch: " + $Message)
}

function Get-Rendezvous {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
}

function Get-ApiContext {
    $rv = Get-Rendezvous
    return @{
        Base    = [string]$rv.baseUri
        Headers = @{
            'X-WotBTreader-Capability' = "$($rv.capability)"
            'Content-Type'             = 'application/json'
        }
    }
}

function Wait-Port([int]$Port, [int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(250, $false) -and $c.Connected) {
                $c.Close()
                return $true
            }
            try { $c.Close() } catch { Write-Verbose "od-launch: port probe close failed: $($_.Exception.Message)" }
        }
        catch { Write-Verbose "od-launch: port probe failed: $($_.Exception.Message)" }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Stop-OdProcesses {
    Get-Process -Name wotblitz, 'WotBTreader.Host.Web' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'Host\.Web' } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Seconds 2
}

# Soft focus only (SetForegroundWindow). ShowWindow/keybd_event during splash
# correlated with become hidden -> WindowDestroyed in live blitz-logs.
Add-Type -Namespace OdLaunch -Name Focus -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);
public static void Soft(System.IntPtr h) { SetForegroundWindow(h); }
"@ -ErrorAction SilentlyContinue

function Wait-GameSettle([int]$Seconds) {
    # Hands-off for the first half of settle (splash is fragile), then soft-focus.
    $handsOff = [Math]::Max(2, [int][Math]::Floor($Seconds / 2))
    $focusSecs = [Math]::Max(0, $Seconds - $handsOff)
    Write-Od ("settle_hands_off_${handsOff}s")
    for ($i = 0; $i -lt $handsOff; $i++) {
        $g = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if (-not $g) {
            Write-Od 'game_window_lost_during_hands_off_settle'
            return $false
        }
        Start-Sleep -Seconds 1
    }
    if ($focusSecs -gt 0) {
        Write-Od ("settle_soft_focus_${focusSecs}s")
        $deadline = (Get-Date).AddSeconds($focusSecs)
        while ((Get-Date) -lt $deadline) {
            $g = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
                Select-Object -First 1
            if (-not $g) {
                Write-Od 'game_window_lost_during_soft_focus_settle'
                return $false
            }
            try { [OdLaunch.Focus]::Soft($g.MainWindowHandle) } catch { Write-Verbose "od-launch: soft focus failed: $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 500
        }
    }
    return $true
}

try {
    $replaysDir = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject\replays'
    $stagingDir = Join-Path $replaysDir 'wotbtreader-staging'
    if ([string]::IsNullOrWhiteSpace($ReplayPath)) {
        # Top-level originals only: never recurse into wotbtreader-staging, and
        # prefer human-named files over GUID stage leftovers in the flat list.
        $candidates = @(Get-ChildItem -LiteralPath $replaysDir -Filter '*.wotbreplay' -File -ErrorAction SilentlyContinue)
        $originals = @($candidates | Where-Object {
            $_.Name -notmatch '^[0-9a-fA-F]{32}\.wotbreplay$'
        })
        $pickFrom = if ($originals.Count -gt 0) { $originals } else { $candidates }
        $replay = $pickFrom | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $replay) {
            Write-Od 'FAILED_no_wotbreplay_in_game_folder'
            exit 1
        }
        $ReplayPath = $replay.FullName
    }
    elseif (-not (Test-Path -LiteralPath $ReplayPath)) {
        Write-Od 'FAILED_replay_path_missing'
        exit 1
    }

    # Refuse launching a path that lives inside the staging folder as "source of truth".
    $fullReplay = [IO.Path]::GetFullPath($ReplayPath)
    if ($fullReplay.StartsWith([IO.Path]::GetFullPath($stagingDir) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Od 'FAILED_replay_is_staging_copy_use_original'
        exit 1
    }

    $replayItem = Get-Item -LiteralPath $ReplayPath
    if ($replayItem.Extension -ne '.wotbreplay') {
        Write-Od 'FAILED_not_wotbreplay'
        exit 1
    }

    $sha12 = ((Get-FileHash -Algorithm SHA256 -LiteralPath $replayItem.FullName).Hash).Substring(0, 12)
    Set-Content -Path (Join-Path $env:TEMP 'od-launch-replay.basename') -Value $replayItem.Name -NoNewline
    Set-Content -Path (Join-Path $env:TEMP 'od-launch-replay.sha12') -Value $sha12 -NoNewline
    Write-Od ("replay=" + $replayItem.Name + " bytes=" + $replayItem.Length + " sha12=" + $sha12)

    $cli = Join-Path $RepoRoot 'src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli)) {
        Write-Od 'FAILED_cli_missing_build_release_first'
        exit 1
    }

    # Stale-build guard (2026-08-05). The host is started below with `dotnet
    # run --no-build` from bin\Release, so a Release build older than the newest
    # source silently runs WITHOUT any endpoints added since that build (the
    # Jul-31-class failure: the trajectory/correlate endpoints 404'd against the
    # stale build and a CAP-2 session was at risk). Fail fast BEFORE any host
    # launch instead of wasting a live session on a host that cannot serve the
    # campaign. `dotnet build -c Release` (or serve.cmd, which republishes)
    # fixes it.
    $hostDll = Join-Path $RepoRoot 'src\WotBTreader.Host.Web\bin\Release\net10.0\WotBTreader.Host.Web.dll'
    if (-not (Test-Path -LiteralPath $hostDll)) {
        Write-Od 'FAILED_host_missing_build_release_first'
        exit 1
    }
    $newestSource = Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $newestSource -and $newestSource.LastWriteTime -gt (Get-Item -LiteralPath $hostDll).LastWriteTime) {
        Write-Od ('FAILED_host_stale_build rebuild_release_first newer=' + $newestSource.Name + ' dll=' + $hostDll)
        exit 1
    }

    if (-not $KeepExistingHost) {
        Write-Od 'stopping_stale_game_and_host'
        Stop-OdProcesses
    }

    if (-not (Wait-Port -Port 9182 -Seconds 2)) {
        Write-Od 'starting_host_research_lease'
        $env:Research__OfflineReplayEvidenceLifetimeSeconds = '120'
        $env:Research__LifecycleEvidenceTimeoutSeconds = '120'
        $proj = Join-Path $RepoRoot 'src\WotBTreader.Host.Web'
        $hostOut = Join-Path $env:TEMP 'od-launch-host.log'
        $hostErr = Join-Path $env:TEMP 'od-launch-host.err.log'
        Start-Process -FilePath 'dotnet' -ArgumentList @(
            'run', '--project', $proj, '-c', 'Release', '--no-build', '--no-launch-profile'
        ) -WorkingDirectory $proj -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr -WindowStyle Hidden |
            Out-Null
        if (-not (Wait-Port -Port 9182 -Seconds $HostWaitSeconds)) {
            Write-Od 'FAILED_host_down'
            exit 1
        }
    }
    Write-Od 'host_ok'

    Write-Od 'importing'
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $importLines = & $cli import $replayItem.FullName 2>&1 | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
    $importText = ($importLines | ForEach-Object { "$_" }) -join "`n"
    if ($importText -notmatch '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
        Write-Od 'FAILED_import_parse'
        exit 1
    }
    # Prefer the explicit "Imported artifact <guid>" line when present.
    if ($importText -match 'Imported artifact\s+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
        $artifactId = $Matches[1]
    }
    else {
        $artifactId = $Matches[1]
    }
    Set-Content -Path (Join-Path $env:TEMP 'od-launch-artifact.id') -Value $artifactId -NoNewline
    Write-Od ("artifact_prefix=" + $artifactId.Substring(0, 8))

    # Always re-read capability immediately before launch (rendezvous rotates ~5 min).
    $api = Get-ApiContext
    $body = @{ sourceArtifactId = $artifactId } | ConvertTo-Json
    Write-Od 'managed_launch'
    try {
        $launch = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/launch" -Method Post -Headers $api.Headers -Body $body
    }
    catch {
        Write-Od ("FAILED_launch_http=" + $_.Exception.Message)
        exit 2
    }
    if (-not $launch.success) {
        Write-Od ("FAILED_launch=" + $launch.message)
        exit 2
    }
    Write-Od ("launch=" + $launch.message)

    $game = $null
    for ($i = 0; $i -lt $WindowWaitSeconds; $i++) {
        $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if ($game) {
            Write-Od ("window_after=${i}s pid=" + $game.Id)
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $game) {
        Write-Od 'FAILED_no_window'
        exit 3
    }

    Write-Od 'watch_offline_sync_dim_ready_then_click'
    # Short settle only (default 4s) so splash can finish; the clicker owns the
    # sync-dim ready gate and must not spam focus during LookingForDialog.

    $api = Get-ApiContext
    $pre = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/state" -Headers $api.Headers
    Write-Od ("pre_watch_vs=" + $pre.verificationState + " reason=" + $pre.reasonCode)

    if ($pre.verificationState -eq 'Denied') {
        Write-Od 'FAILED_host_denied_before_watch_restart_required'
        exit 2
    }

    if ($SkipWatchOffline) {
        Write-Od 'skip_watch_offline'
        exit 0
    }

    if ($SettleSeconds -gt 0) {
        Write-Od ("optional_settle_${SettleSeconds}s")
        if (-not (Wait-GameSettle -Seconds $SettleSeconds)) {
            Write-Od 'FAILED_game_died_during_settle'
            exit 3
        }
    }

    $watchScript = Join-Path $RepoRoot 'scripts\click-watch-offline.ps1'
    # In-process invoke via ResultPath: nested consoles either steal focus
    # (OnBackground / dialog dismiss) or, when Hidden, fail to inject clicks.
    $watchResult = Join-Path $env:TEMP 'od-watch-offline.exit.txt'
    Remove-Item -LiteralPath $watchResult -Force -ErrorAction SilentlyContinue
    $watchExit = 99
    try {
        & $watchScript -TimeoutSeconds $WatchTimeoutSeconds -ResultPath $watchResult
        if (Test-Path -LiteralPath $watchResult) {
            $watchExit = [int](Get-Content -LiteralPath $watchResult -Raw)
        }
        else {
            $watchExit = 0
        }
    }
    catch {
        $msg = [string]$_.Exception.Message
        if ($msg -match 'WATCH_EXIT:(\d+)') {
            $watchExit = [int]$Matches[1]
        }
        elseif (Test-Path -LiteralPath $watchResult) {
            $watchExit = [int](Get-Content -LiteralPath $watchResult -Raw)
        }
        else {
            Write-Od ("watch_unexpected=" + $msg)
            $watchExit = 4
        }
    }
    Write-Od ("watch_exit=" + $watchExit)
    if ($watchExit -ne 0) {
        exit 4
    }

    $api = Get-ApiContext
    $post = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/state" -Headers $api.Headers
    Write-Od ("post_watch_vs=" + $post.verificationState + " reason=" + $post.reasonCode)
    if ($post.verificationState -ne 'OfflineReplayVerified') {
        Write-Od 'FAILED_gate_not_verified'
        exit 4
    }

    Write-Od 'OK OfflineReplayVerified'
    exit 0
}
catch {
    Write-Od ("FAILED_unexpected=" + $_.Exception.Message)
    exit 5
}
