#Requires -Version 5.1
<#
.SYNOPSIS
  Run one coordinator-owned gun-aim read (penetration v0.3 G1 item 5) against
  the currently verified offline replay launch, optionally polling to observe
  a controlled turret/gun traverse.

.DESCRIPTION
  Decoupled from launch-offline-replay-for-od.ps1 on purpose: the launcher's
  job is to reach OfflineReplayVerified; this script polls that gate itself,
  binds the launch artifact to its decoded run, and POSTs
  /api/v1/game/discover/entity-region with regionAnchor=gun-aim. The
  coordinator owns every read location (the pen-ownership-walk rotator scan,
  then the rotator's two per-frame Update inputs at +0xe0/+0xe4 and the
  gun-marker aim struct at +0x28..0x40), so this script carries no address,
  offset, or pointer and prints only the aim inputs + aim struct floats.

  With -PollSeconds > 0 the script re-reads the anchor at a 100 ms cadence
  for that many seconds and reports every DISTINCT resolved aim tuple (the
  two inputs + the normalized direction) plus the number of transitions
  observed. A controlled turret traverse moves exactly one input; a
  controlled gun-elevation change moves the other, so the transition pattern
  is what names turret yaw vs gun elevation.

  Hull yaw (ring +0x30, already Verified) is the gate's discriminator and is
  read by the EXISTING live-frame surface, not this anchor; the correlator
  combines it with these samples. This is a discovery read, not a promotion:
  nothing here publishes or promotes any field.

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

    # When > 0, poll the gun-aim anchor at 100 ms cadence for this many
    # seconds and report distinct aim tuples + transitions. 0 = one read.
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
Write-Host 'pen_aim: waiting_for_verified_gate'
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
        Write-Host ('pen_aim: FAILED_launcher=' + $launcherFatal)
        exit 1
    }

    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'pen_aim: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'pen_aim: gate=OfflineReplayVerified'

# ---- 2. Artifact + decode-run binding --------------------------------------
$artifactId = Get-LaunchArtifactId
if ([string]::IsNullOrWhiteSpace($artifactId)) {
    Write-Host 'pen_aim: FAILED_launch_artifact_binding'
    exit 2
}

$page = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
$artifactSessions = @($page.items | Where-Object {
    $null -ne $_.session -and
    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
})
if ($artifactSessions.Count -eq 0) {
    Write-Host 'pen_aim: FAILED_no_decoded_session'
    exit 2
}
$battleSessionId = [string]$artifactSessions[0].session.battleSessionId
Write-Host ('pen_aim: bound_battle_session=' + $battleSessionId)

# ---- 3. Gun-aim read -------------------------------------------------------
$body = @{
    entityId                = 0
    regionLength            = 16
    regionAnchor            = 'gun-aim'
    battleSessionId         = $battleSessionId
}

$script:distinct = New-Object System.Collections.Generic.HashSet[string]
$script:transitionCount = 0
$script:previousKey = $null
$script:samples = 0

function Read-GunAim {
    $response = Invoke-PenApi -Method 'Post' `
        -RelativePath '/api/v1/game/discover/entity-region' `
        -Body $body
    $script:samples += 1
    if ($response.Status -ne 'Resolved') {
        Write-Host ('pen_aim: status=' + $response.Status +
            ' failure_stage=' + $response.FailureStage)
        return
    }

    $input0 = [double]$response.GunAimInput0
    $input1 = [double]$response.GunAimInput1
    $dirX = [double]$response.GunAimDirX
    $dirY = [double]$response.GunAimDirY
    $dirZ = [double]$response.GunAimDirZ
    $distance = [double]$response.GunAimDistance
    $stable = $response.GunAimTwoPassStable

    # Round to absorb per-frame float jitter so a stationary aim stays one
    # distinct state and a real traverse/elevation change is a transition.
    $key = ('in0=' + [math]::Round($input0, 4) +
        ' in1=' + [math]::Round($input1, 4) +
        ' dir=' + [math]::Round($dirX, 4) + ',' +
        [math]::Round($dirY, 4) + ',' + [math]::Round($dirZ, 4))

    if ($script:distinct.Add($key)) {
        Write-Host ('pen_aim: state=' + $key +
            ' dist=' + [math]::Round($distance, 2) +
            ' stable=' + $stable)
    }
    if ($null -ne $script:previousKey -and $script:previousKey -ne $key) {
        $script:transitionCount += 1
    }
    $script:previousKey = $key
}

if ($PollSeconds -le 0) {
    Read-GunAim
}
else {
    Write-Host ('pen_aim: polling=' + $PollSeconds + 's')
    $pollDeadline = [DateTime]::UtcNow.AddSeconds($PollSeconds)
    while ([DateTime]::UtcNow -lt $pollDeadline) {
        try {
            Read-GunAim
        }
        catch {
            # A transient read failure during polling is reported, not fatal;
            # the distinct-state set already captured keeps the verdict honest.
            Write-Host ('pen_aim: transient_read_error=' + $_.Exception.Message)
        }
        Start-Sleep -Milliseconds 100
    }
}

Write-Host ('pen_aim: samples=' + $script:samples +
    ' distinct_states=' + $script:distinct.Count +
    ' transitions=' + $script:transitionCount)

if ($script:distinct.Count -ge 1) {
    Write-Host 'pen_aim: GUN_AIM_OBSERVED (Update inputs + aim struct read live)'
}
else {
    Write-Host 'pen_aim: honest_negative_or_fail_closed (no resolved gun-aim state)'
}

exit 0
