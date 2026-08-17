#Requires -Version 5.1
<#
.SYNOPSIS
  Sample the coordinator-owned pen-semantic-fields snapshot during a
  verified offline replay.

.DESCRIPTION
  Polls OfflineReplayVerified, binds the launch artifact to its decoded
  session, then POSTs /api/v1/game/discover/entity-region with
  regionAnchor=pen-semantic-fields on a bounded cadence. The coordinator
  reuses the ownership walk and two-pass-reads the published gun-marker
  and VehicleGun reload/state block. This script prints only counts,
  enum histograms, and 16-sector yaw/pitch bins -- never addresses,
  tokens, paths, or world XYZ. Walk-confirmed same-clock samples are
  overwritten to %TEMP%\pen-semantic-fields-samples.json.

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

    [ValidateRange(1, 64)]
    [int]$Samples = 16,

    [ValidateRange(100, 5000)]
    [int]$CadenceMs = 200,

    [ValidateRange(0, 7)]
    [int]$OwnershipCandidateIndex = 0,

    [ValidateRange(1, 4096)]
    [int]$RegionLength = 16
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

$ok = 0
$errors = 0
$walkConfirmed = 0
$reloadInRange = 0
$markerFinite = 0
$markerUnit = 0
$twoPassStable = 0
$enumSeen = New-Object 'System.Collections.Generic.HashSet[int]'
$markerYawBins = New-Object 'System.Collections.Generic.HashSet[int]'
$markerPitchBins = New-Object 'System.Collections.Generic.HashSet[int]'
$hullYawBins = New-Object 'System.Collections.Generic.HashSet[int]'
$independentWindows = 0
$elevationIndependentWindows = 0
$g2ClockSamples = 0
$lastHullBin = $null
$lastMarkerBin = $null
$lastMarkerPitchBin = $null
$sameHullStreak = 0
$persistedSamples = New-Object System.Collections.ArrayList
$samplesPath = [System.IO.Path]::GetFullPath(
    (Join-Path $env:TEMP 'pen-semantic-fields-samples.json'))

for ($i = 0; $i -lt $Samples; $i++) {
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
        $ok++

        $walkOk = ($response.Status -eq 'Resolved') -and
            ($response.PenOwnershipOwnerPointerReadable -eq $true) -and
            ($response.PenOwnershipForwardRoundTripConfirmed -eq $true) -and
            ($response.PenOwnershipGunVtableConfirmed -eq $true) -and
            ($response.PenOwnershipEntityHpPlausible -eq $true) -and
            ($response.PenOwnershipTwoPassStable -eq $true)
        if ($walkOk) { $walkConfirmed++ }
        if ($response.PenSemanticReloadEnumInRange -eq $true) { $reloadInRange++ }
        if ($response.PenSemanticMarkerFinite -eq $true) { $markerFinite++ }
        if ($response.PenSemanticMarkerDirectionUnit -eq $true) { $markerUnit++ }
        if ($response.PenSemanticTwoPassStable -eq $true) { $twoPassStable++ }
        if ($null -ne $response.PenSemanticReloadEnum) {
            [void]$enumSeen.Add([int]$response.PenSemanticReloadEnum)
        }

        $markerBin = $null
        $markerPitchBin = $null
        $hullBin = $null
        if ($null -ne $response.PenSemanticMarkerYawRadians) {
            $markerBin = Get-YawBin ([double]$response.PenSemanticMarkerYawRadians)
            [void]$markerYawBins.Add($markerBin)
        }
        if (Test-FiniteNumber $response.PenSemanticMarkerPitchRadians) {
            $markerPitchBin = Get-PitchBin ([double]$response.PenSemanticMarkerPitchRadians)
            [void]$markerPitchBins.Add($markerPitchBin)
        }
        if ($null -ne $response.PenSemanticHullYawRadians) {
            $hullBin = Get-YawBin ([double]$response.PenSemanticHullYawRadians)
            [void]$hullYawBins.Add($hullBin)
        }

        $qualityOk = $walkOk -and
            ($response.PenSemanticMarkerFinite -eq $true) -and
            ($response.PenSemanticMarkerDirectionUnit -eq $true) -and
            ($response.PenSemanticTwoPassStable -eq $true)
        $sameClock = ($response.SameDecodedClockProven -eq $true)
        $replayTimeFinite = Test-FiniteNumber $response.ReplayTimeSeconds
        if ($sameClock -and $replayTimeFinite) {
            $g2ClockSamples++
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
            [void]$persistedSamples.Add([ordered]@{
                    replayTimeSeconds      = [double]$response.ReplayTimeSeconds
                    markerYawRadians       = $markerYawValue
                    markerPitchRadians     = $markerPitchValue
                    hullYawRadians         = $hullYawValue
                    reloadEnum             = $reloadEnum
                    sameDecodedClockProven = $true
                })
        }

        if ($null -ne $hullBin -and $null -ne $lastHullBin -and $hullBin -eq $lastHullBin) {
            $sameHullStreak++
            if ($sameHullStreak -ge 2 -and $null -ne $markerBin -and
                $null -ne $lastMarkerBin -and $markerBin -ne $lastMarkerBin) {
                $independentWindows++
            }
            if ($sameHullStreak -ge 2 -and $qualityOk -and
                $null -ne $markerBin -and $null -ne $lastMarkerBin -and
                $markerBin -eq $lastMarkerBin -and
                $null -ne $markerPitchBin -and $null -ne $lastMarkerPitchBin -and
                $markerPitchBin -ne $lastMarkerPitchBin) {
                $elevationIndependentWindows++
            }
        }
        else {
            $sameHullStreak = 0
        }
        $lastHullBin = $hullBin
        $lastMarkerBin = $markerBin
        $lastMarkerPitchBin = $markerPitchBin
    }
    catch {
        $errors++
        Write-Host ('pen_fields: sample_error i=' + $i + ' ' + $_.Exception.Message)
        $terminal = Get-LauncherFatalError
        if ($null -ne $terminal) {
            Write-Host ('pen_fields: stopping_launcher=' + $terminal)
            break
        }
    }

    Start-Sleep -Milliseconds $CadenceMs
}

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
