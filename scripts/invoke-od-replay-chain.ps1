<#
.SYNOPSIS
    One-command live offline-replay chain: launch -> gate anchor -> driver.

.DESCRIPTION
    Runs the offline-replay launch protocol (scripts/launch-offline-replay-for-od.ps1)
    and then the capture driver (scripts/invoke-hp-diffing-session.ps1) against the
    live battle session, in ONE command. Generalized from the .data scratch chains
    that ran the OD-RECOVERY-096 medvedkovo capture.

    The launch is DEADLOCK-FREE (2026-08-12 lesson): Start-Process
    -RedirectStandardOutput instead of `& launcher *> log`. PowerShell's `*>`
    waits for EOF on the redirect handle; the launcher's grandchildren (Host.Web,
    wotblitz.exe) inherit that handle, so `*>` never returns and the chain hangs
    while the replay plays out unwatched. Start-Process returns immediately; we
    poll the launcher's own log file for the gate anchor.

    Log reads use FileShare.ReadWrite (the launcher holds the redirect write
    handle for the whole launch; a plain read throws IOException while it is
    being written - this killed the first chain attempt) and decode both the
    UTF-16 redirect form and UTF-8.

    Completion recognition (2026-08-12): a launcher log line containing
    FAILED_replay_already_completed (gate Denied with reason
    evidence.replay_completed) means the replay ALREADY finished - a clean
    terminal state, not a failure. The chain reports it distinctly (exit 7).
    Persisted completion marker (OD-099 durable fix): the gate denial is
    in-memory and dies with the game process, so the pre-flight ALSO consults
    a marker file keyed to the replay's immutable fingerprint (written by the
    driver on the in-session definitive teardown, or by the launcher on an
    in-window gate denial) and exits 7 before any launch.

.EXITCODES
  0  Driver verdict ran (exit code propagated from invoke-hp-diffing-session.ps1)
  1  Usage / parameter error
  2  Replay file missing
  3  Launcher FAILED_ token (other than replay already completed)
  4  Launcher exited without the gate
  5  No battleSession anchor within the timeout
  6  Launcher exited OK but no anchor
  7  Replay already completed (launcher FAILED_replay_already_completed / persisted completion marker)

.PARAMETER ReplayPath
    Top-level ORIGINAL replay in the game's replays folder (the launcher's
    pre-flight probe validates the client version; a staging copy or a wrong
    version fails fast with the probe verdict).

.PARAMETER Track
    Driver -Track (default damage-dealt).

.PARAMETER RegionAnchor
    Driver -RegionAnchor (default avatar-stats).

.PARAMETER DataRoot
    Driver -DataRoot (default "$env:LOCALAPPDATA\WotBTreader" - the HOST store,
    which holds the launch-matched decode the G2 clock anchor attaches to).

.PARAMETER FailOnNoHit
    Pass -FailOnNoHit to the driver (verdict must hit or the chain exits 1).

.PARAMETER LauncherTimeoutMinutes
    How long to wait for the gate anchor (default 8).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-od-replay-chain.ps1 `
        -ReplayPath "$env:LOCALAPPDATA\wotblitz\DAVAProject\replays\deadrail-20260802.wotbreplay" `
        -Track damage-dealt -RegionAnchor avatar-stats -FailOnNoHit
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ReplayPath,
    [string]$Track = 'damage-dealt',
    [string]$RegionAnchor = 'avatar-stats',
    [string]$DataRoot = "$env:LOCALAPPDATA\WotBTreader",
    [switch]$FailOnNoHit,
    [int]$LauncherTimeoutMinutes = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Persisted replay-completion marker (OD-099 durable fix): the cross-session
# gate denial evidence.replay_completed is in-memory and dies with the game
# process, so the pre-flight consults a marker file keyed to the replay's
# immutable fingerprint instead. Fail BEFORE any launch (the launcher would
# reject it too, but this exits 7 immediately with a clear message).
. (Join-Path $PSScriptRoot 'od-replay-completion.ps1')

if (-not (Test-Path -LiteralPath $ReplayPath)) {
    Write-Output "CHAIN: replay file missing: $ReplayPath"
    exit 2
}

if (Test-OdReplayCompleted -ReplayPath $ReplayPath) {
    Write-Output 'CHAIN: replay already completed (persisted marker) - no capture possible'
    exit 7
}

$launchLog = '.data/od-chain-launch.log'
$launchErr = '.data/od-chain-launch.err.log'
Remove-Item -LiteralPath $launchLog, $launchErr -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path '.data' | Out-Null

function Read-LogText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    try {
        # FileShare.ReadWrite: the launcher holds the redirect write handle
        # open for the whole launch; a plain read throws IOException while it
        # is being written (killed the first chain attempt).
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $length = [int]$stream.Length
            if ($length -eq 0) { return '' }
            $bytes = New-Object byte[] $length
            [void]$stream.Read($bytes, 0, $length)
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        # Transient write lock: return empty; the poll loop retries.
        return ''
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

$launcher = Start-Process -FilePath 'powershell' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        'scripts/launch-offline-replay-for-od.ps1', '-ReplayPath', $ReplayPath) `
    -RedirectStandardOutput $launchLog -RedirectStandardError $launchErr `
    -PassThru -WindowStyle Hidden

$deadline = (Get-Date).AddMinutes($LauncherTimeoutMinutes)
$liveSession = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $logText = Read-LogText -path $launchLog
    if ($logText -match 'battleSession=([0-9a-f-]{36})') {
        $liveSession = $Matches[1]
        Write-Output ('CHAIN: gate anchor battleSession=' + $liveSession)
        break
    }
    if ($logText -match 'FAILED_') {
        if ($logText -match 'FAILED_replay_already_completed') {
            Write-Output 'CHAIN: replay already completed (FAILED_replay_already_completed) - no capture possible'
            exit 7
        }
        $failedLine = ($logText -split "`n" | Select-String 'FAILED_' | Select-Object -Last 1)
        Write-Output ('CHAIN: launcher FAILED: ' + $failedLine)
        exit 3
    }
    $launcher.Refresh()
    if ($launcher.HasExited) {
        if ($logText -match 'OK OfflineReplayVerified') {
            Write-Output 'CHAIN: launcher exited OK but no battleSession anchor'
            exit 6
        }
        Write-Output ('CHAIN: launcher exited ' + $launcher.ExitCode + ' without gate')
        exit 4
    }
}
if (-not $liveSession) {
    Write-Output 'CHAIN: no battleSession within timeout'
    exit 5
}

$driverArgs = @('-SessionId', $liveSession, '-Track', $Track,
    '-RegionAnchor', $RegionAnchor, '-LiveAcquire', '-DataRoot', $DataRoot,
    '-ReplayPath', $ReplayPath)
if ($FailOnNoHit) { $driverArgs += '-FailOnNoHit' }

Write-Output ('CHAIN: running hp-diffing session for ' + $liveSession)
& powershell -NoProfile -ExecutionPolicy Bypass -File scripts/invoke-hp-diffing-session.ps1 `
    @driverArgs *> '.data/od-chain-session.log'
$driverCode = $LASTEXITCODE
Write-Output ('CHAIN: driver exit ' + $driverCode)
exit $driverCode
