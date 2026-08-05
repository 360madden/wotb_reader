#Requires -Version 5.1
<#
.SYNOPSIS
  OD-048 monitor-and-correlate session driver (strategy v4, M1): stage
  candidate addresses from the decoded replay trajectory, monitor them while
  the replay plays, and correlate each address's value series against the
  replay's known trajectory.

.DESCRIPTION
  This is the replay-guided correlation layer that replaces the exact-pause
  scan (OD-047 M1). No precise pause is required: the replay plays at 1x, the
  driver re-reads a fixed staged address set every -ReadIntervalSeconds, and
  the host scorer (TrajectoryCorrelationScorer) matches each address's value
  series against the decoded replay's per-entity trajectories with a
  time-shift sweep, per axis, with sign flips. The winning evidence is an
  address that reproduces the movement sequence with direction/speed changes.

  Shift audit: survivors whose winning shift rides the sweep EDGE (within 2s
  of -MaxTimeShiftSeconds) are demoted from strong to suspect
  (verdict evidence-edge-aligned) and listed under suspectEdgeAligned -- a
  boundary-aligned shift means the true alignment is probably beyond the
  sweep (bad anchor or load latency exceeded the bound).

  Staging: the driver fetches the decoded session trajectory (viewpoint
  entity first, then the most-moving entities), waits -StageDelaySeconds for
  the battle to load after the Start marker, then scans the game process for
  Float values near the ground-truth sample nearest the expected current
  replay tick (one scan per axis). The tolerance is auto-scaled from the
  entity's maximum speed x the load-latency bound, so the band covers the
  battle entity's live position regardless of load jitter. The union is the
  staged set; the scan retries until it finds candidates (battle loaded).

  No operator input is needed after the replay launches. The battle plays to
  completion; the gate revokes at battle end and the driver stops.

.EXITCODES
  0  Campaign completed; report written to .data\od-048-<timestamp>.json
  1  Preflight failure (no host/rendezvous, gate never verified)
  2  Staging failure (trajectory unavailable or scan failed)
  3  Monitor failure (read loop aborted before completion)
  4  Correlate failure
  5  Report could not be written
#>
[CmdletBinding()]
param(
    # Decoded battle session GUID providing the ground truth. When empty, the
    # driver auto-picks the most recent decoded session from the read API.
    [string]$SessionId = '',
    [int]$WaitVerifiedSeconds = 300,
    # How many entities to stage on: the viewpoint plus the most-moving
    # entities. Each staged entity costs three scans (x/y/z of its first
    # position sample).
    [int]$StageTopN = 3,
    # FloatTolerance for each staging scan (world units). Absolute coordinate
    # bands are rare in memory, so even a loose tolerance yields a small set.
    [double]$ScanTolerance = 8.0,
    # Hard cap on the staged union.
    [int]$MaxStaged = 3000,
    # Load-settle delay after the gate verifies (the Start marker fires when
    # loading BEGINS; the battle entities with live positions exist only after
    # LoadGameScene completes). Staging scans run after this delay.
    [int]$StageDelaySeconds = 15,
    # Staging attempts: each retry re-estimates the current replay tick and
    # rescans with a fresh delay, so a battle that is still loading on the
    # first attempt is caught on a later one.
    [int]$MaxStagingAttempts = 3,
    [double]$ReadIntervalSeconds = 2.0,
    # Read rounds; the battle length (duration_ticks / 10MHz) bounds the useful
    # window. Dead Rail is ~271s; default 90 rounds at 2s covers ~180s.
    [int]$MaxReadRounds = 90,
    # Per-axis correlation tolerance (world units).
    [double]$TolerancePerAxis = 6.0,
    # Time-shift sweep bound (seconds); absorbs Start-marker anchor error AND
    # load latency (the battle starts at tick 0 some seconds AFTER the Start
    # marker, so the observed series trails the anchor by the load time).
    # 30s covers observed load latencies; the server cap is 120.
    [int]$MaxTimeShiftSeconds = 30,
    # Observed series with a span below this are treated as constants.
    [double]$MinMovingSpan = 0.5,
    # Addresses per /discover/read call (server cap is 2000).
    [int]$ReadChunk = 500,
    # Optional wall-clock anchor override (ISO-8601 UTC) captured from the
    # replay Start marker. When empty, the driver anchors at the moment it
    # first observes the verified gate -- correct when the driver starts
    # before the replay reaches battle start; see the WARNING printed on the
    # first poll when the gate is already verified.
    [string]$ReplayStartWallTimeUtc = '',
    # JSON summary output. Default .data\od-048-<timestamp>.json (runtime
    # data, never tracked).
    [string]$ResultPath = '',
    [string]$RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $dataDir = Join-Path $RepoRoot '.data'
    if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
    $ResultPath = Join-Path $dataDir ("od-048-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
}

function Write-Od048([string]$Message) {
    Write-Host ("od048: " + $Message)
}

function Get-Rendezvous {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem $dir -File -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $file) { return $null }
        return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
    }
    catch { return $null }
}

function Get-GateState {
    param([object]$Rendezvous)
    try {
        if (-not $Rendezvous) { return $null }
        return Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/state') -Headers @{
            'X-WotBTreader-Capability' = [string]$Rendezvous.capability
        }
    }
    catch { return $null }
}

function Invoke-Api {
    param(
        [object]$Rendezvous,
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )
    $params = @{
        Uri     = $Rendezvous.baseUri + $RelativePath
        Method  = $Method
        Headers = @{ 'X-WotBTreader-Capability' = [string]$Rendezvous.capability }
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }
    try {
        return Invoke-RestMethod @params
    }
    catch { return $null }
}

# Float -> little-endian hex for the staging scan.
function Convert-ToFloatHex {
    param([double]$Value)
    $bytes = [BitConverter]::GetBytes([float]$Value)
    return ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Test-FiniteDouble {
    param([double]$Value)
    return -not ([double]::IsNaN($Value) -or [double]::IsInfinity($Value))
}

# -- Preflight: rendezvous + verified gate --
Write-Od048 'preflight_start'
$rendezvous = Get-Rendezvous
if (-not $rendezvous) {
    Write-Od048 'FAILED_no_rendezvous'
    exit 1
}

Write-Od048 'waiting_for_verified_gate'
$deadline = (Get-Date).AddSeconds($WaitVerifiedSeconds)
$state = $null
$replayStartWallUtc = $ReplayStartWallTimeUtc
$pollCount = 0
while ((Get-Date) -lt $deadline) {
    $pollCount += 1
    $state = Get-GateState -Rendezvous $rendezvous
    if ($state -and $state.verificationState -eq 'OfflineReplayVerified') {
        Write-Od048 'gate=OfflineReplayVerified'
        if ([string]::IsNullOrWhiteSpace($replayStartWallUtc)) {
            $replayStartWallUtc = ([DateTime]::UtcNow).ToString('o')
        }
        if ($pollCount -eq 1) {
            Write-Od048 'WARNING anchor_captured_after_verified - if the battle was already underway when this driver started, the wall anchor is wrong and the run will find no evidence. Restart the driver BEFORE the replay reaches battle start, or pass -ReplayStartWallTimeUtc from the Start marker.'
        }
        break
    }
    $vs = if ($state) { [string]$state.verificationState } else { 'no-host' }
    Write-Od048 ("waiting gate=" + $vs)
    Start-Sleep -Seconds 2
}
if ($null -eq $state -or $state.verificationState -ne 'OfflineReplayVerified') {
    Write-Od048 'FAILED_gate_never_verified'
    exit 1
}

Write-Od048 ("staging_delay_s=" + $StageDelaySeconds)
if ($StageDelaySeconds -gt 0) { Start-Sleep -Seconds $StageDelaySeconds }

# -- Ground truth --
$battleSessionId = $SessionId
if ([string]::IsNullOrWhiteSpace($battleSessionId)) {
    $page = Invoke-Api -Rendezvous $rendezvous -Method 'Get' -RelativePath '/api/v1/read/sessions?limit=50'
    if ($null -eq $page -or $page.items.Count -eq 0) {
        Write-Od048 'FAILED_no_decoded_session'
        exit 2
    }
    $battleSessionId = [string]$page.items[0].session.id
}
Write-Od048 ("ground_truth_session=" + $battleSessionId)

$trajectory = Invoke-Api -Rendezvous $rendezvous -Method 'Get' -RelativePath ('/api/v1/game/discover/trajectory/' + $battleSessionId)
if ($null -eq $trajectory -or $trajectory.entities.Count -eq 0) {
    Write-Od048 'FAILED_trajectory_unavailable'
    exit 2
}
Write-Od048 ("duration_ticks=" + $trajectory.durationTicks)

# -- Staging: viewpoint first, then most-moving entities --
$scored = @()
foreach ($entity in $trajectory.entities) {
    $minX = [double]::MaxValue; $maxX = [double]::MinValue
    $minY = [double]::MaxValue; $maxY = [double]::MinValue
    $minZ = [double]::MaxValue; $maxZ = [double]::MinValue
    $maxSpeed = 0.0
    $prevSample = $null
    foreach ($sample in $entity.samples) {
        if ($sample.x -lt $minX) { $minX = $sample.x }
        if ($sample.x -gt $maxX) { $maxX = $sample.x }
        if ($sample.y -lt $minY) { $minY = $sample.y }
        if ($sample.y -gt $maxY) { $maxY = $sample.y }
        if ($sample.z -lt $minZ) { $minZ = $sample.z }
        if ($sample.z -gt $maxZ) { $maxZ = $sample.z }
        if ($null -ne $prevSample) {
            $dtTicks = [double]$sample.replayTimeTicks - [double]$prevSample.replayTimeTicks
            if ($dtTicks -gt 0) {
                $dx = [double]$sample.x - [double]$prevSample.x
                $dy = [double]$sample.y - [double]$prevSample.y
                $dz = [double]$sample.z - [double]$prevSample.z
                $dist = [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
                $speed = $dist / ($dtTicks / 10000000.0)
                if ($speed -gt $maxSpeed) { $maxSpeed = $speed }
            }
        }
        $prevSample = $sample
    }
    $movement = ($maxX - $minX) + ($maxY - $minY) + ($maxZ - $minZ)
    $scored += [pscustomobject]@{
        EntityId    = $entity.entityId
        TankName    = $entity.tankName
        IsViewpoint = $entity.isViewpoint
        Movement    = $movement
        MaxSpeed    = $maxSpeed
        Samples     = $entity.samples
    }
}

$maxSpeedGlobal = 0.0
if ($scored.Count -gt 0) {
    $speedMax = $scored | Measure-Object -Property MaxSpeed -Maximum
    if ($null -ne $speedMax -and $null -ne $speedMax.Maximum) {
        $maxSpeedGlobal = [double]$speedMax.Maximum
    }
}
# The scan band must cover the entity's live position despite unknown load
# latency: tolerance = maxSpeed x (load-latency bound) x 1.5 safety margin.
# 25s beyond the settle delay covers observed LoadGameScene times, and the
# margin covers the downsampled-series peak-speed underestimate (fast bursts
# are averaged out); capped at 800 world units so the staged union stays
# bounded (the correlate filter rejects decoys anyway).
$stagingTolerance = [Math]::Max([double]$ScanTolerance,
    [Math]::Min(800.0, $maxSpeedGlobal * 1.5 * ($StageDelaySeconds + 25)))
Write-Od048 ("max_speed=" + [Math]::Round($maxSpeedGlobal, 2) + " staging_tolerance=" + [Math]::Round($stagingTolerance, 2))

function Select-NearestSample {
    param([object]$Samples, [long]$TargetTick)
    $best = $null
    $bestDistance = [long]::MaxValue
    foreach ($s in $Samples) {
        $distance = [Math]::Abs(([long]$s.replayTimeTicks) - $TargetTick)
        if ($distance -lt $bestDistance) {
            $bestDistance = $distance
            $best = $s
        }
    }
    if ($null -eq $best) { return $Samples[0] }
    return $best
}

$stagingEntities = @(
    ($scored | Where-Object { $_.IsViewpoint } | Select-Object -First 1)
    ($scored | Where-Object { -not $_.IsViewpoint } | Sort-Object Movement -Descending | Select-Object -First ($StageTopN - 1))
) | Where-Object { $null -ne $_ }

if ($stagingEntities.Count -eq 0) {
    Write-Od048 'FAILED_no_staging_entity'
    exit 2
}
$stagingEntities = @($stagingEntities | Select-Object -First $StageTopN)
Write-Od048 ("staging_entities=" + $stagingEntities.Count)

# Stage on the ground-truth sample nearest the expected current replay tick
# (elapsed since the anchor), not the tick-0 sample: the battle entities with
# live positions exist only after load, and by scan time the tank is seconds
# into the battle. The speed-scaled tolerance absorbs the unknown load latency;
# the shift sweep then aligns the observed series to the ground truth.
$anchorUtc = [datetime]$replayStartWallUtc
$staged = [System.Collections.Generic.List[string]]::new()
$stagedEntitiesReport = @()
$stagingAttempt = 0
while ($stagingAttempt -lt $MaxStagingAttempts) {
    $stagingAttempt += 1
    $staged.Clear()
    $stagedEntitiesReport = @()
    $elapsedSeconds = ((Get-Date).ToUniversalTime() - $anchorUtc).TotalSeconds
    if ($elapsedSeconds -lt 0) { $elapsedSeconds = 0 }
    $stageTickEstimate = [long]($elapsedSeconds * 10000000.0)
    Write-Od048 ("staging attempt={0} elapsed_s={1} tick_est={2}" -f $stagingAttempt, [Math]::Round($elapsedSeconds, 1), $stageTickEstimate)

    foreach ($entity in $stagingEntities) {
        $sample = Select-NearestSample -Samples $entity.Samples -TargetTick $stageTickEstimate
        $axisValues = @{ x = $sample.x; y = $sample.y; z = $sample.z }
        foreach ($axis in @('x', 'y', 'z')) {
            $axisValue = [double]$axisValues[$axis]
            if (-not (Test-FiniteDouble -Value $axisValue)) { continue }
            $scanBody = @{
                FieldName        = ('corr-' + $axis + '-' + [string]$entity.EntityId)
                FieldType        = 'Float'
                ExpectedValueHex = (Convert-ToFloatHex -Value $axisValue)
                FloatTolerance   = $stagingTolerance
                MaxCandidates    = 10000
                MinRegionSize    = 4096
                Alignment        = 1
            }
            $scan = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover' -Body $scanBody
            if ($null -eq $scan -or $null -eq $scan.candidates) {
                Write-Od048 ('FAILED_staging_scan axis=' + $axis)
                exit 2
            }
            foreach ($candidate in $scan.candidates) {
                if ($staged.Count -ge $MaxStaged) { break }
                # Keep the canonical "0x..." form end-to-end (staging, read
                # batches, series keys, report) so no decimal/hex mismatch can
                # split an address across two identities.
                $hexAddress = [string]$candidate.absoluteAddress
                if ($hexAddress -notmatch '^0x[0-9a-fA-F]+$') { continue }
                if (-not $staged.Contains($hexAddress)) { $staged.Add($hexAddress) }
            }
        }
        $stagedEntitiesReport += [pscustomobject]@{
            EntityId    = $entity.EntityId
            TankName    = $entity.TankName
            IsViewpoint = $entity.IsViewpoint
        }
    }

    if ($staged.Count -ge 3) { break }
    if ($stagingAttempt -lt $MaxStagingAttempts) {
        Write-Od048 'staging retry in 15s (battle may still be loading)'
        Start-Sleep -Seconds 15
    }
}
if ($staged.Count -lt 3) {
    Write-Od048 ("FAILED_staging_too_small staged=" + $staged.Count)
    exit 2
}
Write-Od048 ("staged=" + $staged.Count)

# -- Monitor loop --
$series = [System.Collections.Generic.Dictionary[string, object]]::new()
$round = 0
$readCalls = 0
$readOkSamples = 0
$stoppedReason = 'rounds-exhausted'
while ($round -lt $MaxReadRounds) {
    $round++
    $gate = Get-GateState -Rendezvous $rendezvous
    if ($null -eq $gate -or $gate.verificationState -ne 'OfflineReplayVerified') {
        $stoppedReason = 'gate-lost'
        Write-Od048 ("monitor_stop gate=" + (if ($null -eq $gate) { 'no-host' } else { [string]$gate.verificationState }))
        break
    }

    $addressBatch = @()
    $i = 0
    foreach ($address in $staged) {
        $addressBatch += $address
        $i++
        if ($i -ge $ReadChunk) {
            $readCalls += 1
            $readBody = @{
                Addresses = $addressBatch
                ValueKind = 'Float'
                ValueSize = 4
            }
            $read = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/read' -Body $readBody
            if ($null -ne $read -and $null -ne $read.reads) {
                $wallNow = ([DateTime]::UtcNow).ToString('o')
                foreach ($item in $read.reads) {
                    if (-not $item.readOk) { continue }
                    $value = [double]::Parse([string]$item.valueSummary, [Globalization.CultureInfo]::InvariantCulture)
                    if (-not (Test-FiniteDouble -Value $value)) { continue }
                    $readOkSamples += 1
                    if ($series.ContainsKey([string]$item.absoluteAddress)) {
                        $list = [System.Collections.Generic.List[object]]$series[[string]$item.absoluteAddress]
                        $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                    }
                    else {
                        $list = [System.Collections.Generic.List[object]]::new()
                        $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                        $series[[string]$item.absoluteAddress] = $list
                    }
                }
            }
            $addressBatch = @()
            $i = 0
        }
    }
    if ($addressBatch.Count -gt 0) {
        $readCalls += 1
        $readBody = @{
            Addresses = $addressBatch
            ValueKind = 'Float'
            ValueSize = 4
        }
        $read = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/read' -Body $readBody
        if ($null -ne $read -and $null -ne $read.reads) {
            $wallNow = ([DateTime]::UtcNow).ToString('o')
            foreach ($item in $read.reads) {
                if (-not $item.readOk) { continue }
                $value = [double]::Parse([string]$item.valueSummary, [Globalization.CultureInfo]::InvariantCulture)
                if (-not (Test-FiniteDouble -Value $value)) { continue }
                $readOkSamples += 1
                if ($series.ContainsKey([string]$item.absoluteAddress)) {
                    $list = [System.Collections.Generic.List[object]]$series[[string]$item.absoluteAddress]
                    $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                }
                else {
                    $list = [System.Collections.Generic.List[object]]::new()
                    $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                    $series[[string]$item.absoluteAddress] = $list
                }
            }
        }
    }

    if (($round % 10) -eq 0) {
        Write-Od048 ("round={0}/{1} series={2} samples={3}" -f $round, $MaxReadRounds, $series.Count, $readOkSamples)
    }
    if ($round -lt $MaxReadRounds) {
        Start-Sleep -Milliseconds ([int]($ReadIntervalSeconds * 1000))
    }
}
Write-Od048 ("monitor done rounds={0} reason={1} series={2}" -f $round, $stoppedReason, $series.Count)

# -- Correlate --
$observations = @()
foreach ($addressKey in $series.Keys) {
    $list = [System.Collections.Generic.List[object]]$series[$addressKey]
    if ($list.Count -lt 2) { continue }
    $observations += @{
        Address = $addressKey
        Samples = @($list | ForEach-Object {
            @{ wallTimeUtc = $_.wallTimeUtc; value = $_.value }
        })
    }
}
if ($observations.Count -eq 0) {
    Write-Od048 'FAILED_no_observations_for_correlate'
    exit 3
}

$correlateBody = @{
    groundTruthSessionId   = $battleSessionId
    replayStartWallTimeUtc = $replayStartWallUtc
    tolerancePerAxis       = $TolerancePerAxis
    maxTimeShiftSeconds    = $MaxTimeShiftSeconds
    minMovingSpan          = $MinMovingSpan
    observations           = $observations
}
$correlated = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/correlate' -Body $correlateBody
if ($null -eq $correlated -or $null -eq $correlated.results) {
    Write-Od048 'FAILED_correlate'
    exit 4
}

$results = @($correlated.results)

# Shift audit: a survivor whose winning shift rides the sweep EDGE means the
# true alignment is probably beyond the sweep (anchor wrong or load latency
# exceeded the bound) -- the classic bad-anchor false positive. Demote those
# from "strong" to "suspect" so a broken anchor cannot masquerade as evidence.
$edgeThreshold = [Math]::Max(2, $MaxTimeShiftSeconds - 2)
$edgeAlignedSurvivors = @()
foreach ($result in $results) {
    $shift = if ($null -eq $result.shiftSeconds) { 0.0 } else { [double]$result.shiftSeconds }
    $isEdgeAligned = ([Math]::Abs($shift) -ge $edgeThreshold)
    $result | Add-Member -NotePropertyName edgeAligned -NotePropertyValue $isEdgeAligned -Force
    if ($isEdgeAligned -and $result.score -ge 0.7) {
        $edgeAlignedSurvivors += $result
    }
}
$strongSurvivors = @($results | Where-Object { $_.score -ge 0.7 -and -not $_.edgeAligned })
$verdict = if ($strongSurvivors.Count -gt 0) { 'evidence-strong' }
    elseif ($edgeAlignedSurvivors.Count -gt 0) { 'evidence-edge-aligned' }
    elseif ($results.Count -gt 0) { 'evidence-mixed' }
    else { 'no-evidence' }

Write-Od048 ("correlate addresses_scored=" + $correlated.addressesScored + " total_samples=" + $correlated.totalSamples)
Write-Od048 ("verdict=" + $verdict + " strong_survivors=" + $strongSurvivors.Count + " edge_aligned_suspects=" + $edgeAlignedSurvivors.Count)

# -- Report --
$report = [ordered]@{
    campaign               = 'od-048'
    completedAtUtc         = ([DateTime]::UtcNow).ToString('o')
    battleSessionId        = $battleSessionId
    durationTicks          = $trajectory.durationTicks
    replayStartWallTimeUtc = $replayStartWallUtc
    staged                 = [ordered]@{
        entities   = $stagedEntitiesReport
        union      = $staged.Count
        capped     = ($staged.Count -ge $MaxStaged)
        attempts   = $stagingAttempt
        delayS     = $StageDelaySeconds
        tolerance  = $stagingTolerance
        maxSpeed   = $maxSpeedGlobal
    }
    monitor                = [ordered]@{
        rounds               = $round
        intervalSeconds      = $ReadIntervalSeconds
        readCalls            = $readCalls
        readOkSamples        = $readOkSamples
        addressesWithSeries  = $series.Count
        stoppedReason        = $stoppedReason
    }
    correlate              = [ordered]@{
        addressesScored       = $correlated.addressesScored
        totalSamples          = $correlated.totalSamples
        shiftAudit            = [ordered]@{
            edgeThresholdSeconds = $edgeThreshold
            edgeAlignedSuspects  = $edgeAlignedSurvivors.Count
        }
    }
    results                = @($results | Select-Object -First 50 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds  = $_.shiftSeconds
            edgeAligned   = $_.edgeAligned
            matchCount    = $_.matchCount
            totalSamples  = $_.totalSamples
            span          = $_.span
            score         = $_.score
        }
    })
    strongSurvivors        = @($strongSurvivors | Select-Object -First 20 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds  = $_.shiftSeconds
            matchCount    = $_.matchCount
            totalSamples  = $_.totalSamples
            score         = $_.score
        }
    })
    suspectEdgeAligned      = @($edgeAlignedSurvivors | Select-Object -First 20 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds  = $_.shiftSeconds
            matchCount    = $_.matchCount
            totalSamples  = $_.totalSamples
            score         = $_.score
        }
    })
    verdict                = $verdict
}

try {
    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($ResultPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Od048 ("report_written=" + $ResultPath)
}
catch {
    Write-Od048 ('FAILED_report_write: ' + $_.Exception.Message)
    exit 5
}

Write-Od048 ('done verdict=' + $verdict + ' survivors=' + $results.Count)
exit 0
