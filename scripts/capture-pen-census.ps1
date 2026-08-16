#Requires -Version 5.1
<#
.SYNOPSIS
  Run one coordinator-owned penetration owner-census capture against the
  currently verified offline replay launch.

.DESCRIPTION
  Decoupled from launch-offline-replay-for-od.ps1 on purpose: the launcher's
  job is to reach OfflineReplayVerified; this script polls that gate itself,
  binds the launch artifact to its decoded run, and POSTs
  /api/v1/game/discover/pen-capture. Run it concurrently with (or right after)
  the launcher so a launcher hang or a late clock-anchor cannot starve the
  battle capture window.

  The endpoint is capability- and loopback-gated and the coordinator owns every
  read location, so this script carries only the opaque decodeRunId and prints
  only the privacy-safe evaluation (no address, path, token, or raw bytes).

.EXITCODES
  0  Capture completed and the evaluation was printed.
  1  Gate never reached OfflineReplayVerified within the bound.
  2  Launch artifact / decode-run binding unavailable.
  3  pen-capture endpoint returned an error.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 600)]
    [int]$WaitVerifiedSeconds = 300
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

    return Invoke-RestMethod @arguments
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

# ---- 1. Gate poll ----------------------------------------------------------
Write-Host 'pen_census: waiting_for_verified_gate'
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
    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'pen_census: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'pen_census: gate=OfflineReplayVerified'

# ---- 2. Artifact + decode-run binding --------------------------------------
$artifactId = Get-LaunchArtifactId
if ([string]::IsNullOrWhiteSpace($artifactId)) {
    Write-Host 'pen_census: FAILED_launch_artifact_binding'
    exit 2
}

$page = Invoke-PenApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
$artifactSessions = @($page.items | Where-Object {
    $null -ne $_.session -and
    [string]$_.decodeRun.sourceArtifactId -eq $artifactId
})
if ($artifactSessions.Count -eq 0) {
    Write-Host 'pen_census: FAILED_no_decoded_session'
    exit 2
}
$decodeRunId = [string]$artifactSessions[0].decodeRun.decodeRunId
$battleSessionId = [string]$artifactSessions[0].session.battleSessionId
Write-Host ('pen_census: bound_battle_session=' + $battleSessionId)

# ---- 3. Capture --------------------------------------------------------------
$response = Invoke-PenApi -Method 'Post' `
    -RelativePath '/api/v1/game/discover/pen-capture' `
    -Body @{ decodeRunId = $decodeRunId }

Write-Host ('pen_census: status=' + $response.Status)
Write-Host ('pen_census: primary_reason=' + $response.PrimaryReason)
Write-Host ('pen_census: reasons=' + (@($response.Reasons) -join ','))
Write-Host ('pen_census: owner_candidate_count=' + $response.OwnerCandidateCount)
Write-Host ('pen_census: exact_weapon_owner_proven=' + $response.ExactWeaponOwnerProven)
Write-Host ('pen_census: exact_loaded_shell_proven=' + $response.ExactLoadedShellProven)
Write-Host ('pen_census: exact_gun_ray_proven=' + $response.ExactGunRayProven)
Write-Host ('pen_census: shell_states=' + $response.ShellStatesObserved +
    ' shell_id_matches=' + $response.ShellIdentityMatches +
    ' aim=' + $response.AimSamples +
    ' ray=' + $response.RaySamples +
    ' joined_ray=' + $response.JoinedRaySamples)

if ($response.Status -eq 'Rejected' -or $response.Status -eq 'NotReady') {
    Write-Host 'pen_census: honest_negative (no field promoted)'
}
else {
    Write-Host 'pen_census: positive_verdict (record + promote per gates)'
}

exit 0
