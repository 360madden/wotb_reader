#Requires -Version 5.1
<#
.SYNOPSIS
  Avatar-stats session rehearsal (OFFLINE, no launches). Proves the
  per-candidate verdict protocol end-to-end on a REAL decoded session
  before the first live L3 session (plan:
  docs/operations/l3-damage-dealt-avatar-family-plan.md, step 3). With
  -Phase4SessionId it ALSO runs the Phase-4 two-replay repeat flow
  offline and requires the matched offset to agree across both replays.

.DESCRIPTION
  Sequence:
    1. QUALIFY (offline, real): scripts/python/replay-delta-extractor.py
       --damage-dealt --attacker-entity 0 -> the attacker (own) entity id,
       the per-window damage series, and the event-bound dump schedule.
       Exits 2 unless the target has >= 2 damage windows (the verdict
       contract's minimum).
    2. SYNTHESIZE (offline, synthetic): builds ONE snapshots file PER scan
       candidate (schema wotbtreader.od.hp-diff.snapshots.v1, regionLength
       16 = the battle-stats quad). Candidate 0 is the PERFECT "own"
       counter: the dword at +0x0 rises by exactly each window's damage sum
       across the before/after dump pairs (a perfect live read of the
       damage-dealt counter). Candidates 1..3 stay flat (all-zero quads) -
       the built-in control windows the increment correlator discriminates
       against at scoring time.
    3. VERDICT (offline, real): wotbtreader-cli hp-diff <file> --session
       <id> --victim <attacker> --mode lenient --direction increment
       --data-root <DataRoot> per candidate. Expects candidate 0 to HIT
       (score 1.0, flatness 1.0, >= 2 exact-sum Strict matches) and every
       other candidate to NOT hit (flat quads produce no change windows).
    4. PHASE-4 (offline, with -Phase4SessionId): repeat 1-3 on the second
       replay and require the matched offset to AGREE with the first -
       the two-replay repeat rule, proven offline before any launch.

  The rehearsal proves: the 16-byte quad snapshots schema, the increment
  correlator's own-counter discrimination, the per-candidate file protocol
  the live session reuses (invoke-hp-diffing-session.ps1 -Track
  damage-dealt -RegionAnchor avatar-stats writes the same -candN.json
  files), and the two-replay repeat flow.

.EXAMPLE
  # Single-replay protocol proof:
  powershell -File scripts/invoke-avatar-stats-rehearsal.ps1 `
      -SessionId 019fa44a-b226-77ae-94de-a27419f23204 -FailOnNoHit

  # Phase-4 two-replay simulation (savanna + medvedkovo, offsets must agree):
  powershell -File scripts/invoke-avatar-stats-rehearsal.ps1 `
      -SessionId 019fa44a-b226-77ae-94de-a27419f23204 `
      -Phase4SessionId 019fb86c-c8e7-7004-9df6-a574f5a7835b -FailOnNoHit
#>
[CmdletBinding()]
param(
    # Any decoded session with >= 2 own-attacker damage windows in the
    # DataRoot store (list with: wotbtreader-cli sessions).
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    # Second decoded session for the Phase-4 two-replay repeat simulation;
    # the matched offset must agree with the primary session.
    [string]$Phase4SessionId = '',
    # Data root holding treader.db with the decoded sessions (the CLI reads
    # <DataRoot>\treader.db). Defaults to the repo-local .data copy.
    [string]$DataRoot = '.data',
    # Where the synthetic snapshots land for the PRIMARY session (default:
    # .data\avatar-stats-rehearsal-<session>-<stamp>).
    [string]$OutputDir = '',
    [string]$Python = 'python',
    [string]$CliDll = '',
    [int]$WindowSeconds = 10,
    [switch]$FailOnNoHit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ("avatar_rehearsal: " + $Message)
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($CliDll -eq '') {
    # Prefer the DEBUG build (same default as invoke-hp-diffing-session.ps1):
    # the published CLI (.\\.build\\publish\\cli) can be stale.
    $CliDll = Join-Path $RepoRoot 'src\WotBTreader.Host.Cli\bin\Debug\net10.0\WotBTreader.Host.Cli.dll'
}
if (-not (Test-Path -LiteralPath $CliDll)) {
    throw "CLI not built: $CliDll (run 'dotnet build src/WotBTreader.Host.Cli' first)."
}
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$DataRootArgs = @('--data-root', $DataRoot)
$DirectionArgs = @('--direction', 'increment')

function Invoke-Verdict {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        # The ground-truth session for THIS replay (never the primary
        # session when called from the phase-4 pass).
        [Parameter(Mandatory = $true)]
        [string]$Session,
        [Parameter(Mandatory = $true)]
        [long]$VictimEntityId
    )
    $ErrorActionPreference = 'Continue'
    try {
        $Json = (& dotnet $CliDll hp-diff $Path `
            --session $Session --victim $VictimEntityId --mode lenient `
            --json @DataRootArgs @DirectionArgs 2>$null) | Out-String
    } finally {
        $ErrorActionPreference = $OldErrorActionPreference
    }
    $Parsed = $Json | ConvertFrom-Json
    if ($null -eq $Parsed -or -not $Parsed.success) {
        $ErrorText = if ($null -ne $Parsed) { ($Parsed.errors | ForEach-Object { $_.message }) -join '; ' } else { 'no output' }
        throw "hp-diff failed for ${Path}: $ErrorText"
    }
    return $Parsed.data
}

# Runs the QUALIFY -> SYNTHESIZE -> VERDICT flow for one session. Returns
# the matched offset of the incrementer (int), or -1 when it did not hit.
function Invoke-SessionRehearsal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Session,
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [string]$Python,
        [Parameter(Mandatory = $true)]
        [int]$WindowSeconds
    )
    Write-Step ("[$Label] Qualifying damage-dealt ground truth for session $Session...")
    $OldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
            --session $Session --damage-dealt --attacker-entity 0 `
            --window $WindowSeconds 2>$null) | Out-String
    } finally {
        $ErrorActionPreference = $OldErrorActionPreference
    }
    $Qualification = $QualificationJson | ConvertFrom-Json
    if ($null -eq $Qualification) {
        throw "Qualification failed for $Session - is it decoded in the DataRoot store (and is the tick unit self-test passing, python scripts/python/replay-delta-extractor.py --self-test)?"
    }
    $HpDelta = $Qualification.damage_dealt
    if ($null -eq $HpDelta) {
        throw "Qualification failed for $Session - no damage-dealt track data."
    }
    $AttackerEntityId = $HpDelta.attacker_entity_id
    $HitWindows = $HpDelta.hit_windows
    $Schedule = @($HpDelta.dump_schedule)
    Write-Step ("[$Label] Attacker {0}: {1} hit window(s), {2} total damage, {3} schedule entries." -f `
        $AttackerEntityId, $HitWindows, $HpDelta.total_damage, $Schedule.Count)
    if ($HitWindows -lt 2) {
        Write-Host "avatar_rehearsal: [$Label] fewer than 2 damage windows - the verdict contract needs >= 2. Pick a different session."
        return -1
    }

    # SYNTHESIZE: candidate 0 = the perfect own counter (dword0 rises by
    # exactly each hit's damage across its before/after pair); candidates
    # 1..3 stay all-zero (flat). Controls sit in damage-free segments and
    # stay unchanged.
    $DumpTimes = [System.Collections.Generic.List[double]]::new()
    $DumpValues = [System.Collections.Generic.List[int]]::new()
    $FirstHit = [double]$Schedule[0].hit_replay_s
    $Control1 = [Math]::Min(20.0, [Math]::Max(2.0, $FirstHit - 40.0))
    $DumpTimes.Add($Control1)
    $DumpValues.Add(0)
    $DumpTimes.Add($Control1 + 2.0)
    $DumpValues.Add(0)
    $Cumulative = 0
    foreach ($entry in $Schedule) {
        $damage = [int]$entry.damage
        $DumpTimes.Add([double]$entry.dump_before_s)
        $DumpValues.Add($Cumulative)
        $Cumulative += $damage
        $DumpTimes.Add([double]$entry.dump_after_s)
        $DumpValues.Add($Cumulative)
    }
    $LastHit = [double]$Schedule[$Schedule.Count - 1].hit_replay_s
    $DumpTimes.Add($LastHit + 20.0)
    $DumpValues.Add($Cumulative)

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $QuadBase = [byte[]](0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    $IncrementerFiles = [System.Collections.Generic.List[string]]::new()
    $FlatFiles = [System.Collections.Generic.List[string]]::new()
    for ($cand = 0; $cand -lt 4; $cand++) {
        $Snapshots = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $DumpTimes.Count; $i++) {
            $region = [byte[]]$QuadBase.Clone()
            if ($cand -eq 0) {
                [System.Buffer]::BlockCopy([BitConverter]::GetBytes($DumpValues[$i]), 0, $region, 0, 4)
            }
            $Snapshots.Add(@{
                replayTimeSeconds = $DumpTimes[$i]
                bytesBase64       = [Convert]::ToBase64String($region)
            })
        }
        $CandPath = Join-Path $OutputDirectory ("hp-snapshots-$Session-cand$cand.json")
        $snapshotsJson = @{
            schema       = 'wotbtreader.od.hp-diff.snapshots.v1'
            regionLength = 16
            snapshots    = @($Snapshots)
        } | ConvertTo-Json -Depth 5
        Set-Content -LiteralPath $CandPath -Value $snapshotsJson -Encoding UTF8
        if ($cand -eq 0) { $IncrementerFiles.Add($CandPath) } else { $FlatFiles.Add($CandPath) }
    }
    Write-Step ("[$Label] wrote {0} dumps per candidate (16 bytes each) to {1}" -f $DumpTimes.Count, $OutputDirectory)

    # VERDICT per candidate.
    $IncrementerHit = $false
    $MatchedOffset = -1
    foreach ($Path in $IncrementerFiles) {
        $Data = Invoke-Verdict -Path $Path -Session $Session -VictimEntityId $AttackerEntityId
        $Hit = $Data.verdict.hit
        Write-Step ("[$Label] VERDICT [incrementer]: hit={0} reason='{1}'" -f $Hit, $Data.verdict.reason)
        $TopProperty = $Data.PSObject.Properties['topCandidate']
        if ($null -ne $TopProperty -and $null -ne $TopProperty.Value) {
            $Top = $TopProperty.Value
            Write-Step ("[$Label]   top candidate offset 0x{0:X} score {1} flatness {2} matched {3}/{4}" -f `
                [int]$Top.offset, $Top.score, $Top.flatness, $Top.matchedDamageWindows, $Top.totalDamageWindows)
            if ($Hit) { $MatchedOffset = [int]$Top.offset }
        }
        $IncrementerHit = $IncrementerHit -or $Hit
    }
    $AnyFlatHit = $false
    foreach ($Path in $FlatFiles) {
        $Data = Invoke-Verdict -Path $Path -Session $Session -VictimEntityId $AttackerEntityId
        $Hit = $Data.verdict.hit
        Write-Step ("[$Label] VERDICT [flat candidate]: hit={0} reason='{1}'" -f $Hit, $Data.verdict.reason)
        if ($Hit) { $AnyFlatHit = $true }
    }

    if (-not $IncrementerHit) {
        Write-Host "avatar_rehearsal: [$Label] the synthesized perfect counter did NOT hit - the verdict protocol is broken (snapshots schema, window attribution, or per-candidate flow). Fix before any live session."
        return -1
    }
    if ($AnyFlatHit) {
        Write-Host "avatar_rehearsal: [$Label] a FLAT candidate hit - the discriminator is not separating the own counter from flat controls. Fix before any live session."
        return -1
    }
    return $MatchedOffset
}

# ---- PRIMARY session ----
$PrimaryDir = if ($OutputDir -ne '') {
    $OutputDir
} else {
    Join-Path $RepoRoot (".data\avatar-stats-rehearsal-$SessionId-$Stamp")
}
$OffsetPrimary = Invoke-SessionRehearsal `
    -Session $SessionId -OutputDirectory $PrimaryDir -Label 'primary' `
    -Python $Python -WindowSeconds $WindowSeconds
if ($OffsetPrimary -lt 0) {
    Write-Host "avatar_rehearsal: primary rehearsal FAILED (offset $OffsetPrimary)."
    exit 1
}

# ---- PHASE-4 second-replay simulation ----
if ($Phase4SessionId -ne '') {
    $Phase4Dir = Join-Path $RepoRoot (".data\avatar-stats-rehearsal-$Phase4SessionId-$Stamp")
    $OffsetPhase4 = Invoke-SessionRehearsal `
        -Session $Phase4SessionId -OutputDirectory $Phase4Dir -Label 'phase4' `
        -Python $Python -WindowSeconds $WindowSeconds
    if ($OffsetPhase4 -lt 0) {
        Write-Host "avatar_rehearsal: phase-4 rehearsal FAILED (offset $OffsetPhase4)."
        exit 1
    }
    if ($OffsetPrimary -ne $OffsetPhase4) {
        Write-Host ("avatar_rehearsal: PHASE-4 MISMATCH - primary matched 0x{0:X}, phase-4 matched 0x{1:X}. The two-replay repeat rule fails." -f `
            $OffsetPrimary, $OffsetPhase4)
        exit 1
    }
    Write-Step ("PHASE-4 SIMULATION PASSED: matched offset 0x{0:X} agrees across both replays ({1} + {2})." -f `
        $OffsetPrimary, $SessionId, $Phase4SessionId)
}

if ($FailOnNoHit) {
    Write-Step "Rehearsal PASSED (with -FailOnNoHit): the increment correlator discriminates the own counter from flat candidates on session $SessionId."
}
else {
    Write-Step "Rehearsal PASSED: the increment correlator discriminates the own counter from flat candidates on session $SessionId."
}
exit 0
