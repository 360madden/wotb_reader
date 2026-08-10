#Requires -Version 5.1
<#
.SYNOPSIS
  HP-diffing session driver (one approved live session). The pre-staged plan:
  docs/operations/record-diffing-groundwork.md ("Live session plan").

.DESCRIPTION
  Sequence:
    1. QUALIFY (offline, real): scripts/python/replay-delta-extractor.py
       --hp-delta --victim-entity <id> (or --damage-dealt --attacker-entity
       <id> with -Track damage-dealt) -> the event-bound dump schedule
       (dump_schedule: a dump just before and after each damage event,
       +-0.2s, plus flat control dumps in the gap segments) and the
       ready-to-paste hp-diff command. Exits 2 unless the target has >= 2
       damage windows (the verdict contract's minimum).
    2. DUMP (live, GATED seam, IMPLEMENTED 2026-08-10): with -LiveAcquire the
       driver POSTs /api/v1/game/discover/entity-region
       (EntityRecordRegionReadRequest/Result - <= 4 KB region, bytes + replay
       time only, OfflineReplayVerified + current authorization) at each
       scheduled replay-clock time (before/after each hit from the extractor's
       dump_schedule plus -ControlTimes for the flat controls), requires
       sameDecodedClockProven on every dump (fail-closed), and writes the
       snapshots file (schema wotbtreader.od.hp-diff.snapshots.v1) with
       strictly increasing replay times. Without a reachable web host the
       driver exits 3 with the contract. Offline-replay mode: pass
       -SnapshotsPath to run the verdict against an already-produced dump
       file.
    3. VERDICT (offline, real): wotbtreader-cli hp-diff <snapshots.json>
       --session <id> --victim <entity> --mode lenient [--direction increment]
       -> buckets, correlates (Lenient first - overkill), confirms under
       Strict, and applies the hardened contract (score 1.0 + flatness 1.0
       + >= 2 exact-sum Strict matches). With -FailOnNoHit the driver exits 1
       when the verdict is not a HIT.

  Tracks: -Track hp (default) correlates a victim's HP drop (decrement);
  -Track damage-dealt correlates the target's scoreboard counter rise
  (increment - the player's own stat; the extractor defaults the attacker to
  the session's viewpoint entity when -VictimEntityId is 0).

  Repeatability: run the identical flow on the second independent replay
  (Dead Rail victim 2549399) and require the matched offsets to agree - the
  Phase-4 rule, applied by the operator after the two sessions.

.EXAMPLE
  # Live acquisition through the gated seam (web host serving the verified
  # offline replay), then the verdict:
  powershell -File scripts/invoke-hp-diffing-session.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b `
      -VictimEntityId 3760578 -LiveAcquire -ControlTimes 30,230 `
      -SnapshotsPath .data/hp-snapshots.json

  # Offline-replay verdict against an existing dump file:
  powershell -File scripts/invoke-hp-diffing-session.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b `
      -VictimEntityId 3760578 -SnapshotsPath .data/hp-snapshots.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    # The entity to correlate: the HP victim (Track hp) or the damage-dealt
    # owner (Track damage-dealt; 0 = default to the session's viewpoint
    # entity - the player's own stat).
    [long]$VictimEntityId = 0,
    # Which stat to correlate: 'hp' (decrement) or 'damage-dealt' (increment).
    [ValidateSet('hp', 'damage-dealt')]
    [string]$Track = 'hp',
    # App data root that holds the decoded treader DB (hp-diff --data-root).
    [string]$DataRoot = '',
    # Path to the dump file. In OFFLINE verdict mode this must exist (the
    # snapshots schema wotbtreader.od.hp-diff.snapshots.v1); in live
    # acquisition mode (-LiveAcquire) it is the OUTPUT path (defaults to
    # .data/hp-snapshots-<session>-<stamp>.json).
    [string]$SnapshotsPath = '',
    # Acquire the region dumps live through the GATED seam
    # (POST /api/v1/game/discover/entity-region). Requires the web host to be
    # serving (rendezvous) with the offline replay verified; without a
    # reachable host the driver exits 3 with the contract.
    [switch]$LiveAcquire,
    # Region length in bytes for each dump (>= 4, multiple of 4, <= 4096).
    # 256 covers the ring record (0x38) plus the +0x48 HP candidate and
    # neighboring fields in one dump.
    [ValidateRange(4, 4096)]
    [int]$RegionLength = 256,
    # Comma-separated replay-clock seconds for flat CONTROL dumps in the
    # no-damage segments (e.g. '30,230' for Oasis Palms). Optional in live
    # mode; the verdict's flatness check needs >= 2 control windows.
    [string]$ControlTimes = '',
    [int]$WindowSeconds = 10,
    [string]$Python = 'python',
    [string]$CliDll = '',
    [switch]$FailOnNoHit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ("hp_session: " + $Message)
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($CliDll -eq '') {
    # Prefer the DEBUG build: the published CLI (.\.build\publish\cli) can be
    # stale (built before the hp-diff command existed / at an older schema),
    # while a normal build produces the current Debug assembly.
    $CliDll = Join-Path $RepoRoot 'src\WotBTreader.Host.Cli\bin\Debug\net10.0\WotBTreader.Host.Cli.dll'
}
if (-not (Test-Path -LiteralPath $CliDll)) {
    throw "CLI not built: $CliDll (run 'dotnet build src/WotBTreader.Host.Cli' first)."
}

# ---- 1. QUALIFY (offline, real) -----------------------------------------
$IsIncrement = ($Track -eq 'damage-dealt')
$TrackLabel = if ($IsIncrement) { 'damage-dealt' } else { 'HP victim' }
Write-Step "Qualifying $TrackLabel $VictimEntityId for session $SessionId..."
# Native tools write informational lines to stderr; under
# $ErrorActionPreference = 'Stop' (PowerShell 7) those become terminating
# NativeCommandError, so the calls below run with EAP temporarily Continue.
$OldErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    if ($IsIncrement) {
        $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
            --session $SessionId --damage-dealt --attacker-entity $VictimEntityId `
            --window $WindowSeconds 2>$null) | Out-String
    } else {
        if ($VictimEntityId -le 0) {
            throw "-VictimEntityId is required for -Track hp."
        }
        $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
            --session $SessionId --hp-delta --victim-entity $VictimEntityId `
            --window $WindowSeconds 2>$null) | Out-String
    }
} finally {
    $ErrorActionPreference = $OldErrorActionPreference
}
$Qualification = $QualificationJson | ConvertFrom-Json
if ($null -eq $Qualification) {
    throw "Qualification failed - is the session decoded in the treader DB (and is the tick unit self-test passing, python scripts/python/replay-delta-extractor.py --self-test)?"
}

$HpDelta = if ($IsIncrement) { $Qualification.damage_dealt } else { $Qualification.hp_delta }
if ($null -eq $HpDelta) {
    throw "Qualification failed - no $Track track data for session $SessionId."
}
$TargetEntityId = if ($IsIncrement) { $HpDelta.attacker_entity_id } else { $HpDelta.victim_entity_id }
$HitWindows = $HpDelta.hit_windows
Write-Step ("Target {0}: {1} hit window(s), {2} total damage, {3} dump entries." -f `
    $TargetEntityId, $HitWindows, $HpDelta.total_damage, $HpDelta.dump_schedule.Count)
if ($HitWindows -lt 2) {
    Write-Host "hp_session: target has fewer than 2 damage windows - the verdict contract needs >= 2. Pick a different target."
    exit 2
}

Write-Step "Event-bound dump schedule (dump just before and after each hit, plus flat control dumps):"
$HpDelta.dump_schedule | ForEach-Object {
    Write-Host ("hp_session:   hit at {0,7}s dmg {1,5} -> dump {2,7}s and {3,7}s" -f `
        $_.hit_replay_s, $_.damage, $_.dump_before_s, $_.dump_after_s)
}
Write-Host "hp_session:   + 2-3 flat control dumps in the no-damage segments (e.g. ~30s and ~230s for Oasis Palms)."
Write-Host ("hp_session: verdict command: " + $HpDelta.commands.hp_diff)

# ---- 2. DUMP (live, gated seam) ------------------------------------------
function Get-HpRendezvous {
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

function Invoke-HpApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )
    $rendezvous = Get-HpRendezvous
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
    # No dump file and no live acquisition requested: print the contract and
    # exit 3 (the gated seam is now IMPLEMENTED - use -LiveAcquire when the
    # web host is serving the verified offline replay).
    Write-Host @"
hp_session: no snapshots file and no -LiveAcquire; the live dump acquisition
is the GATED seam (POST /api/v1/game/discover/entity-region -
EntityRecordRegionReadRequest/Result, <= 4 KB region, bytes + replay time
only, OfflineReplayVerified + current authorization). Start the web host,
verify the offline replay, then re-run with -LiveAcquire (or pass
-SnapshotsPath to run the verdict against an existing dump file).
"@
    exit 3
}

if (-not $OfflineDumpExists) {
    # ---- live acquisition through the gated seam ----
    Write-Step "Acquiring region dumps live via /discover/entity-region (region=$RegionLength)..."
    if (($RegionLength % 4) -ne 0) {
        throw "-RegionLength must be a multiple of 4 (the snapshots schema requires it)."
    }
    if (($null -eq $HpDelta.dump_schedule) -or ($HpDelta.dump_schedule.Count -lt 1)) {
        throw "No dump schedule from the extractor - cannot acquire dumps."
    }

    # Dump times: before+after each hit (event-bound) + flat control times.
    $DumpTimes = [System.Collections.Generic.List[double]]::new()
    foreach ($entry in $HpDelta.dump_schedule) {
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
    $ControlCount = 0
    foreach ($t in ($DumpTimes | Sort-Object -Unique)) {
        Write-Step ("  region dump at replay {0,7}s (entity {1})..." -f $t, $TargetEntityId)
        $response = Invoke-HpApi -Method 'Post' -RelativePath '/api/v1/game/discover/entity-region' `
            -Body @{
                entityId        = $TargetEntityId
                regionLength    = $RegionLength
                battleSessionId = $SessionId
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
        $ControlCount++
    }
    if ($Snapshots.Count -lt 2) {
        throw "Fewer than 2 region dumps acquired - the verdict needs at least one hit window and one control."
    }

    # The bucketer requires strictly increasing replay times; the extractor's
    # times are already ascending after Sort-Object -Unique, but the LIVE
    # clocks can jitter - re-sort by the response's replayTimeSeconds and
    # drop any non-increasing duplicates fail-closed.
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
        $SnapshotsPath = Join-Path $RepoRoot ".data\hp-snapshots-$SessionId-$stamp.json"
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
Write-Step "Running hp-diff verdict (direction: $(if ($IsIncrement) { 'increment' } else { 'decrement' }))..."
$DataRootArgs = if ($DataRoot -ne '') { @('--data-root', $DataRoot) } else { @() }
$DirectionArgs = if ($IsIncrement) { @('--direction', 'increment') } else { @() }
$ErrorActionPreference = 'Continue'
try {
    $VerdictJson = (& dotnet $CliDll hp-diff $SnapshotsPath `
        --session $SessionId --victim $TargetEntityId --mode lenient `
        --json @DataRootArgs @DirectionArgs 2>$null) | Out-String
} finally {
    $ErrorActionPreference = $OldErrorActionPreference
}
$Verdict = $VerdictJson | ConvertFrom-Json
if ($null -eq $Verdict -or -not $Verdict.success) {
    $ErrorText = if ($null -ne $Verdict) { ($Verdict.errors | ForEach-Object { $_.message }) -join '; ' } else { 'no output' }
    throw "hp-diff failed: $ErrorText"
}

$VerdictData = $Verdict.data
$Hit = $VerdictData.verdict.hit
Write-Step ("VERDICT: hit={0} reason='{1}'" -f $Hit, $VerdictData.verdict.reason)
# ConvertFrom-Json can drop null-valued members, so probe the property table
# instead of touching $VerdictData.topCandidate directly (StrictMode throws
# PropertyNotFoundException when the member is absent).
$TopCandidateProperty = $VerdictData.PSObject.Properties['topCandidate']
if ($null -ne $TopCandidateProperty -and $null -ne $TopCandidateProperty.Value) {
    $TopCandidate = $TopCandidateProperty.Value
    Write-Step ("top candidate offset 0x{0:X} score {1} flatness {2} matched {3}/{4}" -f `
        [int]$TopCandidate.offset, $TopCandidate.score, `
        $TopCandidate.flatness, $TopCandidate.matchedDamageWindows, `
        $TopCandidate.totalDamageWindows)
    $TopCandidate.matchedWindows | ForEach-Object {
        Write-Host ("hp_session:   matched window ({0:0.###}, {1:0.###}] sum {2}" -f `
            $_.fromSeconds, $_.toSeconds, $_.damageSum)
    }
}

if ($FailOnNoHit -and -not $Hit) {
    exit 1
}
exit 0
