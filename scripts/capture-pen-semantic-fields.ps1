#Requires -Version 5.1
<#
.SYNOPSIS
  Sample the coordinator-owned pen-semantic-fields snapshot during a
  verified offline replay.

.DESCRIPTION
  Polls OfflineReplayVerified, binds the launch artifact to its decoded
  session, then POSTs /api/v1/game/discover/entity-region with
  regionAnchor=pen-semantic-fields on a bounded cadence. The coordinator
  reuses a process-local ownership-walk cache (vtable re-validated) and
  two-pass-reads the published gun-marker and VehicleGun reload/state
  block. When AlignToViewpointShots is true (default), marker-shots
  supplies viewpoint ShotImpact replay-seconds and the script waits for
  that window after an optional elevation stretch unless
  -NoAlignToViewpointShots is set. This script prints
  only counts, enum histograms, and 16-sector yaw/pitch bins -- never
  addresses, tokens, paths, or world XYZ. Walk-confirmed same-clock
  samples are overwritten to %TEMP%\pen-semantic-fields-samples.json.

.EXITCODES
  0  At least one snapshot completed and the summary was printed.
  1  Gate never reached OfflineReplayVerified within the bound.
  2  Launch artifact / decode-run binding unavailable.
  3  Every snapshot request failed.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 600)]
    [int]$WaitVerifiedSeconds = 300,

    [ValidateRange(1, 128)]
    [int]$Samples = 64,

    [ValidateRange(50, 5000)]
    [int]$CadenceMs = 200,

    [ValidateRange(0, 64)]
    [int]$ElevationSamples = 24,

    [switch]$NoAlignToViewpointShots,

    [ValidateRange(0, 30)]
    [int]$ShotLeadSeconds = 1,

    [ValidateRange(0, 30)]
    [int]$ShotTrailSeconds = 1,

    [ValidateRange(10, 600)]
    [int]$WaitReplayTimeoutSeconds = 400,

    [ValidateRange(0, 7)]
    [int]$OwnershipCandidateIndex = 0,

    [ValidateRange(1, 4096)]
    [int]$RegionLength = 16
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

. (Join-Path $scriptDir 'od-replay-completion.ps1')

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
            throw ('pen_api_http_error status=' + [int]$response.StatusCode)
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

function Get-YawBin([double]$Radians) {
    $twoPi = 6.283185307179586
    $shifted = $Radians + 3.141592653589793
    $normalized = $shifted - ([math]::Floor($shifted / $twoPi) * $twoPi)
    $bin = [int][math]::Floor(($normalized / $twoPi) * 16.0)
    if ($bin -lt 0) { return 0 }
    if ($bin -gt 15) { return 15 }
    return $bin
}

function Get-PitchBin([double]$Radians) {
    $halfPi = [math]::PI / 2.0
    $clamped = $Radians
    if ($clamped -lt (-1.0 * $halfPi)) { $clamped = -1.0 * $halfPi }
    if ($clamped -gt $halfPi) { $clamped = $halfPi }
    $span = [math]::PI
    $shifted = $clamped + $halfPi
    $bin = [int][math]::Floor(($shifted / $span) * 16.0)
    if ($bin -lt 0) { return 0 }
    if ($bin -gt 15) { return 15 }
    return $bin
}

function Test-FiniteNumber([object]$Value) {
    if ($null -eq $Value) { return $false }
    try {
        $number = [double]$Value
    }
    catch {
        return $false
    }
    return -not ([double]::IsNaN($number) -or [double]::IsInfinity($number))
}

function ConvertTo-JsonArray([System.Collections.ICollection]$Items) {
    if ($null -eq $Items -or $Items.Count -eq 0) {
        return '[]'
    }

    $parts = New-Object System.Collections.ArrayList
    foreach ($item in $Items) {
        [void]$parts.Add(($item | ConvertTo-Json -Compress -Depth 4))
    }
    return '[' + ($parts -join ',') + ']'
}

function Get-ViewpointShotSeconds {
    param([string]$SessionId)

    $cli = Join-Path $repoRoot 'src\WotBTreader.Host.Cli\bin\Release\net10.0\WotBTreader.Host.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli)) {
        Write-Host 'pen_fields: shot_times_cli_missing'
        return @()
    }

    $oldEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $cli marker-shots --session $SessionId --json 2>$null |
            ForEach-Object { "$_" }
    }
    finally {
        $ErrorActionPreference = $oldEap
    }

    $json = ($out -join "`n")
    if ([string]::IsNullOrWhiteSpace($json)) {
        Write-Host 'pen_fields: shot_times_empty'
        return @()
    }

    try {
        $envelope = $json | ConvertFrom-Json
    }
    catch {
        Write-Host 'pen_fields: shot_times_parse_failed'
        return @()
    }

    if ($envelope.success -ne $true) {
        Write-Host 'pen_fields: shot_times_cli_failed'
        return @()
    }

    $raw = @()
    if ($null -ne $envelope.data -and
        $null -ne $envelope.data.shotReplaySeconds) {
        $raw = @($envelope.data.shotReplaySeconds)
    }

    $times = New-Object System.Collections.ArrayList
    foreach ($item in $raw) {
        if (Test-FiniteNumber $item) {
            [void]$times.Add([double]$item)
        }
    }

    return @($times)
}

Write-Host 'pen_fields: waiting_for_verified_gate'
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
        Write-Host ('pen_fields: FAILED_launcher=' + $launcherFatal)
        exit 1
    }

    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'pen_fields: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'pen_fields: gate=OfflineReplayVerified'

$artifactId = Get-LaunchArtifactId
if ([string]::IsNullOrWhiteSpace($artifactId)) {
    Write-Host 'pen_fields: FAILED_launch_artifact_binding'
    exit 2
}

$page = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
$artifactSessions = @($page.items | Where-Object {
    $null -ne $_.session -and
    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
})
if ($artifactSessions.Count -eq 0) {
    Write-Host 'pen_fields: FAILED_no_decoded_session'
    exit 2
}
$battleSessionId = [string]$artifactSessions[0].session.battleSessionId
Write-Host ('pen_fields: bound_battle_session=' + $battleSessionId)

$script:ok = 0
$script:errors = 0
$script:walkConfirmed = 0
$script:reloadInRange = 0
$script:markerFinite = 0
$script:markerUnit = 0
$script:twoPassStable = 0
$script:enumSeen = New-Object 'System.Collections.Generic.HashSet[int]'
$script:markerYawBins = New-Object 'System.Collections.Generic.HashSet[int]'
$script:markerPitchBins = New-Object 'System.Collections.Generic.HashSet[int]'
$script:hullYawBins = New-Object 'System.Collections.Generic.HashSet[int]'
$script:independentWindows = 0
$script:elevationIndependentWindows = 0
$script:g2ClockSamples = 0
$script:originInBand = 0
$script:lastHullBin = $null
$script:lastMarkerBin = $null
$script:lastMarkerPitchBin = $null
$script:sameHullStreak = 0
$script:lastReplayTimeSeconds = $null
$script:stopRequested = $false
$script:persistedSamples = New-Object System.Collections.ArrayList
$samplesPath = [System.IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'pen-semantic-fields-samples.json'))

function Invoke-PenSemanticSample {
    param([int]$Index)

    try {
        $body = @{
            entityId                = 0
            regionLength            = $RegionLength
            regionAnchor            = 'pen-semantic-fields'
            battleSessionId         = $battleSessionId
            ownershipCandidateIndex = $OwnershipCandidateIndex
        }
        $response = Invoke-PenApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/entity-region' `
            -Body $body
        $script:ok++

        if (Test-FiniteNumber $response.ReplayTimeSeconds) {
            $script:lastReplayTimeSeconds = [double]$response.ReplayTimeSeconds
        }

        $walkOk = ($response.Status -eq 'Resolved') -and
            ($response.PenOwnershipOwnerPointerReadable -eq $true) -and
            ($response.PenOwnershipForwardRoundTripConfirmed -eq $true) -and
            ($response.PenOwnershipGunVtableConfirmed -eq $true) -and
            ($response.PenOwnershipEntityHpPlausible -eq $true) -and
            ($response.PenOwnershipTwoPassStable -eq $true)
        if ($walkOk) { $script:walkConfirmed++ }
        if ($response.PenSemanticReloadEnumInRange -eq $true) { $script:reloadInRange++ }
        if ($response.PenSemanticMarkerFinite -eq $true) { $script:markerFinite++ }
        if ($response.PenSemanticMarkerDirectionUnit -eq $true) { $script:markerUnit++ }
        if ($response.PenSemanticTwoPassStable -eq $true) { $script:twoPassStable++ }
        if ($null -ne $response.PenSemanticReloadEnum) {
            [void]$script:enumSeen.Add([int]$response.PenSemanticReloadEnum)
        }

        $markerBin = $null
        $markerPitchBin = $null
        $hullBin = $null
        if ($null -ne $response.PenSemanticMarkerYawRadians) {
            $markerBin = Get-YawBin ([double]$response.PenSemanticMarkerYawRadians)
            [void]$script:markerYawBins.Add($markerBin)
        }
        if (Test-FiniteNumber $response.PenSemanticMarkerPitchRadians) {
            $markerPitchBin = Get-PitchBin ([double]$response.PenSemanticMarkerPitchRadians)
            [void]$script:markerPitchBins.Add($markerPitchBin)
        }
        if ($null -ne $response.PenSemanticHullYawRadians) {
            $hullBin = Get-YawBin ([double]$response.PenSemanticHullYawRadians)
            [void]$script:hullYawBins.Add($hullBin)
        }

        $qualityOk = $walkOk -and
            ($response.PenSemanticMarkerFinite -eq $true) -and
            ($response.PenSemanticMarkerDirectionUnit -eq $true) -and
            ($response.PenSemanticTwoPassStable -eq $true)
        $sameClock = ($response.SameDecodedClockProven -eq $true)
        $replayTimeFinite = Test-FiniteNumber $response.ReplayTimeSeconds
        if ($sameClock -and $replayTimeFinite) {
            $script:g2ClockSamples++
        }
        if ($response.PenSemanticOriginInBand -eq $true) {
            $script:originInBand++
        }

        if ($walkOk -and $sameClock -and $replayTimeFinite) {
            $reloadEnum = $null
            if ($null -ne $response.PenSemanticReloadEnum) {
                $reloadEnum = [int]$response.PenSemanticReloadEnum
            }
            $markerYawValue = $null
            $markerPitchValue = $null
            $hullYawValue = $null
            if (Test-FiniteNumber $response.PenSemanticMarkerYawRadians) {
                $markerYawValue = [double]$response.PenSemanticMarkerYawRadians
            }
            if (Test-FiniteNumber $response.PenSemanticMarkerPitchRadians) {
                $markerPitchValue = [double]$response.PenSemanticMarkerPitchRadians
            }
            if (Test-FiniteNumber $response.PenSemanticHullYawRadians) {
                $hullYawValue = [double]$response.PenSemanticHullYawRadians
            }
            $originRelX = $null
            $originRelY = $null
            $originRelZ = $null
            if (Test-FiniteNumber $response.PenSemanticOriginRelX) {
                $originRelX = [double]$response.PenSemanticOriginRelX
            }
            if (Test-FiniteNumber $response.PenSemanticOriginRelY) {
                $originRelY = [double]$response.PenSemanticOriginRelY
            }
            if (Test-FiniteNumber $response.PenSemanticOriginRelZ) {
                $originRelZ = [double]$response.PenSemanticOriginRelZ
            }
            [void]$script:persistedSamples.Add([ordered]@{
                    replayTimeSeconds      = [double]$response.ReplayTimeSeconds
                    markerYawRadians       = $markerYawValue
                    markerPitchRadians     = $markerPitchValue
                    hullYawRadians         = $hullYawValue
                    reloadEnum             = $reloadEnum
                    sameDecodedClockProven = $true
                    originRelX             = $originRelX
                    originRelY             = $originRelY
                    originRelZ             = $originRelZ
                })
        }

        if ($null -ne $hullBin -and $null -ne $script:lastHullBin -and
            $hullBin -eq $script:lastHullBin) {
            $script:sameHullStreak++
            if ($script:sameHullStreak -ge 2 -and $null -ne $markerBin -and
                $null -ne $script:lastMarkerBin -and
                $markerBin -ne $script:lastMarkerBin) {
                $script:independentWindows++
            }
            if ($script:sameHullStreak -ge 2 -and $qualityOk -and
                $null -ne $markerBin -and $null -ne $script:lastMarkerBin -and
                $markerBin -eq $script:lastMarkerBin -and
                $null -ne $markerPitchBin -and
                $null -ne $script:lastMarkerPitchBin -and
                $markerPitchBin -ne $script:lastMarkerPitchBin) {
                $script:elevationIndependentWindows++
            }
        }
        else {
            $script:sameHullStreak = 0
        }
        $script:lastHullBin = $hullBin
        $script:lastMarkerBin = $markerBin
        $script:lastMarkerPitchBin = $markerPitchBin
        return $true
    }
    catch {
        $script:errors++
        Write-Host ('pen_fields: sample_error i=' + $Index + ' ' + $_.Exception.Message)
        $terminal = Get-LauncherFatalError
        if ($null -ne $terminal) {
            Write-Host ('pen_fields: stopping_launcher=' + $terminal)
            $script:stopRequested = $true
        }
        return $false
    }
}

function Invoke-PenSemanticBurst {
    param(
        [int]$Count,
        [double]$StopAfterSeconds = -1
    )

    for ($i = 0; $i -lt $Count; $i++) {
        if ($script:stopRequested) { break }
        [void](Invoke-PenSemanticSample -Index $i)
        if ($StopAfterSeconds -ge 0 -and
            $null -ne $script:lastReplayTimeSeconds -and
            $script:lastReplayTimeSeconds -ge $StopAfterSeconds) {
            break
        }
        Start-Sleep -Milliseconds $CadenceMs
    }
}

function Wait-PenReplaySeconds {
    param(
        [double]$TargetSeconds,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastPrint = [DateTime]::MinValue
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:stopRequested) { return $false }
        try {
            $body = @{
                entityId                = 0
                regionLength            = $RegionLength
                regionAnchor            = 'pen-semantic-fields'
                battleSessionId         = $battleSessionId
                ownershipCandidateIndex = $OwnershipCandidateIndex
            }
            $response = Invoke-PenApi -Method 'Post' `
                -RelativePath '/api/v1/game/discover/entity-region' `
                -Body $body
            if (Test-FiniteNumber $response.ReplayTimeSeconds) {
                $now = [double]$response.ReplayTimeSeconds
                $script:lastReplayTimeSeconds = $now
                if (([DateTime]::UtcNow - $lastPrint).TotalSeconds -ge 10) {
                    Write-Host ('pen_fields: waiting_replay_seconds=' +
                        ([math]::Round($now, 1)) +
                        ' target=' + ([math]::Round($TargetSeconds, 1)))
                    $lastPrint = [DateTime]::UtcNow
                }
                if (($now + 0.05) -ge $TargetSeconds) {
                    return $true
                }
            }
        }
        catch {
            Write-Host ('pen_fields: wait_error ' + $_.Exception.Message)
            $terminal = Get-LauncherFatalError
            if ($null -ne $terminal) {
                Write-Host ('pen_fields: stopping_launcher=' + $terminal)
                $script:stopRequested = $true
                return $false
            }
        }
        Start-Sleep -Milliseconds 500
    }

    Write-Host 'pen_fields: wait_timeout'
    return $false
}

$shotSeconds = @()
if (-not $NoAlignToViewpointShots) {
    $shotSeconds = @(Get-ViewpointShotSeconds -SessionId $battleSessionId)
}

if ($shotSeconds.Count -gt 0) {
    $firstShot = [double]$shotSeconds[0]
    $lastShot = [double]$shotSeconds[$shotSeconds.Count - 1]
    Write-Host ('pen_fields: viewpoint_shots=' + $shotSeconds.Count +
        ' first=' + ([math]::Round($firstShot, 3)) +
        ' last=' + ([math]::Round($lastShot, 3)))
}
else {
    Write-Host 'pen_fields: viewpoint_shots=0'
}

$elevationCount = $ElevationSamples
if ($elevationCount -gt $Samples) { $elevationCount = $Samples }
if ($elevationCount -gt 0) {
    Write-Host ('pen_fields: elevation_phase samples=' + $elevationCount)
    Invoke-PenSemanticBurst -Count $elevationCount
}

$remaining = $Samples - $script:ok
if ($remaining -lt 0) { $remaining = 0 }
if ($shotSeconds.Count -gt 0 -and $remaining -gt 0 -and
    -not $script:stopRequested) {
    $waitUntil = [double]$shotSeconds[0] - [double]$ShotLeadSeconds
    if ($waitUntil -lt 0) { $waitUntil = 0 }
    $stopAfter = [double]$shotSeconds[$shotSeconds.Count - 1] +
        [double]$ShotTrailSeconds
    Write-Host ('pen_fields: shot_phase wait_until=' +
        ([math]::Round($waitUntil, 3)) +
        ' stop_after=' + ([math]::Round($stopAfter, 3)))
    $reached = Wait-PenReplaySeconds -TargetSeconds $waitUntil `
        -TimeoutSeconds $WaitReplayTimeoutSeconds
    if ($reached) {
        Invoke-PenSemanticBurst -Count $remaining -StopAfterSeconds $stopAfter
    }
    else {
        Write-Host 'pen_fields: shot_window_not_reached'
    }
}
elseif ($remaining -gt 0 -and -not $script:stopRequested) {
    Write-Host ('pen_fields: unaligned_phase samples=' + $remaining)
    Invoke-PenSemanticBurst -Count $remaining
}

$ok = $script:ok
$errors = $script:errors
$walkConfirmed = $script:walkConfirmed
$reloadInRange = $script:reloadInRange
$markerFinite = $script:markerFinite
$markerUnit = $script:markerUnit
$twoPassStable = $script:twoPassStable
$enumSeen = $script:enumSeen
$markerYawBins = $script:markerYawBins
$hullYawBins = $script:hullYawBins
$independentWindows = $script:independentWindows
$markerPitchBins = $script:markerPitchBins
$elevationIndependentWindows = $script:elevationIndependentWindows
$g2ClockSamples = $script:g2ClockSamples
$originInBand = $script:originInBand
$persistedSamples = $script:persistedSamples

$enumList = ($enumSeen | Sort-Object) -join ','
if ([string]::IsNullOrWhiteSpace($enumList)) { $enumList = 'none' }

Write-Host ('pen_fields: samples_ok=' + $ok)
Write-Host ('pen_fields: sample_errors=' + $errors)
Write-Host ('pen_fields: walk_confirmed=' + $walkConfirmed)
Write-Host ('pen_fields: reload_in_range=' + $reloadInRange)
Write-Host ('pen_fields: marker_finite=' + $markerFinite)
Write-Host ('pen_fields: marker_unit=' + $markerUnit)
Write-Host ('pen_fields: two_pass_stable=' + $twoPassStable)
Write-Host ('pen_fields: reload_enum_values=' + $enumList)
Write-Host ('pen_fields: marker_yaw_bins=' + $markerYawBins.Count)
Write-Host ('pen_fields: hull_yaw_bins=' + $hullYawBins.Count)
Write-Host ('pen_fields: turret_independent_windows=' + $independentWindows)
Write-Host ('pen_fields: marker_pitch_bins=' + $markerPitchBins.Count)
Write-Host ('pen_fields: elevation_independent_windows=' + $elevationIndependentWindows)
Write-Host ('pen_fields: g2_clock_samples=' + $g2ClockSamples)
Write-Host ('pen_fields: origin_in_band=' + $originInBand)

$samplesJson = ConvertTo-JsonArray $persistedSamples
[IO.File]::WriteAllText(
    $samplesPath,
    $samplesJson,
    (New-Object Text.ASCIIEncoding))
Write-Host 'pen_fields: samples_file=pen-semantic-fields-samples.json'

if ($ok -eq 0) {
    Write-Host 'pen_fields: FAILED_no_snapshots'
    exit 3
}

Write-Host 'pen_fields: OK'
exit 0
