#Requires -Version 5.1
<#
.SYNOPSIS
  OD-048 monitor-and-correlate session driver (strategy v4, M1 + M2): stage
  candidate addresses from the decoded replay trajectory, monitor them while
  the replay plays, and correlate each address's value series against the
  replay's known trajectory. M2 family mapping: mid-battle, the driver
  re-stages the +/-16-byte neighbors of the top provisional survivors so the
  final correlate maps the sibling x/y/z components in the same session.

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

  Family mapping (M2): after -FamilyRefineAfterRounds rounds, a provisional
  correlate picks the top survivors (score >= -FamilyMinScore, cap
  -FamilySurvivorCap; edge-aligned candidates are NOT rejected here -- short
  series have wide ambiguity bands that ride the sweep edges, and the final
  correlate re-audits alignment); their +/-16-byte neighbors are added to the
  staged set, so the remaining rounds record the sibling components. A pass
  that finds zero survivors DEFERS to a later round (cadence
  -FamilyRefineRetryGapRounds) instead of self-terminating, so M2 is not
  permanently disabled by one short-series pass. The final correlate's
  families section groups the scored addresses into coordinate families
  (same entity, one byte window); a family whose three members reproduce
  x/y/z at distinct offsets (none edge-aligned) upgrades the verdict to
  family-complete -- one session mapped the whole coordinate vector.

  Staging: the driver fetches the decoded session trajectory (viewpoint
  entity first, then the most-moving entities), waits -StageDelaySeconds for
  the battle to load after the Start marker, then scans the game process for
  Float values near the ground-truth sample nearest the expected current
  replay tick (one scan per axis). The tolerance is auto-scaled from the
  entity's maximum speed x the load-latency bound, so the band covers the
  battle entity's live position regardless of load jitter. The union is the
  staged set; the scan retries until it finds candidates (battle loaded).

  Battle-time budget: staging scans are expensive (tens of seconds each), so
  the driver derives a hard staging deadline from the decoded battle duration
  (battle end - 30s minimum monitor window). Staging never runs past the
  deadline -- it stops with whatever it has staged -- and the monitor exits
  early once the decoded battle duration has elapsed, so a slow staging pass
  cannot consume the whole battle and leave the monitor with an empty world.

  No operator input is needed after the replay launches. The battle plays to
  completion; the gate revokes at battle end and the driver stops.

  -AutoWriteTraceOnVerdict (M2 automation, choreography 7): when the final
  correlate produces a usable family (complete x/y/z triple, else >= 2
  members), the driver IMMEDIATELY invokes x64dbg-write-trace.ps1
  -FamilyFile <this report> -AutoWriteTrace in the same process/launch,
  closing the human-reaction gap on the ~30s green window. The write-trace
  result (exit code, hits, rips) is written to -AutoTraceResultPath
  (default .data\od-048-autotrace-<timestamp>.json); the M1 report itself
  is never re-written. The campaign exit code stays 0 even when the
  auto-trace fails (a no-family / stale / paused verdict is recorded in
  the auto-trace report, not treated as an M1 failure).

.EXITCODES
  0  Campaign completed; report written to .data\od-048-<timestamp>.json
  1  Preflight failure (no host/rendezvous, gate never verified)
  2  Staging failure (trajectory unavailable or scan failed)
  3  Monitor failure (read loop aborted before completion)
  4  Correlate failure
  5  Report could not be written
  6  Attach-smoke gate failed (-AttachSmokeOnFirstRound): the x64dbg
     attach/pause/detach round-trip or memory-BP install is red against the
     live game - fix the debugger wiring, then rerun. The correlate + trace
     window was deliberately NOT spent.
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
    # FloatTolerance for each staging scan (world units). MUST stay small:
    # live probes show any band >= 0.5 floods 10000 candidates in this game
    # (the coordinate field drowns in address-ordered garbage). 0.001 (exact
    # float match) is the empirically selective setting (~1500 candidates).
    [double]$ScanTolerance = 0.001,
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
    # Family refinement (M2): after this many monitor rounds, a provisional
    # correlate picks the top survivors and their +/-FamilyWindowBytes
    # neighbors are added to the staged set for the remaining rounds, so one
    # session can map the sibling x/y/z components without a second launch.
    [int]$FamilyRefineAfterRounds = 10,
    # Provisional-survivor cap for family refinement (8 neighbors each => up
    # to 200 extra staged addresses, comfortably inside the read chunk).
    [int]$FamilySurvivorCap = 25,
    # Byte window around each survivor: three float32s are 12 bytes; 16 leaves
    # headroom for padding or an interleaved field. Neighbor offsets are read
    # at every 4-byte step inside [-Window, +Window] excluding 0.
    [int]$FamilyWindowBytes = 16,
    # Minimum score for a provisional survivor to seed a family.
    [double]$FamilyMinScore = 0.7,
    # Retry cadence for the M2 provisional correlate: when a refinement pass
    # finds zero survivors (short series -> wide ambiguity bands), defer the
    # next attempt this many rounds later so the series accumulate more
    # samples instead of burning a correlate call every round.
    [int]$FamilyRefineRetryGapRounds = 5,
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
    # M2 automation: when the final correlate yields a usable family, invoke
    # x64dbg-write-trace.ps1 -FamilyFile <report> -AutoWriteTrace immediately
    # in the same process (the write-trace's liveness re-read REQUIRES the
    # same game process, and the green window is the battle tail - there is
    # no time for an operator). The auto-trace result report goes to
    # -AutoTraceResultPath. No-op (with a log line) when no family survives.
    [switch]$AutoWriteTraceOnVerdict,
    # How long the auto-invoked write-trace keeps its window. Budget from the
    # choreography timing table: -MaxReadRounds 70 on Dead Rail leaves ~31s
    # of battle; 25 is the recommended first attempt. The operator may pass a
    # higher value on a later attempt once per-round timing is observed.
    [int]$AutoTraceSeconds = 25,
    # Where the auto-trace evidence report lands. Default
    # .data\od-048-autotrace-<timestamp>.json (runtime data, never tracked).
    [string]$AutoTraceResultPath = '',
    # Pass -SkipPlayProbe / -SkipLivenessCheck through to the auto-invoked
    # write-trace (headless validation; the live round keeps both defaults).
    [switch]$AutoTraceSkipPlayProbe,
    [switch]$AutoTraceSkipLivenessCheck,
    # M2 pre-flight gate: after the first monitor round proves the game
    # process is readable, run x64dbg-write-trace.ps1 -AttachSmoke against the
    # LIVE game (hex-pid attach -> pause -> verify -> optional bpm arm/clear
    # -> detach -> verify resume). This validates the two live-only write-trace
    # mechanics mid-battle so a defect is diagnosed as attach-vs-address, not
    # an undiagnosable no-hit run. Fail-closed: a red smoke aborts the
    # campaign (exit 6) before the correlate + trace window is spent.
    [switch]$AttachSmokeOnFirstRound,
    # For -AttachSmokeOnFirstRound: absolute hex address whose guard page is
    # armed+cleared during the smoke (default: the first staged address,
    # which the monitor already reads successfully).
    [string]$AttachSmokeProbeAddress = '',
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

# The host rotates its short-lived local capability (5-minute lease) and
# re-publishes the rendezvous record every >=15s. A driver that reads the
# record once at startup holds a token that expires mid-battle (battles run
# ~271s), after which every POST 401s with web.local_capability_required even
# though the gate is still verified. Re-read the record so the capability
# header always carries the host's current lease; keep the last known record
# if the re-read fails (the host is up -- the read itself is best-effort).
function Refresh-Rendezvous {
    param([object]$Current)
    $fresh = Get-Rendezvous
    if ($null -ne $fresh -and -not [string]::IsNullOrWhiteSpace([string]$fresh.capability)) {
        return $fresh
    }
    return $Current
}

function Get-GateState {
    param([object]$Rendezvous)
    try {
        if (-not $Rendezvous) { return $null }
        # Explicit 10s timeout: a hung host must fail a poll fast, not hang
        # the driver silently (PS 5.1's default is indefinite).
        return Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/state') -TimeoutSec 10 -Headers @{
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
        [object]$Body = $null,
        # Explicit timeout: a staging scan is a full-memory pass that can take
        # tens of seconds, and pwsh 7's Invoke-RestMethod default (100s) would
        # abort it mid-scan; PS 5.1's default (indefinite) hangs forever on a
        # dead host. 300s is generous for the slowest scan while still failing
        # a hung call in finite time.
        [int]$TimeoutSec = 300
    )
    $params = @{
        Uri        = $Rendezvous.baseUri + $RelativePath
        Method     = $Method
        TimeoutSec = $TimeoutSec
        Headers    = @{ 'X-WotBTreader-Capability' = [string]$Rendezvous.capability }
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }
    try {
        return Invoke-RestMethod @params
    }
    catch {
        # Log WHY the call failed (status + short error body): the generic
        # FAILED_* messages alone leave the operator blind between a broken
        # request, a gate-revoked 4xx, and a dead host. Loopback URIs and the
        # small error bodies of 400s are error codes, not sensitive data.
        $status = $null
        $detail = ''
        # PS 5.1 throws WebException (has Response, no StatusCode); pwsh 7
        # throws HttpResponseException (has StatusCode, no Response). Bare
        # property access on a non-matching exception type would throw
        # PropertyNotFoundException under StrictMode, so gate every access
        # on a PSObject.Properties presence check (index access is always
        # StrictMode-safe and returns $null for a missing property).
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        elseif ($_.Exception.PSObject.Properties['StatusCode'] -and $_.Exception.StatusCode) {
            $status = [int]$_.Exception.StatusCode
        }
        # ErrorDetails may be null (e.g. connection refused has no response
        # body) -- null.Message throws under StrictMode, so guard the member.
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) {
            $detail = ([string]$_.ErrorDetails.Message -replace '[\r\n]+', ' ').Trim()
            if ($detail.Length -gt 200) { $detail = $detail.Substring(0, 200) }
        }
        # PS 5.1 fallback: WebException keeps the response body in the stream,
        # not in ErrorDetails -- read it so a 400 reason is never invisible.
        if (-not $detail -and $null -ne $_.Exception -and $_.Exception.PSObject.Properties['Response']) {
            try {
                $response = $_.Exception.Response
                if ($null -ne $response -and $response.PSObject.Properties['GetResponseStream']) {
                    $stream = $response.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = New-Object System.IO.StreamReader($stream)
                        $body = $reader.ReadToEnd()
                        $reader.Dispose()
                        $detail = ($body -replace '[\r\n]+', ' ').Trim()
                        if ($detail.Length -gt 200) { $detail = $detail.Substring(0, 200) }
                    }
                }
            }
            catch {
                # Body read failure is not fatal; keep whatever detail we have.
            }
        }
        $diag = ('api_failed method={0} path={1}' -f $Method, $RelativePath)
        if ($null -ne $status) { $diag += (' status=' + $status) }
        if ($detail) { $diag += (' body=' + $detail) }
        Write-Od048 $diag
        return $null
    }
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

# Server cap on correlate observations (matches the endpoint validation); the
# driver keeps the family-neighbor series inside the cap with priority.
$correlateMaxObservations = 2000

# Build the correlate observations array from the monitored series.
function Get-CorrelateObservations {
    param([object]$Series)
    $obs = @()
    foreach ($addressKey in $Series.Keys) {
        $list = [System.Collections.Generic.List[object]]$Series[$addressKey]
        if ($list.Count -lt 2) { continue }
        $obs += @{
            Address = $addressKey
            Samples = @($list | ForEach-Object {
                @{ wallTimeUtc = $_.wallTimeUtc; value = $_.value }
            })
        }
    }
    return $obs
}

function New-CorrelateBody {
    param(
        [object]$Observations,
        [string]$SessionId,
        [string]$ReplayStartWallTimeUtc,
        [double]$TolerancePerAxis,
        [int]$MaxTimeShiftSeconds,
        [double]$MinMovingSpan
    )
    return @{
        groundTruthSessionId   = $SessionId
        replayStartWallTimeUtc = $ReplayStartWallTimeUtc
        tolerancePerAxis       = $TolerancePerAxis
        maxTimeShiftSeconds    = $MaxTimeShiftSeconds
        minMovingSpan          = $MinMovingSpan
        observations           = $Observations
    }
}

# Neighbor addresses at every 4-byte step inside +/-WindowBytes (excluding the
# survivor itself): a 16-byte window yields -16,-12,-8,-4,+4,+8,+12,+16.
function Get-FamilyNeighborAddresses {
    param([string]$Address, [int]$WindowBytes, [int]$ValueSize = 4)
    $hex = $Address
    if ($hex.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) { $hex = $hex.Substring(2) }
    $value = [long]::Parse($hex, [Globalization.NumberStyles]::HexNumber, [Globalization.CultureInfo]::InvariantCulture)
    $neighbors = @()
    for ($offset = -$WindowBytes; $offset -le $WindowBytes; $offset += $ValueSize) {
        if ($offset -eq 0) { continue }
        $neighborValue = $value + $offset
        if ($neighborValue -le 0) { continue }
        $neighbors += ('0x{0:X}' -f $neighborValue)
    }
    return $neighbors
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
    # The read API route is /api/v1/sessions (the old /api/v1/read/sessions
    # prefix 404'd -- 2026-08-05 live-round fix), and the wire field is
    # session.battleSessionId (NOT session.id). The launch flow imports the
    # replay into the HOST's data root (the OD launch host defaults to
    # %LocalAppData%\WotBTreader, not .data), so the newest session in THIS
    # host is the just-imported battle -- auto-pick must use the host's own
    # list, never a hardcoded id from another data root.
    $page = Invoke-Api -Rendezvous $rendezvous -Method 'Get' -RelativePath '/api/v1/sessions?limit=50'
    if ($null -eq $page -or $page.items.Count -eq 0) {
        Write-Od048 'FAILED_no_decoded_session'
        exit 2
    }
    # items[].session is nullable on the wire: a decode run with no battle
    # session serializes a null entry. Guard it so StrictMode fails with a
    # clean diagnostic instead of crashing on a member access of null.
    $newestSession = $page.items[0]
    if ($null -eq $newestSession -or $null -eq $newestSession.session) {
        Write-Od048 'FAILED_newest_session_null'
        exit 2
    }
    $battleSessionId = [string]$newestSession.session.battleSessionId
    Write-Od048 ("auto_picked_session=" + $battleSessionId)
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
# The scan band must match the entity's LIVE position value. Empirically the
# game holds many floats in ANY coordinate band: a live probe on the 11.19.0
# client showed tolerance 0.5 already floods 10000 candidates (returned in
# address order, so the true field drowns and never gets staged) while an
# exact match (0.001) yields ~1500. The correlate shift sweep absorbs anchor
# error and load latency at SCORING time; the staging scan must stay selective
# or the staged set is random memory (all constant decoys -> zero results).
$stagingTolerance = [double]$ScanTolerance
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
# Parse the anchor robustly: Z-suffixed, bare-UTC, and explicit-offset ISO
# strings all normalize to UTC (a bare string is ASSUMED UTC, per the
# documented contract, so it must NOT be reinterpreted as local time).
$anchorUtc = [datetime]::Parse(
    $replayStartWallUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
# -- Battle-time budget --
# Staging is the expensive step: up to MaxStagingAttempts x StageTopN entities
# x 3 axes = 27 full-memory scans, each taking tens of seconds. Unguarded, a
# slow first attempt plus retries can consume the ENTIRE battle (Dead Rail is
# ~271s; the real decoded sessions average ~250s) and leave the monitor with
# an empty world. Derive a hard staging deadline from the decoded duration:
# staging must never run past (battle end - minimum monitor window). All
# deadline comparisons use UTC explicitly (DateTime comparison in PS ignores
# Kind, so local-vs-UTC mixing would compare wall clocks, not instants).
$durationSeconds = 0.0
if ($null -ne $trajectory.durationTicks -and [double]$trajectory.durationTicks -gt 0) {
    $durationSeconds = [double]$trajectory.durationTicks / 10000000.0
}
$battleEndUtc = $null
if ($durationSeconds -gt 0) { $battleEndUtc = $anchorUtc.AddSeconds($durationSeconds) }
$monitorMinSeconds = 30.0
$stagingDeadlineUtc = $null
$monitorExitUtc = $null
if ($null -ne $battleEndUtc) {
    $stagingDeadlineUtc = $battleEndUtc.AddSeconds(-$monitorMinSeconds)
    # The battle starts at tick 0 some seconds AFTER the Start marker (load
    # latency, absorbed by the shift sweep up to MaxTimeShiftSeconds), so the
    # UPPER bound on wall-time battle end = anchor + duration + max load
    # latency. The monitor must not exit before that bound or it drops the
    # tail observations; the staging deadline may stay at the nominal end
    # (stopping staging early is safe, only losing scan attempts).
    $monitorExitUtc = $battleEndUtc.AddSeconds([double]$MaxTimeShiftSeconds + 10.0)
    Write-Od048 ("battle_duration_s=" + [Math]::Round($durationSeconds, 1) + " staging_deadline=" + $stagingDeadlineUtc.ToString('o'))
}
$stagingStartUtc = [datetime]::UtcNow
$budgetExhausted = $false
$scanFailed = $false
# Edge threshold for the shift-band audit: computed ONCE before staging so the
# mid-battle family refinement and the final survivor audit share the formula.
$edgeThreshold = [Math]::Max(2, $MaxTimeShiftSeconds - 2)
# Family refinement state (M2): provisional-survivor addresses, the staged
# neighbor set, whether the mid-battle pass already ran, and the last round a
# pass was ATTEMPTED (defer cadence; -99 means never attempted).
$familyRefined = $false
$familyRefineRound = 0
$familyRefineLastAttemptRound = -99
$familyRefineAttempts = 0
$familyRefineDeferred = 0
$familySurvivors = @()
# Attach-smoke gate (M2 pre-flight): whether it already ran, its report
# (null until run; a red smoke aborts the campaign with exit 6), and the
# path its JSON report lands at (defaulted when the gate fires).
$attachSmokeDone = $false
$attachSmoke = $null
$smokeResultPath = ''
$familyStaged = [System.Collections.Generic.HashSet[string]]::new()
$familyNeighborAdded = 0
$staged = [System.Collections.Generic.List[string]]::new()
$stagedEntitiesReport = @()
# Per-scan staging budget (breadth fix): the union cap (MaxStaged) is GLOBAL,
# so without per-scan budgets the first scan's candidates (the viewpoint x
# scan alone returns ~1500 exact matches) consume the whole union and every
# later scan -- the y/z axes and all entities after the first -- contributes
# ZERO candidates (FRESH5: staged=3000 yet all 50 results were axis=x; the
# y/z sibling fields were never staged, so no family could form). Reserve a
# slice of the union for the backup entities so every axis of every staged
# entity contributes candidates; the viewpoint scans keep the rest (their
# depth found the true field in FRESH5). The viewpoint's budget is a TOTAL
# across its three axis scans (not per scan), so the first flood (x ~1500)
# cannot eat the reserve -- the union cap is enforced globally regardless.
$reservedForBackup = if ($stagingEntities.Count -le 1) { 0 }
    else { [int]($MaxStaged / [Math]::Max(2, $stagingEntities.Count)) }
$backupScanCount = [Math]::Max(1, ($stagingEntities.Count - 1) * 3)
$perScanBudgetBackup = [Math]::Max(100, [int]($reservedForBackup / $backupScanCount))
# Decremented as the viewpoint's scans add candidates; when exhausted, the
# remaining viewpoint axes are skipped so the reserve is honored.
$viewpointBudgetLeft = $MaxStaged - $reservedForBackup
Write-Od048 ("staging_budget viewpoint_total={0} backup_scan={1} reserved={2}" -f $viewpointBudgetLeft, $perScanBudgetBackup, $reservedForBackup)
$stagingAttempt = 0
while ($stagingAttempt -lt $MaxStagingAttempts -and -not $budgetExhausted) {
    $stagingAttempt += 1
    $staged.Clear()
    $stagedEntitiesReport = @()
    $scanFailed = $false
    $attemptElapsed = ((Get-Date).ToUniversalTime() - $anchorUtc).TotalSeconds
    Write-Od048 ("staging attempt={0} elapsed_s={1}" -f $stagingAttempt, [Math]::Round([Math]::Max(0.0, $attemptElapsed), 1))

    for ($entityIndex = 0; $entityIndex -lt $stagingEntities.Count; $entityIndex++) {
        $entity = $stagingEntities[$entityIndex]
        $entityLogged = $false
        # Viewpoint scans draw from the shared viewpoint total (decremented as
        # candidates are added); backup entities get the fair-share slice so
        # their y/z fields are staged too.
        foreach ($axis in @('x', 'y', 'z')) {
            # Viewpoint total exhausted: skip its remaining axes so the
            # backup reserve is honored (the first flood must not eat it).
            if ($entityIndex -eq 0 -and $viewpointBudgetLeft -le 0) { break }
            # Budget guard: a full-memory scan takes tens of seconds. If the
            # battle is about to end, stop staging and use what we have so the
            # monitor keeps a real observation window.
            if ($null -ne $stagingDeadlineUtc -and ([datetime]::UtcNow -gt $stagingDeadlineUtc)) {
                $budgetExhausted = $true
                Write-Od048 'staging_budget_exhausted'
                break
            }
            # Fresh tick estimate PER AXIS: the estimate computed at attempt
            # start goes stale while the full-memory scans run (tens of
            # seconds each), so the band would trail the tank by scan duration
            # x speed. Recentering before every scan keeps each band on target.
            $elapsedSeconds = ((Get-Date).ToUniversalTime() - $anchorUtc).TotalSeconds
            if ($elapsedSeconds -lt 0) { $elapsedSeconds = 0 }
            $stageTickEstimate = [long]($elapsedSeconds * 10000000.0)
            if (-not $entityLogged) {
                Write-Od048 ("staging entity={0} tick_est={1}" -f $entity.EntityId, $stageTickEstimate)
                $entityLogged = $true
            }
            $sample = Select-NearestSample -Samples $entity.Samples -TargetTick $stageTickEstimate
            $rawAxisValue = $sample.$axis
            if ($null -eq $rawAxisValue) { continue }
            $axisValue = [double]$rawAxisValue
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
                $scanFailed = $true
                Write-Od048 ('staging_scan_failed axis=' + $axis + ' (retrying attempt)')
                break
            }
            # Per-scan budget: viewpoint draws from its remaining total (so a
            # 1500-candidate x flood leaves room for y/z and the reserve), and
            # backup scans are capped at their fair share.
            $scanBudget = if ($entityIndex -eq 0) { $viewpointBudgetLeft } else { $perScanBudgetBackup }
            $scanAdded = 0
            foreach ($candidate in $scan.candidates) {
                # Per-scan budget (not just the global cap): a single flooding
                # scan must not starve the remaining axes/entities (see the
                # staging_budget computation above).
                if ($scanAdded -ge $scanBudget -or $staged.Count -ge $MaxStaged) { break }
                # Keep the canonical "0x..." form end-to-end (staging, read
                # batches, series keys, report) so no decimal/hex mismatch can
                # split an address across two identities.
                $hexAddress = [string]$candidate.absoluteAddress
                if ($hexAddress -notmatch '^0x[0-9a-fA-F]+$') { continue }
                if (-not $staged.Contains($hexAddress)) { $staged.Add($hexAddress); $scanAdded += 1 }
            }
            if ($entityIndex -eq 0) { $viewpointBudgetLeft -= $scanAdded }
        }
        # Entity-level budget/scan guard: once exhausted or a scan has failed,
        # stop the entity loop too, so entities whose scans never ran are NOT
        # reported as staged.
        if ($budgetExhausted -or $scanFailed) { break }
        # One report entry per ENTITY (not per axis scan).
        $stagedEntitiesReport += [pscustomobject]@{
            EntityId    = $entity.EntityId
            TankName    = $entity.TankName
            IsViewpoint = $entity.IsViewpoint
        }
    }

    # Break the attempt loop only when we have enough staged addresses or the
    # budget is gone. A scan FAILURE must NOT break here: it falls through to
    # the retry block below so the next attempt (which resets $scanFailed) can
    # succeed -- a failed attempt 1 is retried, not wasted.
    if ($staged.Count -ge 3 -or $budgetExhausted) { break }
    if ($stagingAttempt -lt $MaxStagingAttempts -and -not $budgetExhausted) {
        # Budget-aware retry: never sleep past the staging deadline.
        $retrySleepSeconds = 15
        if ($null -ne $stagingDeadlineUtc) {
            $remainingToDeadline = ($stagingDeadlineUtc - [datetime]::UtcNow).TotalSeconds
            if ($remainingToDeadline -lt 15) { $retrySleepSeconds = [Math]::Max(0, [int]$remainingToDeadline) }
        }
        Write-Od048 ("staging retry in {0}s (battle may still be loading)" -f $retrySleepSeconds)
        Start-Sleep -Seconds $retrySleepSeconds
    }
}
$stagingEndUtc = [datetime]::UtcNow
if ($staged.Count -lt 3) {
    if ($scanFailed) {
        Write-Od048 'FAILED_staging_scan (all attempts failed)'
    }
    else {
        Write-Od048 ("FAILED_staging_too_small staged=" + $staged.Count)
    }
    exit 2
}
Write-Od048 ("staged=" + $staged.Count)
# Snapshot the SCAN-only staged count before the mid-battle family expansion
# so the report's staged.union/capped reflect the scan cap, not the expansion.
$scanStagedCount = $staged.Count

# -- Monitor loop --
$series = [System.Collections.Generic.Dictionary[string, object]]::new()
$round = 0
$readCalls = 0
$readOkSamples = 0
$stoppedReason = 'rounds-exhausted'
while ($round -lt $MaxReadRounds) {
    $round++
    # Refresh the capability BEFORE polling the gate: the host rotates its
    # short-lived lease (~5 min) mid-battle; a stale token 401s every read
    # even while the gate stays verified.
    $rendezvous = Refresh-Rendezvous -Current $rendezvous
    $gate = Get-GateState -Rendezvous $rendezvous
    # PS 5.1 gate-lost diag: the (if ...) expression used to label the gate
    # state is PS7-only syntax and crashes Windows PowerShell at runtime --
    # compute the label before the branch that consumes it.
    if ($null -eq $gate) { $gateDiag = 'no-host' } else { $gateDiag = [string]$gate.verificationState }
    if ($null -eq $gate -or $gate.verificationState -ne 'OfflineReplayVerified') {
        $stoppedReason = 'gate-lost'
        Write-Od048 ("monitor_stop gate=" + $gateDiag)
        break
    }

    # End-of-battle early exit: once the UPPER bound on wall-time battle end
    # (nominal duration + max load latency + trailing window) has elapsed,
    # further rounds only observe an empty world -- stop and correlate what we
    # have instead of burning rounds.
    if ($null -ne $monitorExitUtc -and ([datetime]::UtcNow -gt $monitorExitUtc)) {
        $stoppedReason = 'battle-ended'
        Write-Od048 'monitor_stop battle-ended'
        break
    }

    # M2 attach-smoke gate (fail-closed pre-flight): once the FIRST round has
    # produced readable samples (the game process is up and the read path
    # works), prove the two live-only write-trace mechanics against the live
    # process - x64dbg hex-pid attach + pause + detach round-trip and (with a
    # probe address) memory-BP install. A red smoke aborts BEFORE the correlate
    # rounds and trace window are spent, so a live defect is diagnosed as
    # attach-vs-address, not an undiagnosable no-hit run. The smoke pauses the
    # game briefly; the next round's reads happen after detach resumes it.
    if ($AttachSmokeOnFirstRound -and -not $attachSmokeDone -and $readOkSamples -gt 0) {
        $smokeScript = Join-Path $PSScriptRoot 'x64dbg-write-trace.ps1'
        if (-not (Test-Path -LiteralPath $smokeScript)) {
            Write-Od048 'attach_smoke FAILED script_missing'
            $stoppedReason = 'attach-smoke-failed'
            break
        }
        $smokeProbe = $AttachSmokeProbeAddress
        if ([string]::IsNullOrWhiteSpace($smokeProbe) -and $staged.Count -gt 0) {
            # Default probe: the first staged address (already proven readable
            # by this round's reads - a guaranteed-live game page).
            $smokeProbe = [string]$staged[0]
        }
        if ([string]::IsNullOrWhiteSpace($smokeResultPath)) {
            $dataDir = Join-Path $RepoRoot '.data'
            if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
            $smokeResultPath = Join-Path $dataDir ("od-048-attach-smoke-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
        }
        $smokeArgs = @{
            AttachSmoke       = $true
            SmokeProbeAddress = $smokeProbe
            SmokeResultPath   = $smokeResultPath
            SkipGateCheck     = $true
        }
        Write-Od048 ('attach_smoke INVOKING round=' + $round + ' probe=' + $smokeProbe)
        try {
            & $smokeScript @smokeArgs
            $smokeExit = $LASTEXITCODE
        }
        catch {
            $smokeExit = -1
            Write-Od048 ('attach_smoke THREW ' + $_.Exception.Message)
        }
        Write-Od048 ('attach_smoke exit=' + $smokeExit)
        $attachSmokeDone = $true
        $attachSmoke = [ordered]@{
            ranUtc       = ([DateTime]::UtcNow).ToString('o')
            atRound      = $round
            probeAddress = $smokeProbe
            resultPath   = $smokeResultPath
            exitCode     = $smokeExit
            ok           = ($smokeExit -eq 0)
        }
        if ($smokeExit -ne 0) {
            $stoppedReason = 'attach-smoke-failed'
            Write-Od048 'attach_smoke FAILED aborting before correlate (fix x64dbg attach/memory-BP on the live game, then rerun)'
            Write-Od048 'attach_smoke WARN_game_may_be_left_paused - close the game window if it is frozen'
            break
        }
        # Green smoke: the ~15-20s pause may have aged out the host's
        # lifecycle evidence (gate transiently not verified). Wait for the
        # gate to recover so a green smoke is NOT followed by a gate-lost
        # abort on the next round. The loop's own gate check re-verifies
        # next round anyway - this grace only covers the stall window.
        $gateGraceDeadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $gateGraceDeadline) {
            $rendezvous = Refresh-Rendezvous -Current $rendezvous
            $gate = Get-GateState -Rendezvous $rendezvous
            $gateNow = if ($null -ne $gate) { [string]$gate.verificationState } else { 'no-host' }
            if ($gateNow -eq 'OfflineReplayVerified') { break }
            Write-Od048 ('attach_smoke gate_recovering ' + $gateNow)
            Start-Sleep -Seconds 3
        }
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

    # Family refinement (M2): once the series carry enough evidence, run a
    # provisional correlate, take the top survivors, and add their
    # +/-FamilyWindowBytes neighbors to the staged set so the remaining rounds
    # capture the sibling x/y/z components. Refinement RETRIES until it finds
    # survivors: a short-series pass at the first eligible round scores few
    # samples over a wide ambiguity band (every candidate rides the sweep edge)
    # and would find zero survivors -- marking the pass done there (FRESH5:
    # 'family_refined round=10 survivors=0' -> families=0) permanently disables
    # M2. So a pass with 0 survivors DEFERS to a later round (cadence
    # -FamilyRefineRetryGapRounds) instead of self-terminating; a transient API
    # failure also defers. The pass marks done only once survivors are found
    # (neighbors staged) or the series are genuinely empty (no scored series).
    # NOTE: the provisional pass deliberately does NOT apply the edge-aligned
    # filter: short series have wide bands that ride the sweep edges, so the
    # filter rejects every candidate (this is what produced survivors=0 at
    # round 10). The provisional pass only SEEDS neighbor staging; the final
    # correlate and the family builder re-audit edge alignment authoritatively.
    if (-not $familyRefined -and $round -ge $FamilyRefineAfterRounds -and ($round - $familyRefineLastAttemptRound) -ge $FamilyRefineRetryGapRounds) {
        $familyRefineLastAttemptRound = $round
        $familyRefineAttempts += 1
        # Fresh array per attempt: retries must not accumulate stale survivor
        # addresses across passes (only the survivors of THIS pass seed staging).
        $familySurvivors = @()
        $provisionalObs = @(Get-CorrelateObservations -Series $series |
            Sort-Object { $_.Samples.Count } -Descending |
            Select-Object -First $correlateMaxObservations)
        if ($provisionalObs.Count -gt 0) {
            $provisional = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/correlate' -Body (New-CorrelateBody -Observations $provisionalObs -SessionId $battleSessionId -ReplayStartWallTimeUtc $replayStartWallUtc -TolerancePerAxis $TolerancePerAxis -MaxTimeShiftSeconds $MaxTimeShiftSeconds -MinMovingSpan $MinMovingSpan)
            if ($null -ne $provisional -and $null -ne $provisional.results) {
                foreach ($r in @($provisional.results)) {
                    if ($familySurvivors.Count -ge $FamilySurvivorCap) { break }
                    if ($null -eq $r.score -or [double]$r.score -lt $FamilyMinScore) { continue }
                    # No edge-aligned rejection here -- see NOTE above.
                    $familySurvivors += [string]$r.address
                }
                foreach ($address in $familySurvivors) {
                    foreach ($neighbor in (Get-FamilyNeighborAddresses -Address $address -WindowBytes $FamilyWindowBytes)) {
                        if ($familyStaged.Contains($neighbor)) { continue }
                        $familyStaged.Add($neighbor) | Out-Null
                        if (-not $staged.Contains($neighbor)) {
                            $staged.Add($neighbor)
                            $familyNeighborAdded += 1
                        }
                    }
                }
                if ($familySurvivors.Count -gt 0) {
                    $familyRefined = $true
                    $familyRefineRound = $round
                    Write-Od048 ("family_refined round={0} survivors={1} neighbors_added={2}" -f $round, $familySurvivors.Count, $familyNeighborAdded)
                }
                else {
                    $familyRefineDeferred += 1
                    Write-Od048 ("family_refine_deferred round={0} survivors=0 (retry after {1} more rounds)" -f $round, $FamilyRefineRetryGapRounds)
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

# -- Attach-smoke gate: fail-closed abort (exit 6) ----------------------------
# A red pre-flight means the write-trace cannot work against this live game
# (attach, pause, or memory-BP install failed). Correlating would still
# produce a family, and the auto-trace would then burn the green window on an
# undiagnosable no-hit run - so stop now with a diagnostic report instead.
if ($stoppedReason -eq 'attach-smoke-failed') {
    Write-Od048 'attach_smoke FAILED aborting campaign (exit 6) - correlate skipped'
    $failReport = [ordered]@{
        verdict       = 'attach-smoke-failed'
        stoppedReason = $stoppedReason
        monitorRounds = $round
        seriesCount   = $series.Count
        attachSmoke   = $attachSmoke
        ranUtc        = ([DateTime]::UtcNow).ToString('o')
        note          = 'M2 pre-flight gate red - fix the x64dbg attach/memory-BP wiring on the live game, then rerun'
    }
    try {
        $failJson = $failReport | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($ResultPath, $failJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Od048 ('report_written=' + $ResultPath)
    }
    catch {
        Write-Od048 ('FAILED_report_write: ' + $_.Exception.Message)
    }
    exit 6
}

# -- Correlate --
$observations = @(Get-CorrelateObservations -Series $series)
if ($observations.Count -eq 0) {
    Write-Od048 'FAILED_no_observations_for_correlate'
    exit 3
}

# The correlate endpoint caps observations at 2000 series, but staging can
# yield up to MaxStaged (3000) addresses PLUS the M2 family neighbors. A plain
# most-sampled-first truncation would drop exactly the family-neighbor series
# (they were staged mid-battle and carry fewer samples than the originals), so
# keep every family address first (capped), then fill the remaining budget
# with the most-sampled rest. Record the truncation in the report.
$observationsTotal = $observations.Count
$familyObs = @($observations | Where-Object { $familyStaged.Contains($_.Address) } | Select-Object -First $correlateMaxObservations)
$restObs = @($observations | Where-Object { -not $familyStaged.Contains($_.Address) } | Sort-Object { $_.Samples.Count } -Descending)
$keepRest = $correlateMaxObservations - $familyObs.Count
if ($keepRest -lt 0) { $keepRest = 0 }
$observations = @($familyObs) + @($restObs | Select-Object -First $keepRest)
if ($observationsTotal -gt $observations.Count) {
    Write-Od048 ("correlate observations truncated from {0} to {1} (server cap {2}; family neighbors kept)" -f $observationsTotal, $observations.Count, $correlateMaxObservations)
}

# Refresh the capability one last time: the final correlate can run minutes
# after the last monitor round (post-processing/truncation), by which point a
# 5-minute lease may have rotated.
$rendezvous = Refresh-Rendezvous -Current $rendezvous
$correlated = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/correlate' -Body (New-CorrelateBody -Observations $observations -SessionId $battleSessionId -ReplayStartWallTimeUtc $replayStartWallUtc -TolerancePerAxis $TolerancePerAxis -MaxTimeShiftSeconds $MaxTimeShiftSeconds -MinMovingSpan $MinMovingSpan)
if ($null -eq $correlated -or $null -eq $correlated.results) {
    Write-Od048 'FAILED_correlate'
    exit 4
}

$results = @($correlated.results)

# Shift audit: a survivor whose winning shift rides the sweep EDGE means the
# true alignment is probably beyond the sweep (anchor wrong or load latency
# exceeded the bound) -- the classic bad-anchor false positive. Demote those
# from "strong" to "suspect" so a broken anchor cannot masquerade as evidence.
# ($edgeThreshold was computed once before staging so the mid-battle family
# refinement and this audit share the same formula.)
$edgeAlignedSurvivors = @()
foreach ($result in $results) {
    $shift = if ($null -eq $result.shiftSeconds) { 0.0 } else { [double]$result.shiftSeconds }
    # Band-based edge detection: the closest-to-zero reported shift can mask an
    # edge-riding alignment by up to (tolerance / local slope) seconds, so flag
    # when EITHER band edge touches the sweep boundary. (Older hosts without
    # the band fields fall back to the reported shift.)
    $minShift = if ($null -eq $result.shiftMinSeconds) { $shift } else { [double]$result.shiftMinSeconds }
    $maxShift = if ($null -eq $result.shiftMaxSeconds) { $shift } else { [double]$result.shiftMaxSeconds }
    $isEdgeAligned = ([Math]::Abs($minShift) -ge $edgeThreshold) -or ([Math]::Abs($maxShift) -ge $edgeThreshold)
    $result | Add-Member -NotePropertyName edgeAligned -NotePropertyValue $isEdgeAligned -Force
    $result | Add-Member -NotePropertyName shiftBandMinSeconds -NotePropertyValue $minShift -Force
    $result | Add-Member -NotePropertyName shiftBandMaxSeconds -NotePropertyValue $maxShift -Force
    if ($isEdgeAligned -and $result.score -ge 0.7) {
        $edgeAlignedSurvivors += $result
    }
}
$strongSurvivors = @($results | Where-Object { $_.score -ge 0.7 -and -not $_.edgeAligned })
$families = @($correlated.families)
$completeFamilies = @($families | Where-Object { $_.complete })
# M2 verdict upgrade: a complete family (three components of one entity
# reproduced at distinct offsets, none edge-aligned) is the strongest artifact
# this pipeline produces -- one session mapped the whole coordinate vector.
$verdict = if ($completeFamilies.Count -gt 0) { 'family-complete' }
    elseif ($strongSurvivors.Count -gt 0) { 'evidence-strong' }
    elseif ($edgeAlignedSurvivors.Count -gt 0) { 'evidence-edge-aligned' }
    elseif ($results.Count -gt 0) { 'evidence-mixed' }
    else { 'no-evidence' }

if ($strongSurvivors.Count -gt 0 -and $families.Count -eq 0) {
    Write-Od048 'family_mapping_failed no_families_from_survivors (M2 stop rule: recheck staging before burning another session)'
}

Write-Od048 ("correlate addresses_scored=" + $correlated.addressesScored + " total_samples=" + $correlated.totalSamples)
Write-Od048 ("verdict=" + $verdict + " strong_survivors=" + $strongSurvivors.Count + " families=" + $families.Count + " complete_families=" + $completeFamilies.Count)

# -- Report --
$report = [ordered]@{
    campaign               = 'od-048'
    completedAtUtc         = ([DateTime]::UtcNow).ToString('o')
    battleSessionId        = $battleSessionId
    durationTicks          = $trajectory.durationTicks
    replayStartWallTimeUtc = $replayStartWallUtc
    # M2 pre-flight gate result (null when -AttachSmokeOnFirstRound was off).
    attachSmoke            = $attachSmoke
    staged                 = [ordered]@{
        entities        = $stagedEntitiesReport
        union           = $scanStagedCount
        capped          = ($scanStagedCount -ge $MaxStaged)
        attempts        = $stagingAttempt
        delayS          = $StageDelaySeconds
        tolerance       = $stagingTolerance
        maxSpeed        = $maxSpeedGlobal
        durationS       = [Math]::Round($durationSeconds, 1)
        stagingS        = [Math]::Round(($stagingEndUtc - $stagingStartUtc).TotalSeconds, 1)
        budgetExhausted = $budgetExhausted
        monitorMinS     = $monitorMinSeconds
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
        observationsSent      = $observations.Count
        observationsTotal     = $observationsTotal
        observationsCap       = $correlateMaxObservations
        familyNeighborsSent   = $familyObs.Count
        shiftAudit            = [ordered]@{
            edgeThresholdSeconds = $edgeThreshold
            edgeAlignedSuspects  = $edgeAlignedSurvivors.Count
            method               = 'band-edges'
        }
        # Axis histogram of the scored results: an all-x (or all-one-axis)
        # report is the M2 family-starvation signature (sibling y/z fields
        # never staged) and must be visible at a glance.
        resultsByAxis         = [ordered]@{
            x = @($results | Where-Object { $_.axis -eq 'x' }).Count
            y = @($results | Where-Object { $_.axis -eq 'y' }).Count
            z = @($results | Where-Object { $_.axis -eq 'z' }).Count
        }
        strongByAxis          = [ordered]@{
            x = @($strongSurvivors | Where-Object { $_.axis -eq 'x' }).Count
            y = @($strongSurvivors | Where-Object { $_.axis -eq 'y' }).Count
            z = @($strongSurvivors | Where-Object { $_.axis -eq 'z' }).Count
        }
    }
    results                = @($results | Select-Object -First 50 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds       = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            edgeAligned        = $_.edgeAligned
            matchCount         = $_.matchCount
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
            shiftSeconds       = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            matchCount         = $_.matchCount
            totalSamples       = $_.totalSamples
            score              = $_.score
        }
    })
    suspectEdgeAligned      = @($edgeAlignedSurvivors | Select-Object -First 20 | ForEach-Object {
        [ordered]@{
            address             = $_.address
            participantId       = $_.participantId
            entityId            = $_.entityId
            axis                = $_.axis
            sign                = $_.sign
            shiftSeconds        = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            matchCount          = $_.matchCount
            totalSamples        = $_.totalSamples
            score               = $_.score
        }
    })
    familyRefinement        = [ordered]@{
        refinedAtRound       = $familyRefineRound
        survivorCap          = $FamilySurvivorCap
        windowBytes          = $FamilyWindowBytes
        provisionalSurvivors = $familySurvivors.Count
        neighborsStaged      = $familyNeighborAdded
        attempts             = $familyRefineAttempts
        deferred             = $familyRefineDeferred
        retryGapRounds       = $FamilyRefineRetryGapRounds
        totalStaged          = $staged.Count
    }
    families                = @($families | ForEach-Object {
        [ordered]@{
            baseAddress = $_.baseAddress
            spanBytes   = $_.spanBytes
            axesCovered = @($_.axesCovered)
            complete    = $_.complete
            members     = @($_.members | ForEach-Object {
                [ordered]@{
                    address             = $_.address
                    offsetBytes         = $_.offsetBytes
                    axis                = $_.axis
                    sign                = $_.sign
                    shiftSeconds        = $_.shiftSeconds
                    shiftBandMinSeconds = $_.shiftMinSeconds
                    shiftBandMaxSeconds = $_.shiftMaxSeconds
                    score               = $_.score
                    edgeAligned         = $_.edgeAligned
                }
            })
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

# -- M2 automation: auto-invoke the write-trace on a surviving family --
# Closes the choreography 7 human-reaction gap: the write-trace must start
# within seconds of the verdict (the green window is the battle tail, ~30s),
# and its liveness re-read (exit 8) REQUIRES the same game process, so a
# manual start loses most of the window. The auto-trace result is a SEPARATE
# report: the M1 report is immutable once written.
$autoTrace = $null
if ($AutoWriteTraceOnVerdict) {
    # Usable-family gate (M2 stop rule): a complete family is the prize; a
    # 2-member family is still worth a trace window -- but only if at least one
    # member is NOT edge-aligned (a bad-anchor family whose every member rides
    # the sweep edge would burn the trace window on fabricated alignment);
    # fewer members means no trace (x64dbg DR0-DR3 arming of a lone member is
    # not evidence).
    $usableFamily = $null
    if ($completeFamilies.Count -gt 0) {
        $usableFamily = $completeFamilies[0]
    }
    else {
        foreach ($f in @($families)) {
            $memberCount = @($f.members).Count
            if ($memberCount -lt 2) { continue }
            $hasNonEdgeAligned = $false
            foreach ($m in @($f.members)) {
                if (-not $m.edgeAligned) { $hasNonEdgeAligned = $true; break }
            }
            if ($hasNonEdgeAligned) { $usableFamily = $f; break }
        }
    }

    if ($null -eq $usableFamily) {
        Write-Od048 ('auto_write_trace SKIPPED no_usable_family verdict=' + $verdict)
    }
    else {
        $wtScript = Join-Path $PSScriptRoot 'x64dbg-write-trace.ps1'
        if (-not (Test-Path -LiteralPath $wtScript)) {
            Write-Od048 ('auto_write_trace FAILED script_missing=' + $wtScript)
        }
        else {
            if ([string]::IsNullOrWhiteSpace($AutoTraceResultPath)) {
                $dataDir = Join-Path $RepoRoot '.data'
                if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
                $AutoTraceResultPath = Join-Path $dataDir ("od-048-autotrace-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
            }
            Write-Od048 ('auto_write_trace INVOKING verdict=' + $verdict + ' family_complete=' + $completeFamilies.Count + ' trace_s=' + $AutoTraceSeconds)
            # Hashtable splat, NOT an array: PowerShell array-splatting of
            # '-Name value' pairs misaligns argument binding (a switch in the
            # middle shifts the following value onto the wrong parameter --
            # live proof: -TraceSeconds received the FamilyFile path and the
            # trace THREW instead of arming). Hashtable splat binds by name
            # and is immune to the shift. (Reproduced in a minimal repro:
            # array-splat throws, hashtable-splat binds correctly.)
            $wtArgs = @{
                FamilyFile     = $ResultPath
                AutoWriteTrace = $true
                TraceSeconds   = $AutoTraceSeconds
                ResultPath     = $AutoTraceResultPath
            }
            if ($AutoTraceSkipPlayProbe) { $wtArgs.SkipPlayProbe = $true }
            if ($AutoTraceSkipLivenessCheck) { $wtArgs.SkipLivenessCheck = $true }
            try {
                & $wtScript @wtArgs
                $wtExit = $LASTEXITCODE
            }
            catch {
                $wtExit = -1
                Write-Od048 ('auto_write_trace THREW ' + $_.Exception.Message)
            }
            Write-Od048 ('auto_write_trace exit=' + $wtExit)
            $autoTrace = [ordered]@{
                invokedUtc  = ([DateTime]::UtcNow).ToString('o')
                script      = $wtScript
                familyFile  = $ResultPath
                traceSeconds = $AutoTraceSeconds
                resultPath  = $AutoTraceResultPath
                exitCode    = $wtExit
                # 0 clean window; 7 paused (SPACE and rerun); 8 stale family
                # (never relaunch between M1 and M2). M1 is unaffected either
                # way: the verdict below stands.
                verdict     = if ($wtExit -eq 0) { 'trace-complete' }
                    elseif ($wtExit -eq 7) { 'trace-replay-paused' }
                    elseif ($wtExit -eq 8) { 'trace-family-stale' }
                    else { 'trace-failed' }
            }
            try {
                $atJson = $autoTrace | ConvertTo-Json -Depth 6
                [System.IO.File]::WriteAllText($AutoTraceResultPath, $atJson, (New-Object System.Text.UTF8Encoding($false)))
                Write-Od048 ('auto_trace_report_written=' + $AutoTraceResultPath)
            }
            catch {
                Write-Od048 ('auto_trace_report_write_failed: ' + $_.Exception.Message)
            }
        }
    }
}
else {
    Write-Od048 ('auto_write_trace off (pass -AutoWriteTraceOnVerdict for M2 automation)')
}

Write-Od048 ('done verdict=' + $verdict + ' results=' + $results.Count + ' families=' + $families.Count)
exit 0
