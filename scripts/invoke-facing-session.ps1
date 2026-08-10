#Requires -Version 5.1
<#
.SYNOPSIS
  Facing/yaw live session driver (one approved live session). The pre-staged
  plan: docs/operations/record-diffing-groundwork.md ("L2 live-session plan").

.DESCRIPTION
  Sequence:
    1. QUALIFY (offline, real): scripts/python/replay-delta-extractor.py
       --yaw-dump [--session <id>] -> the dump-pair schedule (one pair per
       turn segment whose cumulative packet-yaw change >= 0.1 rad, dump
       +/-0.2s around the segment) plus the ready-to-paste yaw-diff command.
       Exits 2 unless the target has >= 2 turn segments (the verdict
       contract's minimum). The schedule is built from the ring-record's
       target (the viewpoint/player entity by default -- the packet yaw is
       the authoritative rotation for the facing track). Windows whose
       expected |delta| sits at or below the 0.05 rad match tolerance are
       CONTROL windows (the correlator's flatness denominator), so the score
       counts only provable turns.
    2. DUMP (live, GATED seam): with -LiveAcquire the driver POSTs
       /api/v1/game/discover/entity-region
       (EntityRecordRegionReadRequest/Result - <= 4 KB region, bytes + replay
       time only, OfflineReplayVerified + current authorization) at each
       scheduled replay-clock time (before/after each turn segment plus
       -ControlTimes for the stationary controls), anchored on the movement
       RING RECORD (-RegionAnchor 'ring-record' -- the yaw candidate lives in
       the ring-record tail +0x2C..+0x37), requires sameDecodedClockProven on
       every dump (fail-closed), and writes the snapshots file (schema
       wotbtreader.od.hp-diff.snapshots.v1 -- the same change-window schema
       the HeadingCorrelator consumes) with strictly increasing replay times.
       Without a reachable web host the driver exits 3 with the contract.
       Offline-replay mode: pass -SnapshotsPath to run the verdict against an
       already-produced dump file.
    3. VERDICT (offline, real): wotbtreader-cli yaw-diff <snapshots.json>
       --session <id> --victim <entity> -> bucketed change windows correlated
       against the packet yaw ground truth: TURN windows (|expected delta| >
       the match tolerance) form the score denominator, stationary CONTROL
       windows (|delta| <= tolerance) form the flatness denominator. With
       -FailOnNoHit the driver exits 1 when
       the verdict is not a HIT.

  Repeatability: run the identical flow on the second independent replay
  (Dead Rail) and require the matched offset to agree - the Phase-4 rule,
  applied by the operator after the two sessions.

.EXAMPLE
  # Live acquisition through the gated seam (web host serving the verified
  # offline replay), then the verdict:
  powershell -File scripts/invoke-facing-session.ps1 `
      -SessionId 019fecb0-f997-765d-9d59-f227e9bd8629 `
      -LiveAcquire -ControlTimes 20,240 `
      -SnapshotsPath .data/facing-snapshots.json

  # Offline-replay verdict against an existing dump file:
  powershell -File scripts/invoke-facing-session.ps1 `
      -SessionId 019fecb0-f997-765d-9d59-f227e9bd8629 `
      -SnapshotsPath .data/facing-snapshots.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    # App data root that holds the decoded treader DB (yaw-diff --data-root).
    [string]$DataRoot = '',
    # Path to the dump file. In OFFLINE verdict mode this must exist; in live
    # acquisition mode (-LiveAcquire) it is the OUTPUT path (defaults to
    # .data/facing-snapshots-<session>-<stamp>.json).
    [string]$SnapshotsPath = '',
    # Acquire the region dumps live through the GATED seam
    # (POST /api/v1/game/discover/entity-region). Requires the web host to be
    # serving (rendezvous) with the offline replay verified; without a
    # reachable host the driver exits 3 with the contract.
    [switch]$LiveAcquire,
    # Region length in bytes for each dump (>= 4, multiple of 4, <= 4096).
    # 256 covers the full ring record (0x38 bytes) with generous headroom.
    [ValidateRange(4, 4096)]
    [int]$RegionLength = 256,
    # Comma-separated replay-clock seconds for flat CONTROL dumps in the
    # stationary segments (e.g. '20,240' for Oasis Palms). Optional in live
    # mode; the verdict's flatness check needs >= 2 control windows.
    [string]$ControlTimes = '',
    # Radians threshold for a turn segment in --yaw-dump (0.1 = 2x the 0.05
    # rad match tolerance; the L2 picker rule).
    [double]$TurnThreshold = 0.1,
    [string]$Python = 'python',
    [string]$CliDll = '',
    [switch]$FailOnNoHit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ("facing_session: " + $Message)
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($CliDll -eq '') {
    $CliDll = Join-Path $RepoRoot 'src\WotBTreader.Host.Cli\bin\Debug\net10.0\WotBTreader.Host.Cli.dll'
}
if (-not (Test-Path -LiteralPath $CliDll)) {
    throw "CLI not built: $CliDll (run 'dotnet build src/WotBTreader.Host.Cli' first)."
}

# ---- 1. QUALIFY (offline, real) -----------------------------------------
Write-Step "Qualifying facing target for session $SessionId (turn threshold $TurnThreshold rad)..."
$OldErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
        --yaw-dump --session $SessionId --turn-threshold $TurnThreshold 2>$null) | Out-String
} finally {
    $ErrorActionPreference = $OldErrorActionPreference
}
$Qualification = $QualificationJson | ConvertFrom-Json
if ($null -eq $Qualification) {
    throw "Qualification failed - is the session decoded in the treader DB (and does it have packet yaw, migration 5+)?"
}
$YawDump = $Qualification.yaw_dump
if ($null -eq $YawDump) {
    throw "Qualification failed - no yaw dump schedule for session $SessionId."
}
$TargetEntityId = $Qualification.entity_id
$Schedule = $YawDump.schedule
Write-Step ("Target entity {0}: {1} turn segment(s) with |packet yaw delta| >= {2} rad." -f `
    $TargetEntityId, $YawDump.turn_segments, $YawDump.turn_threshold_rad)
if (($null -eq $Schedule) -or ($Schedule.Count -lt 2)) {
    Write-Host "facing_session: fewer than 2 turn segments - the verdict contract needs >= 2. Pick a different session/target or lower -TurnThreshold."
    exit 2
}

Write-Step "Dump schedule (before/after each turn segment):"
$Schedule | ForEach-Object {
    Write-Host ("facing_session:   turn {0,7}s expected {1,7}deg -> dump {2,7}s and {3,7}s" -f `
        $_.turn_replay_s, $_.expected_delta_deg, $_.dump_before_s, $_.dump_after_s)
}
Write-Host "facing_session:   + 2-3 flat control dumps in the stationary segments (e.g. ~20s and ~240s for Oasis Palms)."
Write-Host ("facing_session: verdict command: " + $Qualification.commands.yaw_diff)

# ---- 2. DUMP (live, gated seam) ------------------------------------------
function Get-FacingRendezvous {
    try {
        $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -eq $file -or
            $file.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-10)) {
            return $null
        }
        $value = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (-not $value.PSObject.Properties['baseUri'] -or
            -not $value.PSObject.Properties['capability'] -or
            [string]::IsNullOrWhiteSpace([string]$value.baseUri) -or
            [string]::IsNullOrWhiteSpace([string]$value.capability)) {
            return $null
        }
        $uri = [Uri][string]$value.baseUri
        if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') {
            return $null
        }
        return $value
    }
    catch {
        return $null
    }
}

function Invoke-FacingApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )
    $rendezvous = Get-FacingRendezvous
    if ($null -eq $rendezvous) {
        throw [InvalidOperationException]::new('rendezvous_unavailable')
    }
    $arguments = @{
        Uri        = [string]$rendezvous.baseUri + $RelativePath
        Method     = $Method
        TimeoutSec = 60
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

$OfflineDumpExists = ($SnapshotsPath -ne '' -and (Test-Path -LiteralPath $SnapshotsPath))
if (-not $OfflineDumpExists -and -not $LiveAcquire) {
    Write-Host @"
facing_session: no snapshots file and no -LiveAcquire; the live dump acquisition
is the GATED seam (POST /api/v1/game/discover/entity-region, RegionAnchor
ring-record). Start the web host, verify the offline replay, then re-run with
-LiveAcquire (or pass -SnapshotsPath to run the verdict against an existing
dump file).
"@
    exit 3
}

if (-not $OfflineDumpExists) {
    Write-Step "Acquiring ring-record region dumps live via /discover/entity-region (region=$RegionLength, anchor=ring-record)..."
    if (($RegionLength % 4) -ne 0) {
        throw "-RegionLength must be a multiple of 4 (the snapshots schema requires it)."
    }
    if (($null -eq $Schedule) -or ($Schedule.Count -lt 1)) {
        throw "No dump schedule from the extractor - cannot acquire dumps."
    }

    $DumpTimes = [System.Collections.Generic.List[double]]::new()
    foreach ($entry in $Schedule) {
        if ($null -ne $entry.dump_before_s) { $DumpTimes.Add([double]$entry.dump_before_s) }
        if ($null -ne $entry.dump_after_s)  { $DumpTimes.Add([double]$entry.dump_after_s) }
    }
    if ($ControlTimes -ne '') {
        foreach ($t in ($ControlTimes -split ',')) {
            $parsed = 0.0
            if ([double]::TryParse($t.Trim(), [ref]$parsed)) { $DumpTimes.Add($parsed) }
        }
    }

    $Snapshots = [System.Collections.Generic.List[object]]::new()
    foreach ($t in ($DumpTimes | Sort-Object -Unique)) {
        Write-Step ("  ring-record dump at replay {0,7}s (entity {1})..." -f $t, $TargetEntityId)
        $response = Invoke-FacingApi -Method 'Post' -RelativePath '/api/v1/game/discover/entity-region' `
            -Body @{
                entityId        = $TargetEntityId
                regionLength    = $RegionLength
                battleSessionId = $SessionId
                regionAnchor    = 'ring-record'
            }
        if ($null -eq $response -or $response.status -ne 'Resolved') {
            throw ("entity-region failed at {0}s: status='{1}' stage='{2}'" -f `
                $t, $(if ($null -ne $response) { $response.status } else { 'null' }), `
                $(if ($null -ne $response) { $response.failureStage } else { '' }))
        }
        if (-not $response.sameDecodedClockProven) {
            throw ("entity-region at {0}s did not attest the decoded clock " +
                "(sameDecodedClockProven=false) - the dump cannot be clock-labeled safely." -f $t)
        }
        $Snapshots.Add(@{
            replayTimeSeconds = [double]$response.replayTimeSeconds
            bytesBase64       = [string]$response.regionBase64
        })
    }
    if ($Snapshots.Count -lt 2) {
        throw "Fewer than 2 region dumps acquired - the verdict needs at least one turn window and one control."
    }

    $Ordered = $Snapshots | Sort-Object @{ Expression = { $_.replayTimeSeconds } }
    $Final = [System.Collections.Generic.List[object]]::new()
    $last = [double]::NegativeInfinity
    foreach ($s in $Ordered) {
        if ([double]$s.replayTimeSeconds -le $last) { continue }
        $Final.Add($s)
        $last = [double]$s.replayTimeSeconds
    }

    if ($SnapshotsPath -eq '') {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $SnapshotsPath = Join-Path $RepoRoot ".data\facing-snapshots-$SessionId-$stamp.json"
    }
    $snapshotsJson = @{
        schema       = 'wotbtreader.od.hp-diff.snapshots.v1'
        regionLength = $RegionLength
        snapshots    = @($Final)
    } | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $SnapshotsPath -Value $snapshotsJson -Encoding UTF8
    Write-Step ("Wrote {0} region dumps to {1}" -f $Final.Count, $SnapshotsPath)
}
else {
    Write-Step "Using existing snapshots file: $SnapshotsPath"
}

# ---- 3. VERDICT (offline, real) ------------------------------------------
Write-Step "Running yaw-diff verdict..."
$DataRootArgs = if ($DataRoot -ne '') { @('--data-root', $DataRoot) } else { @() }
$ErrorActionPreference = 'Continue'
try {
    $VerdictJson = (& dotnet $CliDll yaw-diff $SnapshotsPath `
        --session $SessionId --victim $TargetEntityId `
        --json @DataRootArgs 2>$null) | Out-String
} finally {
    $ErrorActionPreference = $OldErrorActionPreference
}
$Verdict = $VerdictJson | ConvertFrom-Json
if ($null -eq $Verdict -or -not $Verdict.success) {
    $ErrorText = if ($null -ne $Verdict) { ($Verdict.errors | ForEach-Object { $_.message }) -join '; ' } else { 'no output' }
    throw "yaw-diff failed: $ErrorText"
}

$VerdictData = $Verdict.data
$Hit = $VerdictData.verdict.hit
Write-Step ("VERDICT: hit={0} reason='{1}'" -f $Hit, $VerdictData.verdict.reason)
$TopCandidateProperty = $VerdictData.PSObject.Properties['topCandidate']
if ($null -ne $TopCandidateProperty -and $null -ne $TopCandidateProperty.Value) {
    $TopCandidate = $TopCandidateProperty.Value
    Write-Step ("top candidate offset 0x{0:X} score {1} flatness {2} matched {3}/{4}" -f `
        [int]$TopCandidate.offset, $TopCandidate.score, `
        $TopCandidate.flatness, $TopCandidate.matchedWindows, `
        $TopCandidate.totalWindows)
}

if ($FailOnNoHit -and -not $Hit) {
    exit 1
}
exit 0
