#Requires -Version 5.1
<#
.SYNOPSIS
  Live PN-4 aim capture: poll the composed live frame during replay playback
  and record the CAM-013 chase-camera aim ray at each instant.

.DESCRIPTION
  The decisive PN-4 aim source. During a managed, gate-verified replay
  playback, poll POST /discover/live-frame at a fixed cadence and reconstruct
  the camera aim EXACTLY as LiveFrameProjector.BuildCamera does (the
  live-verified CAM-010/CAM-012 path):

    eye     = (pose.X, pose.Z, pose.Y)   # the yz-swap (CAM-010)
    forward = -basis[3..5] normalized    # forward = -row1 (CAM-012)

  Each sample is keyed by the live frame's replay clock (replayTimeSeconds ->
  TimeSpan ticks), recorded only when the clock is G2-anchored
  (sameDecodedClockProven) so the ticks line up with the decoded
  ShotImpact.ReplayTime values the scorer compares against. Samples are
  monotonic (a stalled/backwards clock is skipped, never re-recorded).

  Output is the exact aimOverrides body for POST
  /api/v1/game/discover/pen-offline-score/{id}:

    { "aimOverrides": [ { "replayTimeTicks", "originX/Y/Z",
                          "directionX/Y/Z" }, ... ] }

  The capture stops when the live frame stops resolving (the replay ended and
  the game exited) or the safety cap is reached, then writes the accumulated
  samples. The scorer re-normalizes non-unit directions and only consumes the
  VIEWPOINT tank's own shots, so this script records EVERY resolved frame, not
  just ones it thinks are shots.

  Never logs tokens, full paths, or account ids.
#>
[CmdletBinding()]
param(
    # Battle session id of the decoded replay being played. When omitted, the
    # script resolves it from the launcher's artifact marker
    # (%LOCALAPPDATA%\WotBTreader\od-launch\artifact.id) via the sessions list.
    [string]$BattleSessionId,
    [int]$CadenceMs = 100,
    [int]$MaxSeconds = 600,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Cap([string]$Message) {
    Write-Host ("pen_aim_capture: " + $Message)
}

function Get-CapRendezvous {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
}

function Get-CapApiContext {
    $rv = Get-CapRendezvous
    return @{
        Base    = [string]$rv.baseUri
        Headers = @{
            'X-WotBTreader-Capability' = [string]$rv.capability
            'Content-Type'             = 'application/json'
        }
    }
}

function Get-CapState($Api) {
    # /state is a read-only GET, so it answers even while the discover gate
    # denies. Keep this in one helper so the pre-gate wait and terminal check
    # use identical request semantics.
    return Invoke-RestMethod -Uri "$($Api.Base)/api/v1/game/state" `
        -Headers $Api.Headers -TimeoutSec 5
}

function Get-CapTerminalReason($Api) {
    # A terminal gate means the replay ended (or the game is gone), not a
    # transient poll failure -- the capture should stop, not retry.
    try {
        $st = Get-CapState $Api
        $vs = [string]$st.verificationState
        $rc = [string]$st.reasonCode
        if ($vs -eq 'Denied' -and $rc -eq 'evidence.replay_completed') {
            return 'replay_completed'
        }
        if ($vs -eq 'Denied') { return "denied_$rc" }
        if ($vs -eq 'GameAbsent') { return 'game_absent' }
        if ($vs -eq 'EvidenceStale') { return 'evidence_stale' }
    }
    catch {
        # Host not reachable yet; fall through to the sustained-failure timer.
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $env:TEMP 'pen-aim-overrides.json'
}

if ([string]::IsNullOrWhiteSpace($BattleSessionId)) {
    # The launcher writes the artifact marker during its import step and the
    # decoded session appears shortly after. Retry so this script can be
    # started BEFORE or IN PARALLEL with the launcher and still resolve the
    # session in time to capture the replay's shots from the start.
    $marker = Join-Path $env:LOCALAPPDATA 'WotBTreader\od-launch\artifact.id'
    $resolveDeadline = (Get-Date).AddSeconds(180)
    $resolved = $false
    while ((Get-Date) -lt $resolveDeadline) {
        if (Test-Path -LiteralPath $marker) {
            try {
                $artifactId = (Get-Content -LiteralPath $marker -Raw).Trim()
                $api = Get-CapApiContext
                $sessions = Invoke-RestMethod -Uri "$($api.Base)/api/v1/sessions?limit=200" -Headers $api.Headers -TimeoutSec 5
                $artifactSessions = @($sessions.items | Where-Object {
                    $null -ne $_.session -and
                    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
                })
                if ($artifactSessions.Count -gt 0) {
                    $BattleSessionId = [string]$artifactSessions[0].session.battleSessionId
                    $resolved = $true
                    break
                }
            }
            catch {
                # Host not up yet; retry.
            }
        }
        Start-Sleep -Seconds 2
    }
    if (-not $resolved) {
        Write-Cap 'FAILED_no_decoded_session_for_artifact'
        exit 1
    }
}

$samples = New-Object System.Collections.ArrayList
$api = $null
$lastTicks = [long]0
$framesSeen = 0
$framesSkippedClock = 0
$framesSkippedCamera = 0
$framesSkippedDuplicate = 0
$errors = 0
$flushEvery = [int][math]::Max(10, [int](30000 / [math]::Max(1, $CadenceMs)))

$deadline = $null
$consecutiveFailures = 0
$failureStartedAt = $null

Write-Cap ("starting session=" + $BattleSessionId + " cadence_ms=" + $CadenceMs +
    " max_s=" + $MaxSeconds + " output=" + $OutputPath)

# The launcher deliberately produces a pre-verification window while the
# game syncs. Do not spend the capture's failure budget there: wait for the
# positive gate once, then start the replay-time deadline and live-frame loop.
$verified = $false
$verificationDeadline = (Get-Date).AddSeconds(180)
Write-Cap 'waiting_for_verified_gate'
while ((Get-Date) -lt $verificationDeadline) {
    try {
        $api = Get-CapApiContext
        $state = Get-CapState $api
        $verificationState = [string]$state.verificationState
        if ($verificationState -eq 'OfflineReplayVerified') {
            $verified = $true
            break
        }
        if ($verificationState -eq 'Denied' -or
            $verificationState -eq 'GameAbsent' -or
            $verificationState -eq 'EvidenceStale') {
            Write-Cap ("stopping_terminal_gate_" + [string]$state.reasonCode)
            exit 1
        }
    }
    catch {
        # The host/rendezvous may not exist until the launcher starts; retry.
    }
    Start-Sleep -Milliseconds 500
}
if (-not $verified) {
    Write-Cap 'FAILED_verification_gate_timeout'
    exit 1
}
Write-Cap 'gate_verified_starting_capture'
$deadline = (Get-Date).AddSeconds($MaxSeconds)

while ((Get-Date) -lt $deadline) {
    try {
        $api = Get-CapApiContext
        $body = @{ battleSessionId = $BattleSessionId } | ConvertTo-Json
        $frame = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/discover/live-frame" `
            -Method Post -Headers $api.Headers -Body $body -TimeoutSec 5
        $consecutiveFailures = 0
        $failureStartedAt = $null
        $framesSeen++

        $cameraResolved = $false
        $ticks = [long]0
        $haveTicks = $false
        if ($null -ne $frame.replayTimeSeconds -and $frame.sameDecodedClockProven) {
            $ticks = [long][math]::Round([double]$frame.replayTimeSeconds * 10000000.0)
            $haveTicks = $true
        }
        else {
            $framesSkippedClock++
        }

        $cam = $frame.camera
        if ($haveTicks -and $null -ne $cam -and [string]$cam.status -eq 'Resolved' -and
            $null -ne $cam.basis -and $cam.basis.Count -ge 9 -and
            $null -ne $cam.x -and $null -ne $cam.y -and $null -ne $cam.z) {
            $cameraResolved = $true
        }
        else {
            $framesSkippedCamera++
        }

        if (-not $cameraResolved -or -not $haveTicks -or $ticks -le $lastTicks) {
            if ($cameraResolved -and $haveTicks -and $ticks -le $lastTicks) {
                $framesSkippedDuplicate++
            }
            Start-Sleep -Milliseconds $CadenceMs
            continue
        }

        # eye = (X, Z, Y) -- the CAM-010 yz-swap.
        $eyeX = [double]$cam.x
        $eyeY = [double]$cam.z
        $eyeZ = [double]$cam.y
        # forward = -row1 = -basis[3..5], normalized (CAM-012).
        $fx = -([double]$cam.basis[3])
        $fy = -([double]$cam.basis[4])
        $fz = -([double]$cam.basis[5])
        $len = [math]::Sqrt($fx * $fx + $fy * $fy + $fz * $fz)
        if ([double]::IsNaN($len) -or [double]::IsInfinity($len) -or $len -le 1e-6) {
            # A resolved camera with a zero/non-finite basis is not an aim
            # sample. Do not write a zero vector that the scorer would later
            # silently replace with the center-line proxy.
            $framesSkippedCamera++
            Start-Sleep -Milliseconds $CadenceMs
            continue
        }
        $fx /= $len
        $fy /= $len
        $fz /= $len

        [void]$samples.Add(@{
            replayTimeTicks = $ticks
            originX         = $eyeX
            originY         = $eyeY
            originZ         = $eyeZ
            directionX      = $fx
            directionY      = $fy
            directionZ      = $fz
        })
        $lastTicks = $ticks

        if ($samples.Count % $flushEvery -eq 0) {
            Write-Cap ("progress samples=" + $samples.Count + " t=" +
                ([double]$ticks / 10000000.0).ToString('0.0'))
        }
    }
    catch {
        $errors++
        $consecutiveFailures++
        if ($null -eq $failureStartedAt) { $failureStartedAt = Get-Date }
        if ($consecutiveFailures % 10 -eq 1) {
            Write-Cap ("poll_error consecutive=" + $consecutiveFailures + " " + $_.Exception.Message)
        }
        # Distinguish a terminal gate (replay completed / game gone) from a
        # transient poll failure, so the capture stops cleanly at replay end
        # instead of misreporting the fail-closed denials as poll errors.
        $terminalReason = $null
        if ($null -ne $api) {
            $terminalReason = Get-CapTerminalReason $api
        }
        if ($null -ne $terminalReason) {
            Write-Cap ("stopping_terminal_" + $terminalReason)
            break
        }
        # A 400 during the launch's pre-verified window is EXPECTED (the gate
        # flips only after Watch Offline). Stop only after a SUSTAINED failure
        # window (the gate never flipped or the game is gone), never on a few
        # fast consecutive 400s at high cadence.
        if (((Get-Date) - $failureStartedAt).TotalSeconds -gt 150) {
            Write-Cap 'stopping_after_sustained_poll_failures'
            break
        }
    }

    Start-Sleep -Milliseconds $CadenceMs
}

$payload = @{ aimOverrides = $samples } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($OutputPath, $payload, (New-Object Text.UTF8Encoding($false)))

Write-Cap ("captured samples=" + $samples.Count +
    " frames_seen=" + $framesSeen +
    " skipped_clock=" + $framesSkippedClock +
    " skipped_camera=" + $framesSkippedCamera +
    " skipped_duplicate=" + $framesSkippedDuplicate +
    " errors=" + $errors +
    " output=" + $OutputPath)

if ($samples.Count -eq 0) {
    Write-Cap 'FAILED_no_samples'
    exit 1
}

Write-Cap ('OK')
exit 0
