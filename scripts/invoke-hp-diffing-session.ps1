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
       dump_schedule plus -ControlTimes for the flat controls), anchored on
       the ENTITY BASE record (-RegionAnchor 'entity-base', the statically-
       verified HP home: signed int16 at [entity+0xB8], alive byte +0xBA,
       healing int16 at +0x11E per VerifyPlayerHpChain 2026-08-11; region
       length >= 0x120), requires sameDecodedClockProven on every dump
       (fail-closed), and
       writes the snapshots file (schema wotbtreader.od.hp-diff.snapshots.v1)
       with strictly increasing replay times. Without a reachable web host the
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
    # 320 covers the statically-verified entity-base HP fields (current
    # health int16 at +0xB8, alive byte +0xBA, healing int16 at +0x11E)
    # plus neighboring entity fields in one dump.
    [ValidateRange(4, 4096)]
    [int]$RegionLength = 320,
    # Which object the dump anchors on: 'ring-record' (the movement ring
    # record the position resolver reads), 'entity-tank-record' (the
    # per-entity tank record at [entity+0x3C]), or 'entity-base' (the entity
    # base record itself). Static evidence (VerifyPlayerHpChain, 11.19.0.10)
    # pins HP as int16 at [entity+0xB8] on the ENTITY BASE record, so this
    # driver defaults to 'entity-base'.
    [ValidateSet('ring-record', 'entity-tank-record', 'entity-base')]
    [string]$RegionAnchor = 'entity-base',
    # Comma-separated replay-clock seconds for flat CONTROL dumps in the
    # no-damage segments (e.g. '30,230' for Oasis Palms). Optional in live
    # mode; the verdict's flatness check needs >= 2 control windows.
    [string]$ControlTimes = '',
    [int]$WindowSeconds = 10,
    [string]$Python = 'python',
    [string]$CliDll = '',
    [switch]$FailOnNoHit,
    # Bounded tolerance (seconds) for attributing a decoded damage event to
    # the change window that contains its memory write. Live evidence
    # (OD-RECOVERY-087) measured a VARIABLE memory-apply lag of ~1-10 s vs
    # the decoded event time, so the HP driver defaults to 12 s (the
    # measured bound plus margin) for the HP path; damage-dealt (int32
    # counter, increments synchronously with the packets) keeps 0.
    [double]$LagToleranceSeconds = 12
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
# The extractor's default --db is the repo-local .data\treader.db, which does
# NOT hold the launch-matched decode (OD-RECOVERY-086 proved the host store
# at %LOCALAPPDATA%\WotBTreader\treader.db is the store that serves the live
# session; the repo-local copy 404s there). When -DataRoot is given, derive
# the extractor DB from it so QUALIFY reads the SAME store hp-diff reads.
$QualifyDbArgs = @()
if ($DataRoot -ne '') {
    $QualifyDbArgs = @('--db', (Join-Path $DataRoot 'treader.db'))
}
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
            --window $WindowSeconds @QualifyDbArgs 2>$null) | Out-String
    } else {
        if ($VictimEntityId -le 0) {
            throw "-VictimEntityId is required for -Track hp."
        }
        $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
            --session $SessionId --hp-delta --victim-entity $VictimEntityId `
            --window $WindowSeconds @QualifyDbArgs 2>$null) | Out-String
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
        [object]$Body = $null,
        # Used by the wait-probe path only: a single missed rendezvous read
        # (the host publishes web.json every 2 min; a rename race or a brief
        # host hiccup can miss one) must not kill the whole session. Bounded
        # retry with backoff; the DUMP path stays fail-closed immediately.
        [switch]$RetryTransient
    )
    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $rendezvous = Get-HpRendezvous
            if ($null -eq $rendezvous) {
                throw [InvalidOperationException]::new('rendezvous_unavailable')
            }
            break
        }
        catch {
            if (-not $RetryTransient -or $attempt -ge 15) {
                throw
            }
            Start-Sleep -Seconds 3
        }
    }
    if ($attempt -gt 1) {
        Write-Step ("  rendezvous recovered after {0} attempt(s)." -f $attempt)
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

    # Dump times: a DENSE SPAN around each hit + flat control times. Live
    # evidence (OD-RECOVERY-087) measured the game applying a decoded damage
    # event to the health field with a VARIABLE lag of ~1-10 s after the
    # decoded packet time, so a single before/after pair around the decoded
    # time cannot bracket the memory write (both dumps land after the hit,
    # and the health drop lands in a later window the correlator cannot
    # attribute). Dumping every ~2 s from hit-1 to hit+13 makes the change
    # window that contains the memory write small enough that the correlator
    # (with --lag-tolerance) attributes the event to exactly that window.
    $DumpTimes = [System.Collections.Generic.List[double]]::new()
    foreach ($entry in $HpDelta.dump_schedule) {
        if ($null -eq $entry.hit_replay_s) { continue }
        $hit = [double]$entry.hit_replay_s
        $DumpTimes.Add($hit - 1.0)
        for ($offset = 1.0; $offset -le 13.0; $offset += 2.0) {
            $DumpTimes.Add($hit + $offset)
        }
    }
    if ($ControlTimes -ne '') {
        foreach ($t in ($ControlTimes -split ',')) {
            $parsed = 0.0
            if ([double]::TryParse($t.Trim(), [ref]$parsed)) { $DumpTimes.Add($parsed) }
        }
    }

    $Snapshots = [System.Collections.Generic.List[object]]::new()
    $ControlCount = 0
    # End-of-replay protection (OD-RECOVERY-090 re-attempt, 2026-08-11): the
    # last dump target can sit past the replay end (the entity is torn down
    # and the entity-region seam returns a teardown status). Skip those
    # targets instead of aborting the whole session and losing the captured
    # dumps. Teardown statuses only count as end-of-replay when the target is
    # within 40 s of the LAST scheduled dump; anything earlier is a genuine
    # failure and still aborts fail-closed.
    $LastDumpTarget = [double](($DumpTimes | Sort-Object -Unique |
        Measure-Object -Maximum).Maximum)
    $TeardownStatuses = @('UnsupportedReplayController', 'EntityNotFound',
        'ReplaySessionInactive')
    $SkippedEndOfReplay = $false
    foreach ($t in ($DumpTimes | Sort-Object -Unique)) {
        # Wait for the game's replay clock to reach the target time before
        # dumping (same class of fix as the batch driver, OD-RECOVERY-086:
        # the endpoint labels each dump with the CURRENT game clock, so
        # firing all dumps back-to-back lands every dump at the same instant
        # and the before/after damage windows never align). A probe of the
        # same endpoint reads the label cheaply; bounded and fail-closed.
        $waitIterations = 0
        $maxWaitIterations = [int]((180 + $t) / 3)  # ~3s per iteration
        $probeLabel = 0.0
        while ($waitIterations -lt $maxWaitIterations) {
            $probeBody = @{
                entityId        = $TargetEntityId
                regionLength    = $RegionLength
                battleSessionId = $SessionId
                regionAnchor    = $RegionAnchor
            }
            $probe = Invoke-HpApi -Method 'Post' `
                -RelativePath '/api/v1/game/discover/entity-region' `
                -Body $probeBody -RetryTransient
            if ($null -eq $probe) {
                throw ("clock probe returned no response while waiting for " +
                    "{0:0.0}s." -f $t)
            }
            if ($probe.status -ne 'Resolved') {
                if ($TeardownStatuses -contains $probe.status -and
                    $t -ge ($LastDumpTarget - 40)) {
                    Write-Step (("  replay ended before target {0:0.0}s " +
                        "(status='{1}') - skipping end-of-replay dump.") -f `
                        $t, $probe.status)
                    $SkippedEndOfReplay = $true
                    break
                }
                throw (("clock probe failed while waiting for {0:0.0}s: " +
                    "status='{1}'.") -f $t, $probe.status)
            }
            if (-not $probe.sameDecodedClockProven) {
                throw ("clock probe at {0:0.0}s did not attest the decoded " +
                    "clock (sameDecodedClockProven=false) - cannot label a " +
                    "wait target safely." -f $t)
            }
            if ($null -eq $probe.replayTimeSeconds) {
                throw ("clock probe while waiting for {0:0.0}s returned no " +
                    "replay-time label." -f $t)
            }
            $probeLabel = [double]$probe.replayTimeSeconds
            if ($probeLabel -ge ($t - 1.0)) {
                Write-Step (("  clock at {0:0.0}s >= target {1:0.0}s " +
                    "(probes {2}) - dumping.") -f $probeLabel, $t, $waitIterations)
                break
            }
            if ($waitIterations -eq 0) {
                Write-Step (("  waiting for replay {0:0.0}s (clock now " +
                    "{1:0.0}s)...") -f $t, $probeLabel)
            }
            $waitIterations++
            Start-Sleep -Seconds 3
        }
        if ($waitIterations -ge $maxWaitIterations) {
            throw ("clock never reached {0:0.0}s within the bounded wait " +
                "(last probe {1:0.0}s) - the replay may have ended; fail-closed." -f `
                $t, $probeLabel)
        }
        if ($SkippedEndOfReplay) {
            $SkippedEndOfReplay = $false
            continue
        }
        Write-Step ("  region dump at replay {0,7}s (entity {1})..." -f $t, $TargetEntityId)
        $response = Invoke-HpApi -Method 'Post' -RelativePath '/api/v1/game/discover/entity-region' `
            -Body @{
                entityId        = $TargetEntityId
                regionLength    = $RegionLength
                battleSessionId = $SessionId
                regionAnchor    = $RegionAnchor
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
        if ($null -eq $response.replayTimeSeconds) {
            throw ("entity-region at {0}s returned no replay-time label despite " +
                "the clock attestation - refusing to write an unlabeled dump." -f $t)
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

    # The pre-dedupe < 2 check above is not enough: live clock jitter can
    # drop duplicates down below one change window. Re-check AFTER the
    # strict-increase dedupe so the verdict never runs on a degenerate file.
    if ($Final.Count -lt 2) {
        throw ("After the strict-increase dedupe only {0} dump(s) remain - " +
            "the verdict needs at least one hit window and one control. " +
            "Retry the session (the live clock labels collapsed)." -f $Final.Count)
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
# HP is statically pinned as a signed int16 at [entity+0xB8] (VerifyPlayerHpChain,
# 11.19.0.10), so the decrement/HP path scans int16 candidates; the increment
# (damage-dealt, int32 counter) path stays int32-only.
$Int16Args = if (-not $IsIncrement) { @('--int16', 'true') } else { @() }
$LagArgs = if (-not $IsIncrement -and $LagToleranceSeconds -gt 0) {
    @('--lag-tolerance', ([string]$LagToleranceSeconds))
} else { @() }
$ErrorActionPreference = 'Continue'
try {
    $VerdictJson = (& dotnet $CliDll hp-diff $SnapshotsPath `
        --session $SessionId --victim $TargetEntityId --mode lenient `
        --json @DataRootArgs @DirectionArgs @Int16Args @LagArgs 2>$null) | Out-String
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
