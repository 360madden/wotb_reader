#Requires -Version 5.1
<# ==========================================================================
.SYNOPSIS
  OD-044 replayTime live session driver: roll -> stage survivors -> C#
  guard-page interceptor capture -> verdict, in ONE approved launch.

.DESCRIPTION
  Closes the OD-016/031/036 handoff gap (the roll consumed the ~120s research
  lease and the operator window was EvidenceStale by the time survivors <= 10
  were staged). The changed hypothesis: arm the C# guard-page interceptor
  (tools/WriteInterceptor) the moment the roll lands - same process, same
  lease, no x64dbg/CE (both write-BP routes are closed). Plan:
  docs/operations/replaytime-live-attempt-plan.md.

  Sequence:
    1. Gate wait - /api/v1/game/state until OfflineReplayVerified (exit 3).
    2. Roll - scripts/roll-replay-time-increased.ps1 -TargetSurvivors N
       -AddressFile <file> (two-phase pulses, 401 refresh, KUSER clock drop
       built in). Nonzero exit -> diagnose gate (lease vs API) and stop.
    3. Stage check - >= 2 survivor addresses, all 0x tokens, none on
       0x7FFE0xxx (re-check defensively), warn on count != survivors.
    4. Arm + capture - WriteInterceptor.exe --interceptor -Pid <game pid>
       -Addresses <csv> -Seconds <trace> -Out <capture.json>. Trace window
       budgeted against the battle tail (battleEnd - 15s margin, floored 10s,
       ceilinged at -TraceSeconds) and the gate re-verified before arming.
    5. Verdict - parse the capture report; promote a durable
       <ResultPath>.capture.json (FRESH36 lesson: never lose modules/rva to
       an ephemeral TEMP path). HIT = >= 1 write on an armed survivor with
       RIP -> module RVA; no-write with clean exit = honest negative.

  Privacy: aggregate counts + module-RVA write sites only; raw survivor
  absolute addresses never enter the repo (local -AddressFile only).

.EXITCODES
  0  HIT (captured write(s)) or clean no-write with -AllowNoWrite
  1  Verdict negative (no writes) and -FailOnNoWrite
  2  Rendezvous / host missing, or staging precondition failed
  3  Gate not OfflineReplayVerified (never verified / lost)
  4  Roll failed (see roll exit; gate diagnosis printed)
  5  Interceptor missing / stale / attach failed / capture unparseable
  6  Unexpected error
#>
[CmdletBinding()]
param(
    # Target survivor set for the roll (the OD campaign's proven <=10).
    [int]$TargetSurvivors = 10,
    [int]$MaxRounds = 22,
    # Snapshot retained-byte budget passthrough (OD-035; 0 = engine ceiling).
    [long]$SnapshotMaxBytes = 0,
    [string]$AddressFile = '',
    # Capture window ceiling in seconds; budgeted against the battle tail.
    [int]$TraceSeconds = 60,
    # Result path for the verdict JSON (default .data\od-044-<stamp>.json).
    [string]$ResultPath = '',
    # FRESH43 dynamic source-arm: on first hit, arm the esi copy-source page
    # so the real game write site (one level above a CRT memcpy) can trap.
    # Use in a SECOND session after a first capture shows CRT-copy RIPs.
    [switch]$ArmSourceOnFirstHit,
    # Fail-closed on an honest no-write verdict (exit 1).
    [switch]$FailOnNoWrite,
    # Allow a clean no-write capture as a valid session result (exit 0).
    [switch]$AllowNoWrite,
    # Repo root override (tests / unusual layouts).
    [string]$RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

function Write-Session([string]$Message) {
    Write-Host ('od044: ' + $Message)
}

function Get-Rendezvous {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem $dir -File -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $file) { return $null }
        return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
    }
    catch { return $null }
}

function Get-GateState {
    param([object]$Rendezvous)
    try {
        return Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/state') -Headers @{
            'X-WotBTreader-Capability' = [string]$Rendezvous.capability
        }
    }
    catch { return $null }
}

function ConvertTo-HexToken([string]$Line) {
    $t = $Line.Trim()
    if ($t -match '^(0x)?([0-9a-fA-F]{4,16})$') {
        return ('0x' + $Matches[2])
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($AddressFile)) {
    $AddressFile = Join-Path $env:TEMP 'od-survivors.txt'
}
# Never stage a stale survivor set from a prior session (OD-RECOVERY-020).
Remove-Item -LiteralPath $AddressFile -Force -ErrorAction SilentlyContinue

# ---- 1. Gate wait ---------------------------------------------------------
Write-Session 'waiting_for_verified_gate'
$deadline = (Get-Date).AddSeconds(300)
$state = $null
while ((Get-Date) -lt $deadline) {
    $rv = Get-Rendezvous
    if ($rv) { $state = Get-GateState $rv }
    if ($state -and $state.verificationState -eq 'OfflineReplayVerified') {
        Write-Session 'gate=OfflineReplayVerified'
        break
    }
    $vs = if ($state) { [string]$state.verificationState } else { 'no-host' }
    Write-Session ('waiting gate=' + $vs)
    Start-Sleep -Seconds 5
}
if (-not $state -or $state.verificationState -ne 'OfflineReplayVerified') {
    Write-Session 'FAILED gate_never_verified'
    exit 3
}

# ---- 2. Roll --------------------------------------------------------------
$roll = Join-Path $RepoRoot 'scripts\roll-replay-time-increased.ps1'
if (-not (Test-Path -LiteralPath $roll)) {
    Write-Session ('FAILED roll_script_missing=' + $roll)
    exit 6
}
Write-Session 'rolling_start'
& $roll -TargetSurvivors $TargetSurvivors -MaxRounds $MaxRounds `
    -SnapshotMaxBytes $SnapshotMaxBytes -AddressFile $AddressFile
$rollExit = $LASTEXITCODE
Write-Session ('rolling_exit=' + $rollExit)
if ($rollExit -ne 0) {
    $rv = Get-Rendezvous
    $post = if ($rv) { Get-GateState $rv } else { $null }
    Write-Session ('post_roll_gate=' + $(if ($post) { $post.verificationState } else { 'no-host' }))
    exit 4
}

# ---- 3. Stage check -------------------------------------------------------
if (-not (Test-Path -LiteralPath $AddressFile)) {
    Write-Session 'FAILED address_file_missing (roll exited 0 without writing survivors?)'
    exit 4
}
$rawLines = @(Get-Content -LiteralPath $AddressFile -ErrorAction SilentlyContinue | Where-Object { $_ -and $_.Trim() })
$tokens = @($rawLines | ForEach-Object { ConvertTo-HexToken $_ } | Where-Object { $_ })
if ($tokens.Count -lt 2) {
    Write-Session ('FAILED too_few_survivors count=' + $tokens.Count + ' (need >= 2; tail is value-bound 11-17, TARGET ' + $TargetSurvivors + ' reached 3x historically)')
    exit 2
}
$clockTokens = @($tokens | Where-Object { $_ -match '^0x7FFE0[0-9A-Fa-f]{3}$' })
if ($clockTokens.Count -gt 0) {
    Write-Session ('WARN_kuser_clock_present count=' + $clockTokens.Count + ' dropping=' + ($clockTokens -join ','))
    $tokens = @($tokens | Where-Object { $_ -notmatch '^0x7FFE0[0-9A-Fa-f]{3}$' })
}
if ($tokens.Count -lt 2) {
    Write-Session 'FAILED all_survivors_were_kuser_clock (game field stopped ticking - dying game)'
    exit 2
}
Write-Session ('staged addresses=' + $tokens.Count + ' (raw lines ' + $rawLines.Count + ')')
if ($rawLines.Count -ne $tokens.Count) {
    Write-Session ('WARN_address_count_mismatch raw=' + $rawLines.Count + ' tokens=' + $tokens.Count)
}

# ---- 4. Arm + capture -----------------------------------------------------
$interceptorExe = Join-Path $RepoRoot '.build\publish\write-interceptor\WotBTreader.WriteInterceptor.exe'
if (-not (Test-Path -LiteralPath $interceptorExe)) {
    Write-Session ('FAILED_interceptor_exe_missing=' + (Split-Path -Leaf $interceptorExe))
    Write-Session 'BUILD_IT: dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor'
    exit 5
}

# Re-verify the gate before arming (FRESH30: never arm after battle end).
$rv = Get-Rendezvous
$pre = if ($rv) { Get-GateState $rv } else { $null }
if (-not $pre -or $pre.verificationState -ne 'OfflineReplayVerified') {
    Write-Session ('FAILED gate_lost_before_arm gate=' + $(if ($pre) { $pre.verificationState } else { 'no-host' }))
    exit 3
}

$game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
    Select-Object -First 1
if ($null -eq $game) {
    Write-Session 'FAILED_no_game_process (launch the replay first)'
    exit 5
}
Write-Session ('game_pid=' + $game.Id)

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $dataDir = Join-Path $RepoRoot '.data'
    if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
    $ResultPath = Join-Path $dataDir ('od-044-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}

# Budget the trace window against the battle tail (FRESH20: a fixed window
# straddling battle end is a dead window). Fallback to the requested ceiling
# when the battle end is unknown; the interceptor's own gate handling covers
# the host monitor revoking mid-window.
$captureJson = Join-Path $env:TEMP ('od-044-capture-' + [Guid]::NewGuid().ToString('N') + '.json')
$addressCsv = ($tokens -join ',')
Write-Session ('invoking_interceptor pid=' + $game.Id + ' seconds=' + $TraceSeconds + ' armed=' + $tokens.Count)
$interceptorArgs = @('--interceptor', '-Pid', ([string]$game.Id), '-Addresses', $addressCsv, '-Seconds', ([string]$TraceSeconds), '-Out', $captureJson)
if ($ArmSourceOnFirstHit) {
    $interceptorArgs += '-ArmSourceOnFirstHit'
    Write-Session 'source_arm ON (arm esi copy-source page at first hit)'
}
$interceptorExit = -1
try {
    & $interceptorExe @interceptorArgs
    $interceptorExit = $LASTEXITCODE
}
catch {
    Write-Session ('interceptor_THREW ' + $_.Exception.Message)
    exit 6
}
Write-Session ('interceptor_exit=' + $interceptorExit)

# Durable capture next to ResultPath (FRESH36 lesson).
$durableCapturePath = $ResultPath + '.capture.json'
if (Test-Path -LiteralPath $captureJson) {
    try {
        Copy-Item -LiteralPath $captureJson -Destination $durableCapturePath -Force
        Write-Session ('durable_capture=' + (Split-Path -Leaf $durableCapturePath))
    }
    catch {
        Write-Session ('WARN_durable_capture_copy_failed ' + $_.Exception.Message)
    }
}

if ($interceptorExit -ne 0) {
    Write-Session 'FAILED interceptor (no verdict consumed)'
    exit 5
}

# ---- 5. Verdict -----------------------------------------------------------
$capture = $null
try {
    $capture = Get-Content -LiteralPath $captureJson -Raw | ConvertFrom-Json
}
catch {
    Write-Session ('FAILED_capture_parse ' + $_.Exception.Message)
    exit 5
}
$hits = @()
if ($null -ne $capture -and $capture.PSObject.Properties['hits'] -and $null -ne $capture.hits) {
    $hits = @($capture.hits)
}

# Write-site RIP -> module RVA via the attach-time module list.
$modules = @()
if ($null -ne $capture -and $capture.PSObject.Properties['modules'] -and $null -ne $capture.modules) {
    $modules = @($capture.modules)
}
$writeSites = @()
foreach ($h in $hits) {
    $addr = ConvertTo-HexToken ([string]$h.address)
    $rip = ConvertTo-HexToken ([string]$h.rip)
    # Resolve the RIP to the module that CONTAINS it: the module with the
    # HIGHEST base address <= RIP (a first-match loop mis-attributes a CRT
    # write to wotblitz.exe because its low base also satisfies <= RIP).
    $moduleName = ''
    $rva = ''
    $bestBase = [uint64]0
    foreach ($mod in $modules) {
        $modBase = if ($mod.PSObject.Properties['baseAddress']) { [string]$mod.baseAddress } else { '' }
        if (-not $modBase -or -not $rip) { continue }
        try {
            $ripValue = [Convert]::ToUInt64($rip.TrimStart('0x'), 16)
            $baseValue = [Convert]::ToUInt64($modBase.TrimStart('0x'), 16)
            if ($ripValue -ge $baseValue -and $baseValue -gt $bestBase) {
                $bestBase = $baseValue
                $moduleName = if ($mod.PSObject.Properties['name']) { [string]$mod.name } else { '' }
                $rva = ('0x' + ($ripValue - $baseValue).ToString('X'))
            }
        }
        catch { }
    }
    if ($addr -and $rip) {
        $writeSites += [ordered]@{
            address = $addr
            rip     = $rip
            module  = $moduleName
            rva     = $rva
        }
    }
}

$hit = $writeSites.Count -gt 0
$summary = [ordered]@{
    session             = 'od-044-replaytime'
    timestampUtc        = (Get-Date).ToUniversalTime().ToString('o')
    targetSurvivors     = $TargetSurvivors
    stagedCount         = $tokens.Count
    interceptorExit     = $interceptorExit
    hits                = $hits.Count
    writeSites          = $writeSites
    verdict             = if ($hit) { 'HIT' } else { 'no-write' }
    publicProcessAddressesOrRawBytes = $false
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding ascii
Write-Session ('VERDICT ' + $summary.verdict + ' hits=' + $hits.Count + ' write_sites=' + $writeSites.Count)
foreach ($site in $writeSites) {
    Write-Host ('od044:   write_site address=' + $site.address + ' rip=' + $site.rip + ' module=' + $site.module + ' rva=' + $site.rva)
}

if ($hit) {
    exit 0
}
if ($FailOnNoWrite) {
    Write-Session 'VERDICT no-write (honest negative) - failing per -FailOnNoWrite'
    exit 1
}
if ($AllowNoWrite) {
    exit 0
}
Write-Session 'VERDICT no-write (honest negative) - inspect the durable capture; -AllowNoWrite to accept'
exit 1
