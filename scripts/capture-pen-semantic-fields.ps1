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
  enum histograms, and 16-sector yaw bins -- never addresses, tokens,
  paths, or raw coordinates.

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
    [int]$CadenceMs = 750,

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

function Get-YawBin([double]$Radians) {
    $twoPi = 6.283185307179586
    $shifted = $Radians + 3.141592653589793
    $normalized = $shifted - ([math]::Floor($shifted / $twoPi) * $twoPi)
    $bin = [int][math]::Floor(($normalized / $twoPi) * 16.0)
    if ($bin -lt 0) { return 0 }
    if ($bin -gt 15) { return 15 }
    return $bin
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
$hullYawBins = New-Object 'System.Collections.Generic.HashSet[int]'
$independentWindows = 0
$lastHullBin = $null
$lastMarkerBin = $null
$sameHullStreak = 0

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
        $hullBin = $null
        if ($null -ne $response.PenSemanticMarkerYawRadians) {
            $markerBin = Get-YawBin ([double]$response.PenSemanticMarkerYawRadians)
            [void]$markerYawBins.Add($markerBin)
        }
        if ($null -ne $response.PenSemanticHullYawRadians) {
            $hullBin = Get-YawBin ([double]$response.PenSemanticHullYawRadians)
            [void]$hullYawBins.Add($hullBin)
        }

        if ($null -ne $hullBin -and $null -ne $lastHullBin -and $hullBin -eq $lastHullBin) {
            $sameHullStreak++
            if ($sameHullStreak -ge 2 -and $null -ne $markerBin -and
                $null -ne $lastMarkerBin -and $markerBin -ne $lastMarkerBin) {
                $independentWindows++
            }
        }
        else {
            $sameHullStreak = 0
        }
        $lastHullBin = $hullBin
        $lastMarkerBin = $markerBin
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

if ($ok -eq 0) {
    Write-Host 'pen_fields: FAILED_no_snapshots'
    exit 3
}

Write-Host 'pen_fields: OK'
exit 0
