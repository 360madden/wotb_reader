#Requires -Version 5.1
<#
.SYNOPSIS
  OD-047 exact-scan session driver (strategy-v3 M1): pause the replay at a
  decoded clock value T1, then run the exact-value scan across the known unit
  variants and record the per-variant collapse from the snapshot baseline.

.DESCRIPTION
  The exact scan (MemoryScanEngine.PassesExact) keeps addresses whose CURRENT
  value is within -ExactTolerance of an absolute -ExactTarget. The in-memory
  replayTime field's unit is unknown, so M1 scans the same frozen pause point
  under three unit variants: seconds, milliseconds, and microseconds (ticks).

  The operator pauses the replay at the decoded T1 frame (the game HUD clock
  shows the battle time; the decoded session's replay_time_ticks locate the
  frame -- see docs/operations/offset-discovery-roadmap.md M1). The roll
  driver's own pause probe (scripts/replay-play-state.ps1) confirms the
  bottom-center HUD icon shows the paused bars before scanning, and warns on
  an accidental resume each round.

  Each variant runs as one roll-driver invocation (fresh snapshot, 1s
  stability re-read rounds, no Space pulses -- the replay must stay paused).
  This driver records each variant's final matched count, writes the survivor
  addresses to a per-variant staging file, and emits a JSON summary to
  .data/od-047-<timestamp>.json (runtime data, never tracked).

  The two-pause fingerprint (M2) re-runs the same scans at a second decoded
  value T2 and intersects each variant's T1/T2 survivor address sets
  (-RunT2). The surviving variant is the one whose intersection is non-empty.

.EXITCODES
  0  M1 completed (variants scanned; report written). The M1 pass/fail
     verdict is in the report (collapse <= 1% of baseline required).
  1  Preflight failure (gate never verified, rendezvous missing).
  2  A variant roll-driver invocation failed.
  3  Report could not be written.
#>
[CmdletBinding()]
param(
    # Decoded pause point T1 in battle-seconds (frame located in the decoded
    # session data; the operator pauses the replay when the HUD clock shows
    # this). Default 60.0s per roadmap M1.
    [double]$T1Seconds = 60.0,
    # Second pause point for the M2 two-pause fingerprint (-RunT2).
    [double]$T2Seconds = 120.0,
    # Absolute tolerance for PassesExact (roadmap M1: 0.05).
    [double]$ExactTolerance = 0.05,
    # Scan value kind (replayTime is an 8-byte Double).
    [ValidateSet('Double', 'Float')]
    [string]$ValueKind = 'Double',
    [int]$MaxRounds = 15,
    # OD-035 snapshot retained-byte budget (0 = engine ceiling, unchanged).
    [long]$SnapshotMaxBytes = 0,
    # JSON summary output. Default .data\od-047-<timestamp>.json (runtime
    # data, never tracked).
    [string]$ResultPath = '',
    # Bypass the pixel pause probe (headless / HUD hidden validation only).
    [switch]$SkipPauseProbe,
    [int]$PauseProbeTimeoutSeconds = 60,
    [int]$WaitVerifiedSeconds = 300,
    # M2: after the M1 variants, prompt the operator to re-pause at T2 and
    # re-run the variants; the driver then intersects the T1/T2 survivor
    # address sets per variant and records the fingerprint in the report.
    [switch]$RunT2,
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
    $ResultPath = Join-Path $dataDir ("od-047-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
}

function Write-Od047([string]$Message) {
    Write-Host ("od047: " + $Message)
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
    try {
        $rv = Get-Rendezvous
        if (-not $rv) { return $null }
        return Invoke-RestMethod -Uri ($rv.baseUri + '/api/v1/game/state') -Headers @{
            'X-WotBTreader-Capability' = [string]$rv.capability
        }
    }
    catch { return $null }
}

# Gate wait (same pattern as od-018-session.ps1).
Write-Od047 'waiting_for_verified_gate'
$deadline = (Get-Date).AddSeconds($WaitVerifiedSeconds)
$state = $null
while ((Get-Date) -lt $deadline) {
    $state = Get-GateState
    if ($state -and $state.verificationState -eq 'OfflineReplayVerified') {
        Write-Od047 'gate=OfflineReplayVerified'
        break
    }
    $vs = if ($state) { [string]$state.verificationState } else { 'no-host' }
    Write-Od047 ("waiting gate=" + $vs)
    Start-Sleep -Seconds 5
}
if (-not $state -or $state.verificationState -ne 'OfflineReplayVerified') {
    Write-Od047 'FAILED gate_never_verified'
    exit 1
}

# Advisory tool pre-flight: System Informer is a supporting operator tool
# (memory map view, suspend/resume, module-base checks) -- never a gate.
$siCheck = Join-Path $RepoRoot 'scripts\system-informer-check.ps1'
$null = & $siCheck
Write-Od047 ("system_informer_check_exit=" + $LASTEXITCODE)

$roll = Join-Path $RepoRoot 'scripts\roll-replay-time-increased.ps1'

function Invoke-VariantScan {
    param(
        [double]$Target,
        [string]$Label,
        [double]$ScanTolerance,
        [string]$ScanValueKind,
        [int]$ScanMaxRounds,
        [long]$ScanSnapshotMaxBytes,
        [bool]$ScanSkipPauseProbe,
        [int]$ScanPauseProbeTimeoutSeconds,
        [string]$RollPath
    )
    $tmpResult = Join-Path $env:TEMP ("od047-variant-" + $Label + ".txt")
    $tmpAddr = Join-Path $env:TEMP ("od047-addr-" + $Label + ".txt")
    $tmpLog = Join-Path $env:TEMP ("od047-log-" + $Label + ".txt")
    Remove-Item -LiteralPath $tmpResult -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tmpAddr -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tmpLog -Force -ErrorAction SilentlyContinue
    Write-Od047 ("variant_" + $Label + " target=" + $Target + " tolerance=" + $ScanTolerance)
    & $RollPath `
        -CompareMode exact `
        -ExactTarget $Target `
        -ExactTolerance $ScanTolerance `
        -ValueKind $ScanValueKind `
        -MaxRounds $ScanMaxRounds `
        -SnapshotMaxBytes $ScanSnapshotMaxBytes `
        -ResultPath $tmpResult `
        -AddressFile $tmpAddr `
        -SkipPauseProbe:$ScanSkipPauseProbe `
        -PauseProbeTimeoutSeconds $ScanPauseProbeTimeoutSeconds 2>&1 | Tee-Object -FilePath $tmpLog
    $exit = $LASTEXITCODE
    $survivors = $null
    if (Test-Path -LiteralPath $tmpResult) {
        $content = Get-Content -LiteralPath $tmpResult -Raw
        if ($content) { $survivors = [int]$content }
    }
    $addresses = @(Get-Content -LiteralPath $tmpAddr -ErrorAction SilentlyContinue | Where-Object { $_ })
    # Round-1 baseline from the roll transcript (the M1 exit criterion is
    # collapse <= ~1% of the snapshot's initial candidate count).
    $baseline = $null
    $rollLine = Get-Content -LiteralPath $tmpLog -ErrorAction SilentlyContinue |
        Where-Object { $_ -match 'round=1 previous=([0-9]+)' } | Select-Object -First 1
    if ($rollLine -and $rollLine -match 'previous=([0-9]+)') {
        $baseline = [long]$Matches[1]
    }
    $collapseRatio = $null
    if ($baseline -gt 0 -and $null -ne $survivors) {
        $collapseRatio = [math]::Round([double]$survivors / [double]$baseline, 6)
    }
    Write-Od047 ("variant_" + $Label + " exit=" + $exit + " final_survivors=" + $survivors + " addresses=" + $addresses.Count + " baseline=" + $baseline)
    if ($exit -ne 0) {
        Write-Od047 ("variant_" + $Label + " FAILED exit=" + $exit)
        return $null
    }
    return @{
        label         = $Label
        target        = $Target
        survivors     = $survivors
        addresses     = $addresses
        baseline      = $baseline
        collapseRatio = $collapseRatio
    }
}

$unitVariants = @(
    @{ Label = 'seconds'; Factor = 1.0 },
    @{ Label = 'milliseconds'; Factor = 1000.0 },
    @{ Label = 'microseconds'; Factor = 1000000.0 }
)

# M1: three unit variants at T1.
Write-Od047 ("M1 pause_at_T1=" + $T1Seconds + "s (pause the replay when the HUD clock shows this; the scan waits for the paused icon)")
$m1Variants = @()
foreach ($uv in $unitVariants) {
    $target = $T1Seconds * $uv.Factor
    $r = Invoke-VariantScan -Target $target -Label ("t1-" + $uv.Label) `
        -ScanTolerance $ExactTolerance -ScanValueKind $ValueKind -ScanMaxRounds $MaxRounds `
        -ScanSnapshotMaxBytes $SnapshotMaxBytes -ScanSkipPauseProbe:$SkipPauseProbe `
        -ScanPauseProbeTimeoutSeconds $PauseProbeTimeoutSeconds -RollPath $roll
    if ($null -eq $r) { exit 2 }
    $m1Variants += $r
}

$report = [ordered]@{
    session        = 'OD-047'
    dateUtc        = (Get-Date).ToUniversalTime().ToString('o')
    t1Seconds      = $T1Seconds
    t2Seconds      = $T2Seconds
    exactTolerance = $ExactTolerance
    valueKind      = $ValueKind
    m1Variants     = $m1Variants
}

if ($RunT2) {
    Write-Od047 ("M2 pause_at_T2=" + $T2Seconds + "s (re-pause the replay; each variant re-scans the new frozen value)")
    $m2Variants = @()
    foreach ($uv in $unitVariants) {
        $target = $T2Seconds * $uv.Factor
        $r = Invoke-VariantScan -Target $target -Label ("t2-" + $uv.Label) `
            -ScanTolerance $ExactTolerance -ScanValueKind $ValueKind -ScanMaxRounds $MaxRounds `
            -ScanSnapshotMaxBytes $SnapshotMaxBytes -ScanSkipPauseProbe:$SkipPauseProbe `
            -ScanPauseProbeTimeoutSeconds $PauseProbeTimeoutSeconds -RollPath $roll
        if ($null -eq $r) { exit 2 }
        $m2Variants += $r
    }
    $report.m2Variants = $m2Variants

    # Two-pause fingerprint: intersect each variant's T1/T2 survivor sets.
    # The true replayTime address must hold both frozen values, so the
    # surviving variant is the one with a non-empty intersection.
    $fingerprint = @()
    for ($i = 0; $i -lt $unitVariants.Count; $i++) {
        $t1 = $m1Variants[$i]
        $t2 = $m2Variants[$i]
        $inter = @($t1.addresses | Where-Object { $_ -in $t2.addresses })
        $fingerprint += [ordered]@{
            variant       = $unitVariants[$i].Label
            t1Count       = $t1.addresses.Count
            t2Count       = $t2.addresses.Count
            intersectionCount = $inter.Count
            addresses     = $inter
        }
        Write-Od047 ("fingerprint variant=" + $unitVariants[$i].Label + " t1=" + $t1.addresses.Count + " t2=" + $t2.addresses.Count + " intersection=" + $inter.Count)
    }
    $report.fingerprint = $fingerprint
}

try {
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding ascii
}
catch {
    Write-Od047 ("FAILED_report_write=" + $_.Exception.Message)
    exit 3
}
Write-Od047 ("report=" + $ResultPath)
Write-Od047 'done'
exit 0
