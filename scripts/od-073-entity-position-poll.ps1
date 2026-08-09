#Requires -Version 5.1
<#
.SYNOPSIS
  Polls the exact-build, module-rooted entity-position resolver during one
  positively verified offline replay.

.DESCRIPTION
  The script auto-selects the newest decoded battle and its single viewpoint
  entity, then sends only that replay entity ID to the server-owned resolver.
  It records aggregate match and movement evidence only: no entity ID,
  coordinates, process addresses, paths, raw bytes, or capability values are
  written to the result or console.

  A positive verdict proves a stable module-rooted polling family for this
  build and replay window. It does not prove hardware atomicity, exact decoded
  clock alignment, a single numeric offset, or offset-table promotion.

.EXITCODES
  0  Bounded campaign completed and a fresh aggregate result was written.
  1  Invalid options, missing rendezvous, or gate not verified.
  2  Ground truth/viewpoint unavailable.
  3  Polling failed before the requested bounded sample count.
  4  Aggregate result could not be created.
#>
[CmdletBinding()]
param(
    [string]$SessionId = '',
    [int]$WaitVerifiedSeconds = 180,
    [int]$StageDelaySeconds = 55,
    [int]$ReadCount = 24,
    [int]$ReadIntervalMilliseconds = 750,
    [string]$ResultPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($WaitVerifiedSeconds -lt 1 -or $WaitVerifiedSeconds -gt 300 -or
    $StageDelaySeconds -lt 0 -or $StageDelaySeconds -gt 180 -or
    $ReadCount -lt 3 -or $ReadCount -gt 120 -or
    $ReadIntervalMilliseconds -lt 100 -or $ReadIntervalMilliseconds -gt 5000) {
    Write-Host 'od073: FAILED_invalid_options'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $ResultPath = Join-Path $repoRoot ('.data\od-073-entity-position-poll-' + $stamp + '.json')
}
if (Test-Path -LiteralPath $ResultPath) {
    Write-Host 'od073: FAILED_result_exists'
    exit 4
}

function Test-OwnerOnlyFileAcl([string]$Path) {
    try {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = Get-Acl -LiteralPath $Path
        $observedOwner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $rules = @($acl.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        return $acl.AreAccessRulesProtected -and $observedOwner -eq $owner -and
            $rules.Count -eq 1 -and $rules[0].IdentityReference -eq $owner -and
            $rules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl)
    }
    catch {
        return $false
    }
}

function Get-Rendezvous {
    try {
        $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -eq $file -or -not (Test-OwnerOnlyFileAcl -Path $file.FullName)) {
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

function Invoke-OdApi {
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
        TimeoutSec = 15
        Headers    = @{
            'X-WotBTreader-Capability' = [string]$rendezvous.capability
        }
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 5 -Compress
    }

    return Invoke-RestMethod @arguments
}

function Get-Float32Hex([single]$Value) {
    return ([BitConverter]::ToString([BitConverter]::GetBytes($Value)) -replace '-', '')
}

function Write-AggregateResult([hashtable]$Aggregate) {
    try {
        $parent = Split-Path -Parent $ResultPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        $json = $Aggregate | ConvertTo-Json -Depth 6
        $encoding = New-Object Text.UTF8Encoding($false)
        $stream = [IO.File]::Open(
            $ResultPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $writer = New-Object IO.StreamWriter -ArgumentList $stream, $encoding
            try {
                $writer.Write($json)
                $writer.Flush()
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        Write-Host 'od073: FAILED_result_write'
        exit 4
    }
}

Write-Host 'od073: waiting_for_verified_gate'
$deadline = [DateTime]::UtcNow.AddSeconds($WaitVerifiedSeconds)
$verified = $false
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $state = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/game/state'
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
    Write-Host 'od073: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'od073: gate=OfflineReplayVerified'

try {
    $battleSessionId = $SessionId
    if ([string]::IsNullOrWhiteSpace($battleSessionId)) {
        $page = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=50'
        if ($null -eq $page -or $null -eq $page.items -or $page.items.Count -eq 0 -or
            $null -eq $page.items[0].session) {
            Write-Host 'od073: FAILED_no_decoded_session'
            exit 2
        }
        $battleSessionId = [string]$page.items[0].session.battleSessionId
    }

    $trajectory = Invoke-OdApi -Method 'Get' -RelativePath (
        '/api/v1/game/discover/trajectory/' + $battleSessionId)
    $viewpoints = @($trajectory.entities | Where-Object {
        $_.isViewpoint -eq $true -and $null -ne $_.entityId
    })
    if ($viewpoints.Count -ne 1 -or $null -eq $viewpoints[0].samples -or
        $viewpoints[0].samples.Count -lt 2) {
        Write-Host 'od073: FAILED_viewpoint_ground_truth'
        exit 2
    }
    $viewpoint = $viewpoints[0]
    $viewpointEntityId = [int]$viewpoint.entityId
}
catch {
    Write-Host 'od073: FAILED_ground_truth_api'
    exit 2
}

Write-Host ('od073: stage_delay_s=' + $StageDelaySeconds)
if ($StageDelaySeconds -gt 0) {
    Start-Sleep -Seconds $StageDelaySeconds
}

$statusCounts = @{}
$resolvedCount = 0
$exactMatchCount = 0
$withinOneCount = 0
$withinThreeCount = 0
$distinctPositions = New-Object 'Collections.Generic.HashSet[string]'
$minimumDistance = [double]::MaxValue
$maximumDistance = 0.0
$buildVersion = ''
$allModuleRooted = $true
$allIdentityRevalidated = $true
$allConsistentDoubleRead = $true
$anyHardwareAtomic = $false
$anySameDecodedClock = $false

for ($index = 0; $index -lt $ReadCount; $index++) {
    try {
        $response = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/entity-position' `
            -Body @{ entityId = $viewpointEntityId }
    }
    catch {
        Write-Host 'od073: FAILED_poll_api'
        exit 3
    }

    $status = [string]$response.status
    if (-not $statusCounts.ContainsKey($status)) {
        $statusCounts[$status] = 0
    }
    $statusCounts[$status] = [int]$statusCounts[$status] + 1
    $buildVersion = [string]$response.gameVersion

    if ($status -eq 'Resolved' -and $null -ne $response.x -and
        $null -ne $response.y -and $null -ne $response.z) {
        $resolvedCount += 1
        $x = [single]$response.x
        $y = [single]$response.y
        $z = [single]$response.z
        $xHex = Get-Float32Hex -Value $x
        $yHex = Get-Float32Hex -Value $y
        $zHex = Get-Float32Hex -Value $z
        $key = $xHex + ':' + $yHex + ':' + $zHex
        [void]$distinctPositions.Add($key)

        $bestDistance = [double]::MaxValue
        $exact = $false
        foreach ($sample in $viewpoint.samples) {
            $sx = [single]$sample.x
            $sy = [single]$sample.y
            $sz = [single]$sample.z
            if ($xHex -eq (Get-Float32Hex -Value $sx) -and
                $yHex -eq (Get-Float32Hex -Value $sy) -and
                $zHex -eq (Get-Float32Hex -Value $sz)) {
                $exact = $true
            }

            $dx = [double]$x - [double]$sx
            $dy = [double]$y - [double]$sy
            $dz = [double]$z - [double]$sz
            $distance = [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
            if ($distance -lt $bestDistance) {
                $bestDistance = $distance
            }
        }

        if ($exact) { $exactMatchCount += 1 }
        if ($bestDistance -le 1.0) { $withinOneCount += 1 }
        if ($bestDistance -le 3.0) { $withinThreeCount += 1 }
        if ($bestDistance -lt $minimumDistance) { $minimumDistance = $bestDistance }
        if ($bestDistance -gt $maximumDistance) { $maximumDistance = $bestDistance }
        $allModuleRooted = $allModuleRooted -and [bool]$response.moduleRooted
        $allIdentityRevalidated = $allIdentityRevalidated -and
            [bool]$response.entityIdentityRevalidated
        $allConsistentDoubleRead = $allConsistentDoubleRead -and
            [bool]$response.consistentDoubleRead
        $anyHardwareAtomic = $anyHardwareAtomic -or [bool]$response.hardwareAtomicReadProven
        $anySameDecodedClock = $anySameDecodedClock -or [bool]$response.sameDecodedClockProven
    }

    if ($index -lt ($ReadCount - 1)) {
        Start-Sleep -Milliseconds $ReadIntervalMilliseconds
    }
}

$minimumDistanceValue = if ($resolvedCount -eq 0) { $null } else { $minimumDistance }
$maximumDistanceValue = if ($resolvedCount -eq 0) { $null } else { $maximumDistance }
$moving = $distinctPositions.Count -ge 2
$trajectoryConsistent = $resolvedCount -gt 0 -and
    ($exactMatchCount -gt 0 -or $withinOneCount -ge [Math]::Ceiling($resolvedCount / 4.0)) -and
    $withinThreeCount -ge [Math]::Ceiling($resolvedCount / 2.0)
$verdict = if ($resolvedCount -eq $ReadCount -and $moving -and
    $trajectoryConsistent -and $allModuleRooted -and
    $allIdentityRevalidated -and $allConsistentDoubleRead -and
    -not $anyHardwareAtomic -and -not $anySameDecodedClock) {
    'stable-resolver-positive'
}
else {
    'honest-negative-or-inconclusive'
}

$aggregate = [ordered]@{
    schema = 'wotbtreader.od073.entity-position-poll.v1'
    campaign = 'od-073'
    completedAtUtc = [DateTime]::UtcNow.ToString('o')
    verdict = $verdict
    expectedGameVersion = '11.19.0.10'
    gameVersion = $buildVersion
    requestedReads = $ReadCount
    resolvedReads = $resolvedCount
    statusCounts = $statusCounts
    distinctPositionCount = $distinctPositions.Count
    moving = $moving
    exactRetainedTrajectoryMatches = $exactMatchCount
    withinOneWorldUnit = $withinOneCount
    withinThreeWorldUnits = $withinThreeCount
    minimumRetainedTrajectoryDistance = $minimumDistanceValue
    maximumRetainedTrajectoryDistance = $maximumDistanceValue
    trajectoryConsistent = $trajectoryConsistent
    allModuleRooted = $allModuleRooted
    allEntityIdentityRevalidated = $allIdentityRevalidated
    allConsistentDoubleRead = $allConsistentDoubleRead
    hardwareAtomicReadProven = $anyHardwareAtomic
    sameDecodedClockProven = $anySameDecodedClock
    moduleRootedLayoutStaticEvidence = $true
    stableRootLiveRepeatabilityProven = $false
    offsetTablePromotionReady = $false
    privacy = [ordered]@{
        entityIdsPersisted = $false
        coordinatesPersisted = $false
        processAddressesPersisted = $false
        rawBytesPersisted = $false
        capabilityPersisted = $false
    }
}

Write-AggregateResult -Aggregate $aggregate
Write-Host ('od073: verdict=' + $verdict +
    ' resolved=' + $resolvedCount + '/' + $ReadCount +
    ' distinct=' + $distinctPositions.Count +
    ' exact=' + $exactMatchCount +
    ' within1=' + $withinOneCount +
    ' within3=' + $withinThreeCount)
exit 0
