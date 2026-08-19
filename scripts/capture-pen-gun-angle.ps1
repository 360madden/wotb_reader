#Requires -Version 5.1
<#
.SYNOPSIS
  Run one coordinator-owned gun-angle read (penetration v0.3 G1 item 5) against
  the currently verified offline replay launch, optionally polling to observe
  a controlled turret/gun traverse.

.DESCRIPTION
  Decoupled from launch-offline-replay-for-od.ps1 on purpose: the launcher's
  job is to reach OfflineReplayVerified; this script polls that gate itself,
  binds the launch artifact to its decoded run, and POSTs
  /api/v1/game/discover/entity-region with regionAnchor=gun-angle. The
  coordinator owns every read location (the pen-ownership-walk rotator scan,
  then [owner+0x04] -> entity -> +0x2c DAVA component array -> vftable scan
  for CurrentGunAnglesComponent), so this script carries no address, offset,
  or pointer and prints only the NAMED axis floats: turretYaw (+0x10) and
  gunPitch (+0x14).

  With -PollSeconds > 0 the script re-reads the anchor at a 100 ms cadence
  for that many seconds and reports every DISTINCT resolved angle tuple plus
  the number of transitions observed. A controlled turret traverse moves
  turretYaw; a controlled gun-elevation change moves gunPitch. Because these
  axes are already statically NAMED (unlike the rotator's +0xe0/+0xe4 pair),
  this surface is the authoritative cross-check that names the rotator inputs
  during the owner-run traverse.

  This is a discovery read, not a promotion: nothing here publishes or
  promotes any field.

.EXITCODES
  0  Read completed and the verdict was printed.
  1  Gate never reached OfflineReplayVerified within the bound.
  2  Launch artifact / decode-run binding unavailable.
  3  entity-region endpoint returned an error.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 600)]
    [int]$WaitVerifiedSeconds = 300,

    # When > 0, poll the gun-angle anchor at 100 ms cadence for this many
    # seconds and report distinct angle tuples + transitions. 0 = one read.
    [ValidateRange(0, 600)]
    [int]$PollSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Owner-only file ACL check (deduped helper, same one the launcher uses).
. (Join-Path $PSScriptRoot 'od-replay-completion.ps1')

function Get-Rendezvous {
    try {
        $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -eq $file -or
            -not (Test-OdOwnerOnlyFileAcl -Path $file.FullName)) {
            return $null
        }

        $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (-not $record.PSObject.Properties['baseUri'] -or
            -not $record.PSObject.Properties['capability'] -or
            [string]::IsNullOrWhiteSpace([string]$record.baseUri) -or
            [string]::IsNullOrWhiteSpace([string]$record.capability)) {
            return $null
        }

        $uri = [Uri][string]$record.baseUri
        if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') {
            return $null
        }

        return $record
    }
    catch {
        return $null
    }
}

function Invoke-PenApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )

    $rendezvous = Get-Rendezvous
    if ($null -eq $rendezvous) {
        throw [InvalidOperationException]::new('rendezvous_unavailable')
    }

    $arguments = @{
        Uri        = [string]$rendezvous.baseUri + $RelativePath
        Method     = $Method
        TimeoutSec = 30
        Headers    = @{
            'X-WotBTreader-Capability' = [string]$rendezvous.capability
        }
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 6 -Compress
    }

    try {
        return Invoke-RestMethod @arguments
    }
    catch [System.Net.WebException] {
        $response = $_.Exception.Response
        if ($null -ne $response) {
            $bodyText = ''
            try {
                $stream = $response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object IO.StreamReader($stream)
                    $bodyText = $reader.ReadToEnd()
                    $reader.Close()
                }
            }
            catch { }
            throw ('pen_api_http_error status=' + [int]$response.StatusCode +
                ' body=' + $bodyText)
        }
        throw
    }
}

function Get-LaunchArtifactId {
    try {
        $marker = Join-Path (Join-Path $env:LOCALAPPDATA 'WotBTreader\od-launch') `
            'artifact.id'
        if (-not (Test-Path -LiteralPath $marker) -or
            -not (Test-OdOwnerOnlyFileAcl -Path $marker)) {
            return $null
        }

        $file = Get-Item -LiteralPath $marker
        if ($file.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-20)) {
            return $null
        }

        $value = (Get-Content -LiteralPath $marker -Raw).Trim()
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse($value, [ref]$parsed) -or $parsed -eq [Guid]::Empty) {
            return $null
        }

        return $parsed.ToString('D')
    }
    catch {
        return $null
    }
}

function Get-LauncherFatalError {
    $log = Join-Path $env:TEMP 'od-launch.log'
    if (-not (Test-Path -LiteralPath $log)) {
        return $null
    }
    try {
        $match = Select-String -LiteralPath $log -Pattern '^od_launch: FAILED_' |
            Select-Object -Last 1
        if ($null -eq $match) {
            return $null
        }
        return $match.Line.Trim()
    }
    catch {
        return $null
    }
}

# ---- 1. Gate poll ----------------------------------------------------------
Write-Host 'pen_angle: waiting_for_verified_gate'
$deadline = [DateTime]::UtcNow.AddSeconds($WaitVerifiedSeconds)
$verified = $false
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $state = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/game/state'
        if ($state.verificationState -eq 'OfflineReplayVerified' -and
            $state.reasonCode -eq 'session.offline_replay_verified') {
            $verified = $true
            break
        }
    }
    catch { }

    $launcherFatal = Get-LauncherFatalError
    if ($null -ne $launcherFatal) {
        Write-Host ('pen_angle: FAILED_launcher=' + $launcherFatal)
        exit 1
    }

    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'pen_angle: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'pen_angle: gate=OfflineReplayVerified'

# ---- 2. Artifact + decode-run binding --------------------------------------
$artifactId = Get-LaunchArtifactId
if ([string]::IsNullOrWhiteSpace($artifactId)) {
    Write-Host 'pen_angle: FAILED_launch_artifact_binding'
    exit 2
}

$page = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
$artifactSessions = @($page.items | Where-Object {
    $null -ne $_.session -and
    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
})
if ($artifactSessions.Count -eq 0) {
    Write-Host 'pen_angle: FAILED_no_decoded_session'
    exit 2
}
$battleSessionId = [string]$artifactSessions[0].session.battleSessionId
Write-Host ('pen_angle: bound_battle_session=' + $battleSessionId)

# ---- 3. Gun-angle read -----------------------------------------------------
$body = @{
    entityId                = 0
    regionLength            = 16
    regionAnchor            = 'gun-angle'
    battleSessionId         = $battleSessionId
}

$script:distinct = New-Object System.Collections.Generic.HashSet[string]
$script:transitionCount = 0
$script:previousKey = $null
$script:samples = 0
$script:errorCount = 0

function Read-GunAngle {
    $response = Invoke-PenApi -Method 'Post' `
        -RelativePath '/api/v1/game/discover/entity-region' `
        -Body $body
    $script:samples += 1
    if ($response.Status -ne 'Resolved') {
        Write-Host ('pen_angle: status=' + $response.Status +
            ' failure_stage=' + $response.FailureStage)
        return
    }

    $turretYaw = [double]$response.GunAngleTurretYaw
    $gunPitch = [double]$response.GunAngleGunPitch
    $stable = $response.GunAngleTwoPassStable

    # Round to absorb per-frame float jitter so a stationary angle stays one
    # distinct state and a real traverse/elevation change is a transition.
    $key = ('turretYaw=' + [math]::Round($turretYaw, 4) +
        ' gunPitch=' + [math]::Round($gunPitch, 4))

    if ($script:distinct.Add($key)) {
        Write-Host ('pen_angle: state=' + $key + ' stable=' + $stable)
    }
    if ($null -ne $script:previousKey -and $script:previousKey -ne $key) {
        $script:transitionCount += 1
    }
    $script:previousKey = $key
}

if ($PollSeconds -le 0) {
    try {
        Read-GunAngle
    }
    catch {
        # A single-read failure is fatal: honor the documented exit code 3.
        Write-Host ('pen_angle: FAILED_endpoint_or_read=' + $_.Exception.Message)
        exit 3
    }
}
else {
    Write-Host ('pen_angle: polling=' + $PollSeconds + 's')
    $pollDeadline = [DateTime]::UtcNow.AddSeconds($PollSeconds)
    while ([DateTime]::UtcNow -lt $pollDeadline) {
        try {
            Read-GunAngle
        }
        catch {
            # A transient read failure during polling is reported, not fatal;
            # the distinct-state set already captured keeps the verdict honest.
            $script:errorCount += 1
            Write-Host ('pen_angle: transient_read_error=' + $_.Exception.Message)
        }
        Start-Sleep -Milliseconds 100
    }
}

Write-Host ('pen_angle: samples=' + $script:samples +
    ' distinct_states=' + $script:distinct.Count +
    ' transitions=' + $script:transitionCount)

if ($script:distinct.Count -ge 1) {
    Write-Host 'pen_angle: GUN_ANGLE_OBSERVED (named turretYaw + gunPitch read live)'
}
elseif ($script:samples -gt 0 -and $script:errorCount -eq $script:samples) {
    # Every sample errored: an infrastructure failure, not a fail-closed
    # discovery. Honor the documented exit code 3 so the gate stays honest.
    Write-Host ('pen_angle: FAILED_all_samples_errored samples=' + $script:samples)
    exit 3
}
else {
    Write-Host 'pen_angle: honest_negative_or_fail_closed (no resolved gun-angle state)'
}

exit 0
