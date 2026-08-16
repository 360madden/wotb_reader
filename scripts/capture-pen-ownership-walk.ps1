#Requires -Version 5.1
<#
.SYNOPSIS
  Run one coordinator-owned pen-ownership-walk read against the currently
  verified offline replay launch.

.DESCRIPTION
  Decoupled from launch-offline-replay-for-od.ps1 on purpose: the launcher's
  job is to reach OfflineReplayVerified; this script polls that gate itself,
  binds the launch artifact to its decoded run, and POSTs
  /api/v1/game/discover/entity-region with regionAnchor=pen-ownership-walk.
  The coordinator owns every read location (gated rotator vftable AOB scan,
  rotator identity re-read, then the five-read chain twice), so this script
  carries no address, offset, or pointer and prints only the aggregate
  verdict booleans/counts.

  Adjudication: H1 is CONFIRMED only when Status=Resolved AND all four chain
  booleans are true AND TwoPassStable. Every other outcome is an honest
  negative or a fail-closed environment read; nothing is promoted here.

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

    # Which rotator scan candidate (0..7) to validate. The scan expects
    # exactly one live VehicleGunRotator; the index is for the rare
    # ambiguous-scan case and is bounded by the coordinator.
    [ValidateRange(0, 7)]
    [int]$OwnershipCandidateIndex = 0,

    # Region length is validated but ignored by the pen-ownership-walk anchor
    # (the coordinator returns verdicts, not bytes). Kept bounded.
    [ValidateRange(1, 4096)]
    [int]$RegionLength = 16
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
Write-Host 'pen_walk: waiting_for_verified_gate'
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
        Write-Host ('pen_walk: FAILED_launcher=' + $launcherFatal)
        exit 1
    }

    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'pen_walk: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'pen_walk: gate=OfflineReplayVerified'

# ---- 2. Artifact + decode-run binding --------------------------------------
$artifactId = Get-LaunchArtifactId
if ([string]::IsNullOrWhiteSpace($artifactId)) {
    Write-Host 'pen_walk: FAILED_launch_artifact_binding'
    exit 2
}

$page = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
$artifactSessions = @($page.items | Where-Object {
    $null -ne $_.session -and
    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
})
if ($artifactSessions.Count -eq 0) {
    Write-Host 'pen_walk: FAILED_no_decoded_session'
    exit 2
}
$battleSessionId = [string]$artifactSessions[0].session.battleSessionId
Write-Host ('pen_walk: bound_battle_session=' + $battleSessionId)

# ---- 3. Ownership-walk read -------------------------------------------------
$body = @{
    entityId              = 0
    regionLength          = $RegionLength
    regionAnchor          = 'pen-ownership-walk'
    battleSessionId       = $battleSessionId
    ownershipCandidateIndex = $OwnershipCandidateIndex
}
$response = Invoke-PenApi -Method 'Post' `
    -RelativePath '/api/v1/game/discover/entity-region' `
    -Body $body

Write-Host ('pen_walk: status=' + $response.Status)
Write-Host ('pen_walk: failure_stage=' + $response.FailureStage)
Write-Host ('pen_walk: rotator_candidate_count=' + $response.PenOwnershipRotatorCandidateCount)
Write-Host ('pen_walk: owner_pointer_readable=' + $response.PenOwnershipOwnerPointerReadable)
Write-Host ('pen_walk: forward_round_trip=' + $response.PenOwnershipForwardRoundTripConfirmed)
Write-Host ('pen_walk: gun_vtable=' + $response.PenOwnershipGunVtableConfirmed)
Write-Host ('pen_walk: entity_hp_plausible=' + $response.PenOwnershipEntityHpPlausible)
Write-Host ('pen_walk: two_pass_stable=' + $response.PenOwnershipTwoPassStable)
Write-Host ('pen_walk: same_decoded_clock=' + $response.SameDecodedClockProven)
Write-Host ('pen_walk: replay_time_seconds=' + $response.ReplayTimeSeconds)
Write-Host ('pen_walk: region_base64_is_null=' + ($null -eq $response.RegionBase64))

$confirmed = ($response.Status -eq 'Resolved') -and
    ($response.PenOwnershipOwnerPointerReadable -eq $true) -and
    ($response.PenOwnershipForwardRoundTripConfirmed -eq $true) -and
    ($response.PenOwnershipGunVtableConfirmed -eq $true) -and
    ($response.PenOwnershipEntityHpPlausible -eq $true) -and
    ($response.PenOwnershipTwoPassStable -eq $true)
if ($confirmed) {
    Write-Host 'pen_walk: H1_CONFIRMED (ownership chain verified live)'
}
else {
    Write-Host 'pen_walk: honest_negative_or_fail_closed (H1 NOT confirmed)'
}

exit 0
