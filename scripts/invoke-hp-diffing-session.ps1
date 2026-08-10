#Requires -Version 5.1
<#
.SYNOPSIS
  HP-diffing session driver (one approved live session). The pre-staged plan:
  docs/operations/record-diffing-groundwork.md ("Live session plan").

.DESCRIPTION
  Sequence:
    1. QUALIFY (offline, real): scripts/python/replay-delta-extractor.py
       --hp-delta --victim-entity <id> -> the event-bound dump schedule
       (dump_schedule: a dump just before and after each damage event,
       +-0.2s, plus flat control dumps in the gap segments) and the
       ready-to-paste hp-diff command. Exits 2 unless the victim has >= 2
       damage windows (the verdict contract's minimum).
    2. DUMP (live, GATED seam): the trusted reader acquires the region dumps
       at the scheduled replay-clock times - requires the ONE bounded product
       addition (EntityRecordRegionReadRequest/Result, <= 4 KB region, bytes +
       replay time only, OfflineReplayVerified + current authorization) and
       writes the snapshots file (schema
       wotbtreader.od.hp-diff.snapshots.v1). NOT YET IMPLEMENTED - pass
       -SnapshotsPath to skip this seam and run the verdict against an
       already-produced dump file (the offline-replay mode).
    3. VERDICT (offline, real): wotbtreader-cli hp-diff <snapshots.json>
       --session <id> --victim <entity> --mode lenient -> buckets, correlates
       (Lenient first - overkill), confirms under Strict, and applies the
       hardened contract (score 1.0 + flatness 1.0 + >= 2 exact-sum Strict
       matches). With -FailOnNoHit the driver exits 1 when the verdict is not
       a HIT.

  Repeatability: run the identical flow on the second independent replay
  (Dead Rail victim 2549399) and require the matched offsets to agree - the
  Phase-4 rule, applied by the operator after the two sessions.

.EXAMPLE
  # Offline-replay verdict against an existing dump file:
  powershell -File scripts/invoke-hp-diffing-session.ps1 `
      -SessionId 019fdff7-8dcf-7426-8547-9fb8cc3eb07b `
      -VictimEntityId 3760578 -SnapshotsPath .data/hp-snapshots.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    [Parameter(Mandatory = $true)]
    [long]$VictimEntityId,
    # App data root that holds the decoded treader DB (hp-diff --data-root).
    [string]$DataRoot = '',
    # Path to the dump file (skip the live seam). Must use the snapshots
    # schema wotbtreader.od.hp-diff.snapshots.v1.
    [string]$SnapshotsPath = '',
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
Write-Step "Qualifying victim $VictimEntityId for session $SessionId..."
# Native tools write informational lines to stderr; under
# $ErrorActionPreference = 'Stop' (PowerShell 7) those become terminating
# NativeCommandError, so the calls below run with EAP temporarily Continue.
$OldErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $QualificationJson = (& $Python (Join-Path $RepoRoot 'scripts\python\replay-delta-extractor.py') `
        --session $SessionId --hp-delta --victim-entity $VictimEntityId `
        --window $WindowSeconds 2>$null) | Out-String
} finally {
    $ErrorActionPreference = $OldErrorActionPreference
}
$Qualification = $QualificationJson | ConvertFrom-Json
if ($null -eq $Qualification -or $null -eq $Qualification.hp_delta) {
    throw "Qualification failed - is the session decoded in the treader DB (and is the tick unit self-test passing, python scripts/python/replay-delta-extractor.py --self-test)?"
}

$HpDelta = $Qualification.hp_delta
$HitWindows = $HpDelta.hit_windows
Write-Step ("Victim {0}: {1} hit window(s), {2} total damage, {3} dump entries." -f `
    $HpDelta.victim_entity_id, $HitWindows, $HpDelta.total_damage, $HpDelta.dump_schedule.Count)
if ($HitWindows -lt 2) {
    Write-Host "hp_session: victim has fewer than 2 damage windows - the verdict contract needs >= 2. Pick a different victim (--top-victims)."
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
if ($SnapshotsPath -eq '') {
    Write-Host @"
hp_session: no -SnapshotsPath given; the live dump acquisition is the GATED
seam (EntityRecordRegionReadRequest/Result - one bounded product addition,
<= 4 KB region, bytes + replay time only, OfflineReplayVerified + current
authorization). Dump at the scheduled times above, write the snapshots file
(schema wotbtreader.od.hp-diff.snapshots.v1), then re-run with -SnapshotsPath.
"@
    exit 3
}
if (-not (Test-Path -LiteralPath $SnapshotsPath)) {
    throw "Snapshots file not found: $SnapshotsPath"
}

# ---- 3. VERDICT (offline, real) ------------------------------------------
Write-Step "Running hp-diff verdict..."
$DataRootArgs = if ($DataRoot -ne '') { @('--data-root', $DataRoot) } else { @() }
$ErrorActionPreference = 'Continue'
try {
    $VerdictJson = (& dotnet $CliDll hp-diff $SnapshotsPath `
        --session $SessionId --victim $VictimEntityId --mode lenient `
        --json @DataRootArgs 2>$null) | Out-String
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
if ($null -ne $VerdictData.topCandidate) {
    Write-Step ("top candidate offset 0x{0:X} score {1} flatness {2} matched {3}/{4}" -f `
        [int]$VerdictData.topCandidate.offset, $VerdictData.topCandidate.score, `
        $VerdictData.topCandidate.flatness, $VerdictData.topCandidate.matchedDamageWindows, `
        $VerdictData.topCandidate.totalDamageWindows)
    $VerdictData.topCandidate.matchedWindows | ForEach-Object {
        Write-Host ("hp_session:   matched window ({0:0.###}, {1:0.###}] sum {2}" -f `
            $_.fromSeconds, $_.toSeconds, $_.damageSum)
    }
}

if ($FailOnNoHit -and -not $Hit) {
    exit 1
}
exit 0
