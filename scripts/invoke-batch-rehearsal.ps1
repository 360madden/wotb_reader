#Requires -Version 5.1
<#
.SYNOPSIS
  Batch N-entity rehearsal driver (one approved live session). Pre-staged
  plan: docs/operations/batch-entity-read-design.md (item 3 - the X2
  rehearsal of the per-frame live read surface).

.DESCRIPTION
  Sequence:
    0. QUALIFY (offline, real): scripts/python/batch-rehearsal-crosscheck.py
       --roster -> the decoded roster (participant entity ids, team order)
       + session duration. With -Times the operator pins the replay-clock
       dump times; otherwise 5 evenly spaced times are derived from the
       duration.
    1. ENUMERATE (optional, live, GATED seam, X3): with -EnumerateLive,
       POST /api/v1/game/discover/entity-roster -> the avatar-family ids
       enumerated from the game's own entity maps + the movement-filter
       precision counters. Verdict against the decoded roster (matched /
       missing / extra, fail-closed on TraversalLimited). With -LiveAcquire
       the ENUMERATED ids drive the batch dumps instead of the decoded
       roster ids (the X3 rehearsal: enumerate -> filter -> batch-read ->
       cross-check). Writes the enumeration evidence file (schema
       wotbtreader.od.batch-rehearsal.roster-enum.v1).
    2. DUMP (live, GATED seam): POST /api/v1/game/discover/entity-regions
       with the WHOLE roster (decoded or enumerated) in one batch per time
       (ring-record anchor; the published position chain reads the float32
       triple at record +0x10), requiring status 'Resolved' AND
       sameDecodedClockProven on every batch (fail-closed). Writes the
       dumps file (schema wotbtreader.od.batch-rehearsal.dumps.v1).
       Without a reachable web host the driver exits 3 with the contract.
    3. VERDICT (offline, real): python --compare reads the dumps + the
       decoded position_samples (nearest-sample at the batch's replay
       label) and reports per-entity deltas. Exit 0 = every compared pair
       within -ToleranceMeters; with -FailOnMiss the driver exits 1 on any
       miss or no-verdict.

  The rehearsal proves the batch surface end-to-end on a replay before live
  mode needs it: full roster per frame, ONE clock attestation per batch,
  memory positions aligned to decoded ground truth.

.EXAMPLE
  # Enumerate the live roster and verdict it against the decoded roster:
  powershell -File scripts/invoke-batch-rehearsal.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b -EnumerateLive `
      -FailOnMiss

  # Live acquisition through the gated seam, then the verdict:
  powershell -File scripts/invoke-batch-rehearsal.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b -LiveAcquire `
      -Times 90,150,220 -FailOnMiss

  # The full X3 rehearsal: enumerate live, then dump the ENUMERATED ids:
  powershell -File scripts/invoke-batch-rehearsal.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b `
      -EnumerateLive -LiveAcquire -Times 90,150,220 -FailOnMiss

  # Offline verdict against an existing dumps file:
  powershell -File scripts/invoke-batch-rehearsal.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b `
      -DumpsPath .data/rehearsal-clean.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    # Comma-separated replay-clock seconds to dump (e.g. '90,150,220').
    # Empty -> 5 evenly spaced times across the session duration.
    [string]$Times = '',
    # Path to the decoded treader SQLite store. Empty -> the repo-local
    # .data/treader.db (the extractor's default).
    [string]$DbPath = '',
    # Region length in bytes per entity (multiple of 4, <= 4096). 64 covers
    # the 0x38-byte ring record + the position float32 triple at +0x10.
    [ValidateRange(4, 4096)]
    [int]$RegionLength = 64,
    # Region anchor for the dump. The position cross-check reads the
    # ring-record layout, so 'ring-record' is the only meaningful default.
    [ValidateSet('ring-record', 'entity-tank-record', 'entity-base')]
    [string]$RegionAnchor = 'ring-record',
    # Position match tolerance in meters (the cross-check verdict).
    [ValidateRange(0.01, 1000.0)]
    [double]$ToleranceMeters = 2.0,
    # Acquire the batch dumps live through the GATED seam
    # (POST /api/v1/game/discover/entity-regions). Requires the web host to
    # be serving (rendezvous) with the offline replay verified; without a
    # reachable host the driver exits 3 with the contract.
    [switch]$LiveAcquire,
    # Enumerate the live avatar-family roster first
    # (POST /api/v1/game/discover/entity-roster, X3) and verdict it against
    # the decoded participants roster (matched/missing/extra + filter
    # precision). With -LiveAcquire the ENUMERATED ids drive the batch dumps
    # (the X3 rehearsal: enumerate -> filter -> batch-read -> cross-check).
    # Writes the enumeration evidence file (schema
    # wotbtreader.od.batch-rehearsal.roster-enum.v1).
    [switch]$EnumerateLive,
    # Path to the enumeration evidence file. With -EnumerateLive it is the
    # OUTPUT path (defaults to
    # .data/roster-enum-<session>-<stamp>.json); it is never read as input.
    [string]$EnumPath = '',
    # Path to the dumps file. In OFFLINE verdict mode this must exist; in
    # live acquisition mode (-LiveAcquire) it is the OUTPUT path (defaults
    # to .data/batch-rehearsal-<session>-<stamp>.json).
    [string]$DumpsPath = '',
    [string]$Python = 'python',
    # Exit 1 when a verdict is not a clean PASS (enumeration and/or
    # position cross-check).
    [switch]$FailOnMiss
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ("batch_rehearsal: " + $Message)
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($DbPath -eq '') {
    $DbPath = Join-Path $RepoRoot '.data\treader.db'
}
$CrossCheck = Join-Path $RepoRoot 'scripts\python\batch-rehearsal-crosscheck.py'

# ---- 0. QUALIFY (offline, real) -----------------------------------------
$RosterJson = & $Python $CrossCheck --db $DbPath --session $SessionId --roster
if ($LASTEXITCODE -ne 0) {
    throw "roster qualification failed (exit $LASTEXITCODE): $RosterJson"
}
$Roster = $RosterJson | ConvertFrom-Json
$EntityIds = @($Roster.entityIds)
$DurationSeconds = [double]$Roster.durationSeconds
if ($EntityIds.Count -lt 1) {
    throw "session $SessionId has no roster entities - cannot rehearse."
}
if ($EntityIds.Count -gt 16) {
    throw ("roster has {0} entities but the batch endpoint caps at 16 - " +
        "split the roster or reconsider the target." -f $EntityIds.Count)
}
Write-Step ("Roster: {0} entity(ies), duration {1:0.0}s." -f `
    $EntityIds.Count, $DurationSeconds)

$DumpTimes = [System.Collections.Generic.List[double]]::new()
if ($Times -ne '') {
    foreach ($t in ($Times -split ',')) {
        $parsed = 0.0
        if ([double]::TryParse($t.Trim(), [ref]$parsed)) {
            if ($parsed -le 0 -or $parsed -ge $DurationSeconds) {
                throw ("dump time {0}s is outside (0, {1:0.0})s." -f `
                    $parsed, $DurationSeconds)
            }
            $DumpTimes.Add($parsed)
        }
    }
    if ($DumpTimes.Count -lt 1) {
        throw "no valid dump times parsed from -Times '$Times'."
    }
}
else {
    # 5 evenly spaced times across the battle (skip the first/last seconds).
    $Span = [Math]::Max(1.0, $DurationSeconds - 10.0)
    for ($i = 0; $i -lt 5; $i++) {
        $DumpTimes.Add([Math]::Round($Span * (0.1 + 0.2 * $i), 1))
    }
}
Write-Step ("Dump times: " + (($DumpTimes | ForEach-Object { "{0:0.0}s" -f $_ }) -join ', '))

# ---- Live API helpers (rendezvous + gated call) -------------------------
function Get-RehearsalRendezvous {
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

function Invoke-RehearsalApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )
    $rendezvous = Get-RehearsalRendezvous
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
        $arguments.Body = $Body | ConvertTo-Json -Depth 6 -Compress
    }
    return Invoke-RestMethod @arguments
}

# ---- 1. ENUMERATE (optional, live, gated seam, X3) ----------------------
$EnumVerdict = $null
if ($EnumerateLive) {
    if ($EnumPath -eq '') {
        $Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $EnumPath = Join-Path $RepoRoot ('.data\roster-enum-{0}-{1}.json' -f `
            $SessionId, $Stamp)
    }
    Write-Step "Enumerating the live avatar-family roster via /discover/entity-roster..."
    $enumResponse = $null
    try {
        $enumResponse = Invoke-RehearsalApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/entity-roster'
    }
    catch [InvalidOperationException] {
        Write-Host @"
batch_rehearsal: no reachable web host for the enumeration; start the
web host, verify the offline replay, then re-run with -EnumerateLive.
"@
        exit 3
    }
    if ($null -eq $enumResponse) {
        throw "entity-roster returned no response."
    }
    if ($enumResponse.status -ne 'Resolved') {
        throw ("entity-roster failed: status='{0}' (stage '{1}')." -f `
            $enumResponse.status, $enumResponse.failureStage)
    }
    if ($enumResponse.traversalLimited) {
        throw "entity-roster returned TraversalLimited - the roster is " +
            "partial and cannot be used (fail-closed)."
    }
    $EnumeratedIds = @($enumResponse.entityIds)
    if ($EnumeratedIds.Count -lt 1) {
        throw "entity-roster enumerated 0 avatar ids - wrong phase/session?"
    }
    $EnumEvidence = @{
        schema           = 'wotbtreader.od.batch-rehearsal.roster-enum.v1'
        sessionId        = $SessionId
        status           = [string]$enumResponse.status
        failureStage     = $enumResponse.failureStage
        candidatesSeen   = [int]$enumResponse.candidatesSeen
        filteredOut      = [int]$enumResponse.filteredOut
        moduleRooted     = [bool]$enumResponse.moduleRooted
        traversalLimited = [bool]$enumResponse.traversalLimited
        entityIds        = $EnumeratedIds
    }
    $EnumEvidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EnumPath -Encoding UTF8
    Write-Step ("Enumeration: {0} avatar id(s) from {1} candidates " +
        "({2} filtered out) -> " + $EnumPath -f $EnumeratedIds.Count, `
        [int]$enumResponse.candidatesSeen, [int]$enumResponse.filteredOut)

    # Verdict: enumerated ids vs the decoded participants roster.
    Write-Step "Cross-checking the enumeration against the decoded roster..."
    & $Python $CrossCheck --db $DbPath --session $SessionId `
        --enumeration $EnumPath
    $EnumVerdict = $LASTEXITCODE
    if ($EnumVerdict -ne 0 -and $FailOnMiss) {
        Write-Host (("batch_rehearsal: enumeration verdict exit {0} with " +
            "-FailOnMiss - exiting 1.") -f $EnumVerdict)
        exit 1
    }

    # With -LiveAcquire the ENUMERATED ids drive the batch dumps (the X3
    # rehearsal composes enumeration -> filter -> batch read -> cross-check).
    if ($LiveAcquire) {
        Write-Step ("Using the enumerated ids ({0}) for the batch dumps " +
            "instead of the decoded roster." -f $EnumeratedIds.Count)
        $EntityIds = $EnumeratedIds
    }
}

# ---- 2. DUMP (live, gated seam) -----------------------------------------
$DumpsExist = ($DumpsPath -ne '' -and (Test-Path -LiteralPath $DumpsPath))
$EnumOnly = ($EnumerateLive -and -not $LiveAcquire -and -not $DumpsExist)
if (-not $DumpsExist -and -not $LiveAcquire -and -not $EnumOnly) {
    Write-Host @"
batch_rehearsal: no dumps file and no -LiveAcquire/-EnumerateLive; the live
dump acquisition is the GATED seam (POST /api/v1/game/discover/entity-regions -
one batch per replay time for the whole roster, bytes + one replay-time
label, OfflineReplayVerified + current authorization). Start the web host,
verify the offline replay, then re-run with -LiveAcquire (or pass
-DumpsPath to run the verdict against an existing dumps file).
"@
    exit 3
}

# Enumeration-only mode (-EnumerateLive without -LiveAcquire): the verdict
# is the enumeration cross-check; exit with its code (FailOnMiss already
# short-circuits on a non-zero verdict).
if ($EnumOnly -and $null -ne $EnumVerdict) {
    Write-Host ("batch_rehearsal: enumeration verdict exit {0}." -f $EnumVerdict)
    exit $EnumVerdict
}

if (-not $DumpsExist) {
    if (($RegionLength % 4) -ne 0) {
        throw "-RegionLength must be a multiple of 4."
    }
    if ($DumpsPath -eq '') {
        $Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $DumpsPath = Join-Path $RepoRoot ('.data\batch-rehearsal-{0}-{1}.json' -f `
            $SessionId, $Stamp)
    }

    Write-Step ("Acquiring batch dumps live via /discover/entity-regions " +
        "(region=$RegionLength, anchor=$RegionAnchor)...")
    $DumpTimesOut = [System.Collections.Generic.List[object]]::new()
    foreach ($t in $DumpTimes) {
        $entitiesBody = [System.Collections.Generic.List[object]]::new()
        foreach ($entityId in $EntityIds) {
            $entitiesBody.Add(@{
                entityId     = $entityId
                regionLength = $RegionLength
                regionAnchor = $RegionAnchor
            })
        }
        Write-Step ("  batch dump at replay {0:0.0}s ({1} entities)..." -f `
            $t, $EntityIds.Count)
        $response = Invoke-RehearsalApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/entity-regions' `
            -Body @{
                entities        = $entitiesBody
                battleSessionId = $SessionId
            }
        if ($null -eq $response) {
            throw ("entity-regions returned no response at {0:0.0}s." -f $t)
        }
        if ($response.status -ne 'Resolved') {
            throw ("entity-regions failed at {0:0.0}s: status='{1}'." -f `
                $t, $response.status)
        }
        if (-not $response.sameDecodedClockProven) {
            throw ("entity-regions at {0:0.0}s did not attest the decoded " +
                "clock (sameDecodedClockProven=false) - the batch cannot be " +
                "clock-labeled safely." -f $t)
        }
        if ($null -eq $response.replayTimeSeconds) {
            throw ("entity-regions at {0:0.0}s returned no replay-time label " +
                "despite the clock attestation - refusing to write an " +
                "unlabeled dump." -f $t)
        }
        $label = [double]$response.replayTimeSeconds
        # Force an array: Invoke-RestMethod (PS 5.1 ConvertFrom-Json) collapses
        # single-element JSON arrays to scalars, which would serialize back as
        # an OBJECT and break the cross-check's iteration.
        $regions = @($response.regions)
        if ($regions.Count -lt 1) {
            throw ("entity-regions at {0:0.0}s returned no regions." -f $t)
        }
        $resolvedCount = 0
        foreach ($region in $regions) {
            if ($region.status -eq 'Resolved') { $resolvedCount++ }
        }
        if ($resolvedCount -lt 1) {
            throw ("entity-regions at {0:0.0}s resolved 0/{1} entities - " +
                "wrong session/roster, or the battle is not active. A frame " +
                "with no resolved entity is useless; failing closed." -f `
                $t, $EntityIds.Count)
        }
        Write-Step ("  batch at {0:0.0}s: {1}/{2} entities resolved " +
            "(label {3:0.00}s)." -f $t, $resolvedCount, $EntityIds.Count, $label)
        $DumpTimesOut.Add(@{
            replayTimeSeconds       = $label
            sameDecodedClockProven  = [bool]$response.sameDecodedClockProven
            entities                = $regions
        })
    }

    $Dumps = @{
        schema       = 'wotbtreader.od.batch-rehearsal.dumps.v1'
        sessionId    = $SessionId
        regionAnchor = $RegionAnchor
        regionLength = $RegionLength
        times        = $DumpTimesOut
    }
    $Dumps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $DumpsPath -Encoding UTF8
    Write-Step ("Wrote dumps: " + $DumpsPath)
}

# ---- 3. VERDICT (offline, real) -----------------------------------------
Write-Step "Cross-checking memory positions against the decoded replay..."
& $Python $CrossCheck --db $DbPath --session $SessionId `
    --dumps $DumpsPath --tolerance $ToleranceMeters
$verdictExit = $LASTEXITCODE
if ($verdictExit -ne 0 -and $FailOnMiss) {
    Write-Host (("batch_rehearsal: verdict exit {0} with -FailOnMiss - " +
        "exiting 1.") -f $verdictExit)
    exit 1
}
Write-Host ("batch_rehearsal: verdict exit {0}." -f $verdictExit)
exit $verdictExit
