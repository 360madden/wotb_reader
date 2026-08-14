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
  4. Probe the replay's client version vs the installed game family; fail
     fast (FAILED_replay_client_version_mismatch) if they differ -- the game
     refuses mismatched replays with "Replay Error code: 126" (a client-
     version mismatch, NOT slow clicks; supersedes the pre-2026-08-12
     attribution in the specs).
  5. Import via CLI -> content-addressed artifact id.
  6. POST /api/v1/game/launch (managed) with a freshly read capability.
  7. Wait for window + settle so WATCH OFFLINE can appear.
  8. Run scripts/click-watch-offline.ps1 (dual: OfflineReplayVerified + dialog gone).

  Never logs private full paths, replay hashes, tokens, or account ids.

.EXITCODES
  0  OfflineReplayVerified after Watch Offline
  1  Missing replay / CLI / host
  2  Managed launch failed / replay already completed (FAILED_replay_already_completed)
  3  Game window never appeared
  4  Watch Offline script failed / replay already completed (FAILED_replay_already_completed)
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
    [switch]$KeepExistingHost,
    # Instruction-first position discovery is opt-in. The helper must already
    # be a fresh self-contained win-x86 publish; this script pins its exact
    # hash into the new Host.Web process without logging the path or hash.
    [switch]$EnableInstructionSnapshot,
    # FRESH17: shrink the game window after the game settles so it never
    # covers the operator's other programs (a covering window steals the
    # foreground lock and swallows the dialog click - the recurring
    # OD-044/FRESH16 focus class). Default 640x360 placed at the top-left
    # corner of the SECOND monitor when one is attached (see
    # -NoSecondMonitorPlacement), else the primary's top-left, applied ONCE
    # after the settle (the splash is fragile; SW_RESTORE churn during
    # LoginOnReplay correlated with OnBackground). The clicker auto-scales
    # its pixel thresholds from the captured window size, so the ready gate
    # still fires at the small size. -NoResizeWindow opts out entirely.
    [int]$ResizeWindowWidth = 640,
    [int]$ResizeWindowHeight = 360,
    [int]$ResizeWindowX = 0,
    [int]$ResizeWindowY = 0,
    [switch]$NoResizeWindow,
    # Keep the resized window at the caller's X/Y (default: primary display
    # top-left) even when a second monitor is attached. The default prefers
    # the second monitor's top-left when one exists.
    [switch]$NoSecondMonitorPlacement
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

# Persisted replay-completion marker (OD-099 durable fix): the cross-session
# gate denial evidence.replay_completed is in-memory and dies with the game
# process, so the pre-flight consults a marker file keyed to the replay's
# immutable fingerprint instead.
. (Join-Path $scriptDir 'od-replay-completion.ps1')

# Replay-selection + staging-refusal helpers (pure path logic, Pester-pinned).
. (Join-Path $scriptDir 'od-replay-selection.ps1')

if ($EnableInstructionSnapshot -and $KeepExistingHost) {
    Write-Host 'od_launch: FAILED_instruction_snapshot_requires_new_host'
    exit 1
}

function Write-Od([string]$Message) {
    Write-Host ("od_launch: " + $Message)
}

# Owner-only ACL helpers (Test/Set-OdOwnerOnly*) are dot-sourced from
# od-replay-completion.ps1 above; the launch marker reuses them so the icacls
# + reparse-point ACL logic has exactly one definition (dedupe of the former
# local Test/Set-OwnerOnly* copies).

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

# FRESH17: window-resize P/Invoke (SetWindowPos), a SEPARATE type from
# OdLaunch.Focus so an in-process autoloop relaunch never hits a stale-type
# guard (same failure class as the clicker's V2->V3 rename). Guarded with the
# -as [type] check for the same reason.
if (-not ('OdLaunch.WindowResize' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace OdLaunch {
    public static class WindowResize {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int SW_RESTORE = 9;
        // DPI-aware + synchronous (no SWP_ASYNCWINDOWPOS): the caller reads the
        // rect back immediately after, so the resize must be applied before we
        // return, and physical-pixel coords on scaled displays.
        public static bool Resize(IntPtr h, int w, int hgt, int x, int y) {
            SetProcessDPIAware();
            return SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        public static bool Restore(IntPtr h) { return ShowWindow(h, SW_RESTORE); }
    }
}
'@ -ErrorAction Stop
}

# Second-monitor placement for the resized game window (FRESH17 follow-on): a
# SEPARATE type from OdLaunch.WindowResize so an in-process autoloop relaunch
# never hits a stale-type guard (same pattern as WindowResize vs Focus). Pure
# Win32 interop - the script does not load System.Windows.Forms.
if (-not ('OdLaunch.MonitorTarget' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace OdLaunch {
    public static class MonitorTarget {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        public const uint MONITORINFOF_PRIMARY = 1;
        // Top-left of the first NON-primary monitor's work area (excludes the
        // taskbar), in physical pixels once SetProcessDPIAware is active.
        // Returns false when only the primary monitor is attached.
        public static bool TryGetSecondMonitorTopLeft(out int x, out int y) {
            x = 0;
            y = 0;
            SetProcessDPIAware();
            bool found = false;
            int foundX = 0;
            int foundY = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (GetMonitorInfo(hMon, ref mi)) {
                    if ((mi.dwFlags & MONITORINFOF_PRIMARY) == 0) {
                        foundX = mi.rcWork.Left;
                        foundY = mi.rcWork.Top;
                        found = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            x = foundX;
            y = foundY;
            return found;
        }
    }
}
'@ -ErrorAction Stop
}

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
        $replay = Select-OdReplay -ReplaysDir $replaysDir
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
    if (Test-OdReplayIsStagingCopy -ReplayPath $ReplayPath -StagingDir $stagingDir) {
        Write-Od 'FAILED_replay_is_staging_copy_use_original'
        exit 1
    }

    $replayItem = Get-Item -LiteralPath $ReplayPath
    if ($replayItem.Extension -ne '.wotbreplay') {
        Write-Od 'FAILED_not_wotbreplay'
        exit 1
    }

    Write-Od ("replay_selected bytes=" + $replayItem.Length)

    # Persisted completion marker (OD-099): if THIS replay (same fingerprint)
    # was already played to completion, fail fast instead of re-importing +
    # re-launching it. The replay files are immutable in this workflow, so a
    # matching fingerprint is authoritative; a replaced/re-imported file
    # (fingerprint mismatch) is treated as a fresh replay.
    if (Test-OdReplayCompleted -ReplayPath $ReplayPath) {
        Write-Od 'FAILED_replay_already_completed'
        exit 2
    }

    $cli = Join-Path $RepoRoot 'src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli)) {
        Write-Od 'FAILED_cli_missing_build_release_first'
        exit 1
    }

    # Replay-version guard (2026-08-12). The game refuses to play a replay
    # whose client-version family differs from the installed game with
    # "Replay Error code: 126" -- a recurring, session-wasting failure whose
    # earlier "slow clicks / sync-dim" attribution in the specs was WRONG
    # (root cause: the staged replay was 11.18.0 against an 11.19.0.10 game).
    # Probe BEFORE any host launch so a mismatched replay fails in seconds,
    # not after the full import + launch + Watch Offline dance.
    # The envelope is the ONLY thing the CLI writes to stdout with --json
    # (all logs go to stderr, discarded here), so join every captured line.
    # PS 5.1: under $ErrorActionPreference='Stop' the CLI's stderr log lines
    # become a terminating RemoteException the moment the probe starts (the
    # first launch that exercised this guard hit it as FAILED_unexpected
    # before any probe output). Drop EAP for the native call only; the
    # envelope parse below still fails closed on any real error.
    $probeOldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $probeOut = & $cli probe $replayItem.FullName --json 2>$null | ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $probeOldEap
    }
    $probeJson = ($probeOut -join "`n")
    if (-not $probeJson) {
        Write-Od 'FAILED_replay_probe_unreadable'
        exit 1
    }
    try {
        $probe = $probeJson | ConvertFrom-Json
    }
    catch {
        Write-Od 'FAILED_replay_probe_parse'
        exit 1
    }
    if (-not $probe.success) {
        Write-Od 'FAILED_replay_probe'
        exit 1
    }
    if ($probe.data.compatible -eq $false) {
        Write-Od ("FAILED_replay_client_version_mismatch replay=" + $probe.data.gameVersion +
            " installed=" + $probe.data.installedGameVersion +
            " use_a_replay_from_the_installed_game_family")
        exit 1
    }
    Write-Od ("replay_probe_ok replay=" + $probe.data.gameVersion +
        " installed=" + $probe.data.installedGameVersion)

    # Stale-build guard (2026-08-05). The host is started below with `dotnet
    # run --no-build` from bin\Release, so a Release build older than the newest
    # source silently runs WITHOUT any endpoints added since that build (the
    # Jul-31-class failure: the trajectory/correlate endpoints 404'd against the
    # stale build and a CAP-2 session was at risk). Fail fast BEFORE any host
    # launch instead of wasting a live session on a host that cannot serve the
    # campaign. `dotnet build -c Release` (or serve.cmd, which republishes)
    # fixes it.
    $hostDll = Join-Path $RepoRoot 'src\WotBTreader.Host.Web\bin\Release\net10.0\WotBTreader.Host.Web.dll'
    $hostExe = Join-Path $RepoRoot 'src\WotBTreader.Host.Web\bin\Release\net10.0\WotBTreader.Host.Web.exe'
    if (-not (Test-Path -LiteralPath $hostDll) -or -not (Test-Path -LiteralPath $hostExe)) {
        Write-Od 'FAILED_host_missing_build_release_first'
        exit 1
    }

    $instructionSnapshotHelper = $null
    $instructionSnapshotHelperSha256 = $null
    if ($EnableInstructionSnapshot) {
        $instructionSnapshotHelper = Join-Path $RepoRoot '.build\publish\instruction-snapshot-helper\WotBTreader.InstructionSnapshotHelper.exe'
        $instructionSnapshotManifest = Join-Path $RepoRoot '.build\publish\instruction-snapshot-helper\identity.json'
        if (-not (Test-Path -LiteralPath $instructionSnapshotHelper) -or
            -not (Test-Path -LiteralPath $instructionSnapshotManifest)) {
            Write-Od 'FAILED_instruction_snapshot_helper_missing_publish_first'
            exit 1
        }
        if (-not (Test-OdOwnerOnlyFileAcl -Path $instructionSnapshotManifest)) {
            Write-Od 'FAILED_instruction_snapshot_helper_manifest_acl'
            exit 1
        }
        $helperSourcePaths = @(
            (Join-Path $RepoRoot 'tools\InstructionSnapshotHelper'),
            (Join-Path $RepoRoot 'tools\WriteInterceptor')
        )
        $newestHelperSource = Get-ChildItem -Path $helperSourcePaths -Recurse -File |
            Where-Object {
                $_.Extension -in '.cs', '.csproj' -and
                $_.FullName -notmatch '\\(?:bin|obj)\\'
            } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $newestHelperSource -and
            $newestHelperSource.LastWriteTime -gt (Get-Item -LiteralPath $instructionSnapshotHelper).LastWriteTime) {
            Write-Od 'FAILED_instruction_snapshot_helper_stale_republish_first'
            exit 1
        }
        try {
            $instructionSnapshotIdentity = Get-Content -LiteralPath $instructionSnapshotManifest -Raw | ConvertFrom-Json
        }
        catch {
            Write-Od 'FAILED_instruction_snapshot_helper_manifest_invalid'
            exit 1
        }
        $instructionSnapshotHelperSha256 = (Get-FileHash -LiteralPath $instructionSnapshotHelper -Algorithm SHA256).Hash
        $currentHostExeSha256 = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash
        $currentHostDllSha256 = (Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash
        if ([string]$instructionSnapshotIdentity.schema -ne 'wotbtreader.instruction-snapshot-helper.identity.v1' -or
            [string]$instructionSnapshotIdentity.helperFile -ne 'WotBTreader.InstructionSnapshotHelper.exe' -or
            [string]$instructionSnapshotIdentity.helperSha256 -ne $instructionSnapshotHelperSha256 -or
            [string]$instructionSnapshotIdentity.coordinatorExeSha256 -ne $currentHostExeSha256 -or
            [string]$instructionSnapshotIdentity.coordinatorAssemblySha256 -ne $currentHostDllSha256) {
            Write-Od 'FAILED_instruction_snapshot_helper_manifest_mismatch'
            exit 1
        }
        $verificationNonce = [Guid]::NewGuid().ToString('N')
        $verificationLines = @(& $instructionSnapshotHelper '--verify-coordinator-file' '-Path' $hostExe `
            '-AssemblyPath' $hostDll '-Nonce' $verificationNonce 2>&1 | ForEach-Object { "$_" })
        $verificationExit = $LASTEXITCODE
        try {
            $verification = (($verificationLines -join "`n") | ConvertFrom-Json)
        }
        catch {
            $verification = $null
        }
        if ($verificationExit -ne 0 -or $null -eq $verification -or
            [string]$verification.schema -ne 'wotbtreader.instruction-snapshot-helper.verify.v1' -or
            [string]$verification.nonce -ne $verificationNonce -or
            -not [bool]$verification.verified) {
            Write-Od 'FAILED_instruction_snapshot_helper_host_identity_mismatch'
            exit 1
        }
        $instructionSnapshotHelperSha256 = [string]$instructionSnapshotIdentity.helperSha256
    }
    $newestSource = Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -File |
        Where-Object {
            $_.Extension -in '.cs', '.csproj' -and
            $_.FullName -notmatch '\\(?:bin|obj)\\'
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $newestSource -and $newestSource.LastWriteTime -gt (Get-Item -LiteralPath $hostDll).LastWriteTime) {
        Write-Od ('FAILED_host_stale_build rebuild_release_first newer=' + $newestSource.Name)
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
        if ($EnableInstructionSnapshot) {
            $env:Research__InstructionSnapshotHelperPath = $instructionSnapshotHelper
            $env:Research__InstructionSnapshotHelperSha256 = $instructionSnapshotHelperSha256
        }
        $hostDirectory = Split-Path -Parent $hostExe
        $hostOut = Join-Path $env:TEMP 'od-launch-host.log'
        $hostErr = Join-Path $env:TEMP 'od-launch-host.err.log'
        Start-Process -FilePath $hostExe -WorkingDirectory $hostDirectory `
            -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr -WindowStyle Hidden |
            Out-Null
        if ($EnableInstructionSnapshot) {
            Remove-Item Env:\Research__InstructionSnapshotHelperPath -ErrorAction SilentlyContinue
            Remove-Item Env:\Research__InstructionSnapshotHelperSha256 -ErrorAction SilentlyContinue
        }
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
    $legacyMarker = Join-Path $env:TEMP 'od-launch-artifact.id'
    Remove-Item -LiteralPath $legacyMarker -Force -ErrorAction SilentlyContinue
    $launchMarkerDirectory = Join-Path $env:LOCALAPPDATA 'WotBTreader\od-launch'
    if (-not (Test-Path -LiteralPath $launchMarkerDirectory)) {
        New-Item -ItemType Directory -Path $launchMarkerDirectory | Out-Null
    }
    Set-OdOwnerOnlyDirectoryAcl -Path $launchMarkerDirectory
    if (-not (Test-OdOwnerOnlyDirectoryAcl -Path $launchMarkerDirectory)) {
        Write-Od 'FAILED_launch_marker_directory_acl'
        exit 1
    }
    $launchMarker = Join-Path $launchMarkerDirectory 'artifact.id'
    Remove-Item -LiteralPath $launchMarker -Force -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText(
        $launchMarker,
        $artifactId,
        (New-Object Text.UTF8Encoding($false)))
    Set-OdOwnerOnlyFileAcl -Path $launchMarker
    if (-not (Test-OdOwnerOnlyFileAcl -Path $launchMarker)) {
        Remove-Item -LiteralPath $launchMarker -Force -ErrorAction SilentlyContinue
        Write-Od 'FAILED_launch_marker_acl'
        exit 1
    }
    Write-Od 'artifact_imported'

    # Always re-read capability immediately before launch (rendezvous rotates ~5 min).
    $api = Get-ApiContext
    $body = @{ sourceArtifactId = $artifactId } | ConvertTo-Json
    Write-Od 'managed_launch'
    try {
        $launch = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/launch" -Method Post -Headers $api.Headers -Body $body
    }
    catch {
        Write-Od 'FAILED_launch_http'
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
        # Distinguishable completion: a gate denied with evidence.replay_completed
        # means the replay ALREADY finished (results screen observed) - a clean,
        # expected terminal state, not a broken host. Same reason the driver's
        # pre-flight exits 4; chains must not read this as 'restart required'.
        if ($pre.reasonCode -eq 'evidence.replay_completed') {
            # In-window belt-and-suspenders: persist the marker so a later
            # pre-flight (after this host/process is gone) also fails fast.
            [void](Write-OdCompletionMarker -ReplayPath $ReplayPath -Reason 'launcher pre-watch gate denial')
            Write-Od 'FAILED_replay_already_completed'
            exit 2
        }
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

    # FRESH17: shrink the game window so it never covers the operator's other
    # programs. Done AFTER the settle (splash is fragile), ONCE, and before the
    # watch/click phase so the dialog renders at the small size from the start.
    # A maximized window is restored first (SetWindowPos cannot shrink a
    # maximized window in place); this single restore is the pre-dialog path,
    # NOT the LoginOnReplay churn that correlated with OnBackground.
    if (-not $NoResizeWindow -and $ResizeWindowWidth -gt 0 -and $ResizeWindowHeight -gt 0) {
        $rg = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if ($rg) {
            $before = New-Object OdLaunch.WindowResize+RECT
            [void][OdLaunch.WindowResize]::GetWindowRect($rg.MainWindowHandle, [ref]$before)
            $wasZoomed = [OdLaunch.WindowResize]::IsZoomed($rg.MainWindowHandle)
            if ($wasZoomed) { [void][OdLaunch.WindowResize]::Restore($rg.MainWindowHandle) }
            # Place the resized window at the second monitor's top-left when
            # one is attached; otherwise keep the caller's X/Y (default 0,0).
            $resizeX = $ResizeWindowX
            $resizeY = $ResizeWindowY
            if (-not $NoSecondMonitorPlacement) {
                $monX = 0
                $monY = 0
                if ([OdLaunch.MonitorTarget]::TryGetSecondMonitorTopLeft([ref]$monX, [ref]$monY)) {
                    $resizeX = $monX
                    $resizeY = $monY
                    Write-Od ("resize_window second_monitor top_left=" + $monX + "," + $monY)
                }
                else {
                    Write-Od 'resize_window second_monitor none_primary_top_left'
                }
            }
            $resized = [OdLaunch.WindowResize]::Resize(
                $rg.MainWindowHandle, $ResizeWindowWidth, $ResizeWindowHeight, $resizeX, $resizeY)
            Start-Sleep -Milliseconds 300
            $after = New-Object OdLaunch.WindowResize+RECT
            [void][OdLaunch.WindowResize]::GetWindowRect($rg.MainWindowHandle, [ref]$after)
            Write-Od ("resize_window ok=" + $resized + " from=" + ($before.Right - $before.Left) + "x" + ($before.Bottom - $before.Top) +
                " to=" + ($after.Right - $after.Left) + "x" + ($after.Bottom - $after.Top) + " at=" + $after.Left + "," + $after.Top + " zoomed=" + $wasZoomed)
        }
        else {
            Write-Od 'resize_window no_game_window_skip'
        }
    }

    $watchScript = Join-Path $RepoRoot 'scripts\click-watch-offline.ps1'
    # In-process invoke via ResultPath: nested consoles either steal focus
    # (OnBackground / dialog dismiss) or, when Hidden, fail to inject clicks.
    $watchResult = Join-Path $env:TEMP 'od-watch-offline.exit.txt'
    Remove-Item -LiteralPath $watchResult -Force -ErrorAction SilentlyContinue
    $watchExit = 99
    try {
        & $watchScript -TimeoutSeconds $WatchTimeoutSeconds -ResultPath $watchResult `
            -ReplayPath $ReplayPath
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
            Write-Od 'watch_unexpected'
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
        # Same distinguishable completion as the pre-watch check: the replay may
        # have ended DURING the watch click (results screen) - that is a clean
        # terminal state, not a failed gate.
        if ($post.reasonCode -eq 'evidence.replay_completed') {
            # In-window belt-and-suspenders: persist the marker so a later
            # pre-flight (after this host/process is gone) also fails fast.
            [void](Write-OdCompletionMarker -ReplayPath $ReplayPath -Reason 'launcher post-watch gate denial')
            Write-Od 'FAILED_replay_already_completed'
            exit 4
        }
        Write-Od 'FAILED_gate_not_verified'
        exit 4
    }

    # G2 clock anchor. The anchor's SourceAnchorUtc must be the wall-clock
    # moment when the replay CLOCK reached replay-time 0 - the blitz-log
    # 'Start replay event' marker the watch stops on - NOT the gate moment
    # observed here (~5 s later). Live rehearsal (OD-RECOVERY-086) measured the
    # consequence: anchoring at the gate put every batch label ~4.9 s ahead of
    # the true replay time (constant skew), which failed the 2 m position
    # cross-check for every MOVING tank while stationary tanks matched to
    # 0.00 m. Every downstream caller (od-073 poll, batch rehearsal, live
    # frame) attests sameDecodedClockProven from this segment; without it the
    # flag stays false and gated reads fail closed. Non-fatal by design: an
    # anchor failure leaves the flag false and the session continues (the
    # caller records the honest negative). A monotonicity conflict (a caller
    # already appended) is ignored.
    try {
        $sessions = Invoke-RestMethod -Uri "$($api.Base)/api/v1/sessions?limit=200" -Headers $api.Headers
        $artifactSessions = @($sessions.items | Where-Object {
            $null -ne $_.session -and
            [string]$_.decodeRun.sourceArtifactId -eq $artifactId
        })
        if ($artifactSessions.Count -gt 0) {
            $battleSessionId = [string]$artifactSessions[0].session.battleSessionId

            # Resolve the replay-start wall-clock from the current blitz log
            # (same log the watch stops on): the last 'Start replay event'
            # line, whose leading HH:MM:SS is the replay-start wall-clock in
            # UTC (verified live: the line reads 15:23:22 while the machine's
            # local time was 11:23:22 at UTC-4). Date comes from the log
            # filename (blitz-logs_YYYYMMDD...). Fall back to UtcNow (the old
            # gate moment) if the marker cannot be parsed - the anchor then
            # carries the known ~5 s skew, but a session still gets a clock
            # rather than none.
            $replayStartUtc = $null
            try {
                $davaDir = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
                $blitzLog = Get-ChildItem -LiteralPath $davaDir -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($blitzLog -and $blitzLog.Name -match 'blitz-logs_(\d{8})') {
                    $logDate = [datetime]::ParseExact(
                        $Matches[1], 'yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
                    $markerLine = Select-String -LiteralPath $blitzLog.FullName `
                        -Pattern 'START_REPLAY_LOCAL|Start replay event' |
                        Select-Object -Last 1
                    if ($markerLine -and $markerLine.Line -match '^(\d{2}):(\d{2}):(\d{2})') {
                        $markerTime = New-Object DateTime(
                            $logDate.Year, $logDate.Month, $logDate.Day,
                            [int]$Matches[1], [int]$Matches[2], [int]$Matches[3],
                            [DateTimeKind]::Utc)
                        # The marker's HH:MM:SS is UTC; its DATE comes from
                        # the log FILENAME. The game keeps writing to the log
                        # it opened at launch, so a replay started just after
                        # UTC midnight lands a marker in the PREVIOUS day's
                        # file - the filename date is then 24 h stale and the
                        # G2 clock estimate is off by a day (observed live
                        # 2026-08-12: marker 00:43:42Z parsed as 08-11 while
                        # the launch was 08-12 -> frame replayTimeSeconds
                        # 86517 = 24.03 h). Roll the date forward (bounded)
                        # until the marker sits within 10 min of now: the
                        # marker is THIS launch's replay start (seconds before
                        # this anchor append), so anything older cannot be it.
                        $markerTooStale = $false
                        $markerRolls = 0
                        while ($markerRolls -lt 4 -and
                            ([DateTime]::UtcNow - $markerTime).TotalMinutes -gt 10) {
                            $markerTime = $markerTime.AddDays(1)
                            $markerRolls++
                        }
                        if (([DateTime]::UtcNow - $markerTime).TotalMinutes -gt 10) {
                            $markerTooStale = $true
                        }
                        if ($markerTooStale) {
                            Write-Od 'clock_anchor marker_too_stale_falling_back_to_gate_moment'
                            $replayStartUtc = [DateTime]::UtcNow
                        }
                        else {
                            $replayStartUtc = $markerTime
                        }
                    }
                }
            }
            catch {
                $replayStartUtc = $null
            }
            if ($null -eq $replayStartUtc) {
                Write-Od 'clock_anchor marker_unparsed_falling_back_to_gate_moment'
                $replayStartUtc = [DateTime]::UtcNow
            }

            $clockBody = @{
                battleSessionId    = $battleSessionId
                sequence           = 0
                sourceAnchorUtc    = $replayStartUtc.ToString('o')
                replayAnchorTicks  = 0
                speed              = 1.0
                source             = 'CaptureLog'
                uncertaintyTicks   = [TimeSpan]::FromSeconds(1).Ticks
            } | ConvertTo-Json
            $clock = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/discover/clock-segment" -Method Post -Headers $api.Headers -Body $clockBody
            Write-Od ('clock_anchor appended sequence=' + $clock.sequence +
                ' uncertainty_s=' + ([TimeSpan]::FromTicks([long]$clock.uncertaintyTicks).TotalSeconds) +
                ' battleSession=' + $battleSessionId +
                ' sourceAnchorUtc=' + $replayStartUtc.ToString('o'))
        }
        else {
            Write-Od 'clock_anchor no_decoded_session_for_artifact (flag stays false)'
        }
    }
    catch {
        Write-Od 'clock_anchor append_failed (flag stays false; launch continues)'
    }

    Write-Od 'OK OfflineReplayVerified'
    exit 0
}
catch {
    Write-Od 'FAILED_unexpected'
    exit 5
}
