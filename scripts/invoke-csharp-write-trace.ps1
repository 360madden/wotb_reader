#Requires -Version 5.1
<#
.SYNOPSIS
  C# guard-page write-trace driver - the M2 successor to
  x64dbg-write-trace.ps1 (FRESH32/33 proved the x64dbg write-BP route is
  dead in this environment). Reads the SAME od-048 family report, selects
  the same best family with the same floors, then drives the x86
  WriteInterceptor helper (tools/WriteInterceptor) instead of x64dbg:
  arm PAGE_GUARD on the pages holding the armed member addresses, attach as
  the process's only debugger, capture every write (RIP, value, i386
  registers, module RVA) while the game KEEPS RUNNING (no breakin, no
  freeze - the WOW64 attach-freeze class is gone by construction), and
  write the same odwt-* evidence shapes the campaign tooling already greps.

.DESCRIPTION
  Contract parity with x64dbg-write-trace.ps1 family mode:
    - -FamilyFile: the od-048 M1 report (families array + members with
      address/offsetBytes/axis/score/shiftBand/span/edgeAligned).
    - -TraceSeconds: how long to keep the capture window open.
    - -ResultPath: where the hits text lands (od-048 overwrites it with its
      own summary JSON afterwards, exactly as with the x64dbg path); the
      durable evidence is ResultPath + '.family.json' in the SAME shape the
      x64dbg family report uses, so launch scripts that grep the family
      report keep working.
    - -MinMemberScore / -MaxMemberBandSeconds / -MinMemberSpan: the same
      floors od-048 and the x64dbg trace apply; this driver re-vets with
      them so the two gates can never disagree.

  Family selection mirrors x64dbg's Select-BestFamily exactly (complete
  family first, then usable, then any scored; rank by distinct axis count
  desc then mean member score desc) and the arm plan caps at 4 members
  (DR0-DR3 parity - the interceptor arms one PAGE_GUARD page per unique
  page, which is not limited to 4, but the family contract keeps the same
  top-4 member selection for report parity).

  The x86 helper: DebugActiveProcess requires same-bitness with the 32-bit
  game, so the helper is a self-contained x86 publish at
  .build/publish/write-interceptor/WotBTreader.WriteInterceptor.exe. Build
  it with:
    dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor

.EXITCODES
  0  Trace window completed cleanly (zero hits is a valid no-write verdict,
     not a failure - the window liveness fields in the family report
     discriminate a real no-write from a broken attach).
  2  Refused: no usable family / no armed members / unparseable family file.
  6  Unexpected: interceptor exe missing, no game process, interceptor
     child failed to attach/arm.
  7  Replay is paused (SPACE and rerun) - window not spent.
#>
[CmdletBinding()]
param(
    # od-048 M1 report containing the families array.
    [string]$FamilyFile,
    # Accepted for hashtable-splat parity with the x64dbg driver contract:
    # od-048's $wtArgs always carries AutoWriteTrace=$true (it is the driver
    # invocation, not the standalone operator mode). This wrapper has no
    # standalone mode, so the switch is intentionally unused - but a splat
    # containing it must bind, or the live invocation throws (the exact
    # misbinding class this campaign has burned sessions on).
    [switch]$AutoWriteTrace,
    # How long to keep the capture window open (seconds).
    [int]$TraceSeconds = 25,
    # Where the hits text + '<path>.family.json' evidence land. Standalone
    # default mirrors x64dbg-write-trace.ps1 (the od-048 wired path always
    # passes a real path).
    [string]$ResultPath = $(Join-Path $env:TEMP 'od-wt-hits.txt'),
    # Minimum correlation score for EVERY selected family member (0 disables).
    [double]$MinMemberScore = 0.9,
    # Maximum ambiguity-band width for every member (0 disables).
    [double]$MaxMemberBandSeconds = 60.0,
    # Minimum observed movement span for every member (0 disables).
    [double]$MinMemberSpan = 10.0,
    # Repo root for locating the interceptor publish (auto-detected).
    [string]$RepoRoot = '',
    # Target process id override for OFFLINE VALIDATION ONLY: when non-zero,
    # the interceptor attaches to this pid instead of auto-discovering the
    # wotblitz game process. Used by the synthetic-counter harness
    # (tmpwotb-e2e) to prove the family->capture pipeline without a live
    # game; the live round never passes it.
    [int]$TargetPid = 0,
    # FRESH38+ source-arm: when set, the interceptor arms the page holding the
    # esi copy-source pointer captured at hit time, so the game's own fill
    # write site (one level above a VCRUNTIME memcpy) can trap in the same
    # window. Requires a running game (the synthetic counter also supports it).
    [switch]$ArmSourceOnFirstHit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

function Write-CsWt([string]$Message) {
    Write-Host ("cswt: " + $Message)
}

function ConvertTo-HexToken([string]$Line) {
    $t = $Line.Trim()
    if ($t -match '^(0x)?([0-9a-fA-F]{4,16})$') {
        return ('0x' + $Matches[2])
    }
    return $null
}

function Get-MemberBandWidth {
    param([object]$Member)
    $minB = $null
    $maxB = $null
    if ($Member.PSObject.Properties['shiftBandMinSeconds'] -and $null -ne $Member.shiftBandMinSeconds) {
        $minB = [double]$Member.shiftBandMinSeconds
    }
    elseif ($Member.PSObject.Properties['shiftMinSeconds'] -and $null -ne $Member.shiftMinSeconds) {
        $minB = [double]$Member.shiftMinSeconds
    }
    if ($Member.PSObject.Properties['shiftBandMaxSeconds'] -and $null -ne $Member.shiftBandMaxSeconds) {
        $maxB = [double]$Member.shiftBandMaxSeconds
    }
    elseif ($Member.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $Member.shiftMaxSeconds) {
        $maxB = [double]$Member.shiftMaxSeconds
    }
    if ($null -eq $minB -or $null -eq $maxB) { return $null }
    return [double]($maxB - $minB)
}

# Mirrors x64dbg-write-trace.ps1 Test-FamilyScored: every member must clear
# the score floor (a below-floor member is noise that burns the window).
function Test-FamilyScored {
    param([object]$Family)
    if (-not $Family.PSObject.Properties['members'] -or $null -eq $Family.members) {
        return $false
    }
    foreach ($m in @($Family.members)) {
        $score = 0.0
        if ($m.PSObject.Properties['score'] -and $null -ne $m.score) { $score = [double]$m.score }
        if ($score -lt $MinMemberScore) { return $false }
    }
    return $true
}

# Mirrors x64dbg Test-FamilyBanded: every member's band known + within the
# floor, and the span floor applied when the member carries a span.
function Test-FamilyBanded {
    param([object]$Family)
    if ($MaxMemberBandSeconds -le 0) { return $true }
    if (-not $Family.PSObject.Properties['members'] -or $null -eq $Family.members) {
        return $false
    }
    foreach ($m in @($Family.members)) {
        $width = Get-MemberBandWidth -Member $m
        if ($null -eq $width -or $width -gt $MaxMemberBandSeconds) { return $false }
        if ($MinMemberSpan -gt 0 -and $m.PSObject.Properties['span'] -and $null -ne $m.span) {
            if ([double]$m.span -lt $MinMemberSpan) { return $false }
        }
    }
    return $true
}

# Mirrors x64dbg Test-UsableFamily: scored + banded + >=1 member + at least
# one non-edge-aligned member (an all-edge family is a bad-anchor decoy).
function Test-UsableFamily {
    param([object]$Family)
    if (-not (Test-FamilyScored -Family $Family)) { return $false }
    if (-not (Test-FamilyBanded -Family $Family)) { return $false }
    $members = @($Family.members)
    if ($members.Count -lt 1) { return $false }
    foreach ($m in $members) {
        if (-not $m.PSObject.Properties['edgeAligned'] -or -not $m.edgeAligned) {
            return $true
        }
    }
    return $false
}

function Get-FamilyAxisCount {
    param([object]$Family)
    $axes = @{}
    foreach ($m in @($Family.members)) {
        if ($m.PSObject.Properties['axis'] -and $m.axis) { $axes[$m.axis] = $true }
    }
    return $axes.Count
}

function Get-FamilyMeanScore {
    param([object]$Family)
    $count = 0
    $total = 0.0
    foreach ($m in @($Family.members)) {
        if ($m.PSObject.Properties['score'] -and $null -ne $m.score) {
            $total += [double]$m.score
            $count++
        }
    }
    if ($count -eq 0) { return 0.0 }
    return ($total / $count)
}

function Select-HighestRankedFamily {
    param([object[]]$Families)
    $best = $null
    $bestAxes = -1
    $bestMean = [double]::MinValue
    foreach ($f in $Families) {
        $axes = Get-FamilyAxisCount -Family $f
        $mean = Get-FamilyMeanScore -Family $f
        if ($axes -gt $bestAxes -or ($axes -eq $bestAxes -and $mean -gt $bestMean)) {
            $bestAxes = $axes
            $bestMean = $mean
            $best = $f
        }
    }
    return $best
}

# Mirrors x64dbg Select-BestFamily: complete first, then usable, then any
# scored; deterministic so the same report selects the same family.
function Select-BestFamily {
    param([object[]]$Families)
    $scored = @($Families | Where-Object { (Test-FamilyScored -Family $_) -and (Test-FamilyBanded -Family $_) })
    $complete = @($scored | Where-Object { $_.PSObject.Properties['complete'] -and $_.complete })
    if ($complete.Count -gt 0) { return (Select-HighestRankedFamily -Families $complete) }
    $usable = @($scored | Where-Object { Test-UsableFamily -Family $_ })
    if ($usable.Count -gt 0) { return (Select-HighestRankedFamily -Families $usable) }
    if ($scored.Count -gt 0) { return (Select-HighestRankedFamily -Families $scored) }
    return $null
}

# Mirrors x64dbg Get-FamilyArmPlan: member addresses ordered by
# base-relative offset, deduped, first <=4 armed (DR0-DR3 parity), the rest
# reported unarmed.
function Get-FamilyArmPlan {
    param([object]$Family)
    $armed = @()
    $unarmed = @()
    if (-not $Family.PSObject.Properties['members'] -or $null -eq $Family.members) {
        return @{ Armed = $armed; Unarmed = $unarmed }
    }
    $members = @($Family.members)
    $ordered = @($members | Sort-Object -Property @{
        Expression = { if ($_.PSObject.Properties['offsetBytes'] -and $null -ne $_.offsetBytes) { [int]$_.offsetBytes } else { 0 } }
        Ascending  = $true
    })
    $seen = @{}
    foreach ($m in $ordered) {
        if (-not $m.PSObject.Properties['address']) { continue }
        $addr = ConvertTo-HexToken ([string]$m.address)
        if (-not $addr) { continue }
        if ($seen.ContainsKey($addr.ToLowerInvariant())) { continue }
        $seen[$addr.ToLowerInvariant()] = $true
        if ($armed.Count -lt 4) { $armed += $addr }
        else { $unarmed += $addr }
    }
    return @{ Armed = $armed; Unarmed = $unarmed }
}

# Probe the replay play-state via the same HUD icon probe the x64dbg trace
# uses. A paused replay burns the window with zero writes; refuse early.
function Get-ReplayPlayState {
    $probe = Join-Path $PSScriptRoot 'replay-play-state.ps1'
    if (-not (Test-Path -LiteralPath $probe)) { return 'unknown' }
    try {
        $line = (& $probe | Select-Object -First 1)
        if ($null -eq $line -or $line -notmatch '^replay_state=(paused|playing|unknown)$') {
            return 'unknown'
        }
        return $Matches[1]
    }
    catch {
        return 'unknown'
    }
}

# ---------------------------------------------------------------------------
# 1. Load + select the family (same contract as the x64dbg path).
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($FamilyFile) -or -not (Test-Path -LiteralPath $FamilyFile)) {
    Write-CsWt ("FAILED_family_file_missing=" + $FamilyFile)
    exit 2
}

$familyDoc = $null
try {
    $familyDoc = Get-Content -LiteralPath $FamilyFile -Raw | ConvertFrom-Json
}
catch {
    Write-CsWt ('FAILED_family_file_unparseable ' + $_.Exception.Message)
    exit 2
}
$families = @()
if ($null -ne $familyDoc -and $familyDoc.PSObject.Properties['families'] -and $null -ne $familyDoc.families) {
    $families = @($familyDoc.families)
}
if ($families.Count -eq 0) {
    Write-CsWt 'FAILED_no_families_in_report'
    exit 2
}

$family = Select-BestFamily -Families $families
if ($null -eq $family) {
    Write-CsWt ('FAILED_no_usable_family score_floor=' + $MinMemberScore + ' band_floor=' + $MaxMemberBandSeconds + 's span_floor=' + $MinMemberSpan)
    exit 2
}
$armPlan = Get-FamilyArmPlan -Family $family
$armed = @($armPlan.Armed)
$unarmed = @($armPlan.Unarmed)
if ($armed.Count -eq 0) {
    Write-CsWt 'FAILED_no_armed_members'
    exit 2
}
Write-CsWt ('family_selected armed=' + $armed.Count + ' unarmed=' + $unarmed.Count + ' complete=' + [bool]($family.PSObject.Properties['complete'] -and $family.complete))

# ---------------------------------------------------------------------------
# 2. Play-state gate (paused replay = zero-write window; refuse early).
# ---------------------------------------------------------------------------
$playState = Get-ReplayPlayState
if ($playState -eq 'paused') {
    Write-CsWt 'FAILED_replay_paused (press SPACE and rerun) - window not spent'
    exit 7
}

# ---------------------------------------------------------------------------
# 3. Locate the x86 helper + the game process.
# ---------------------------------------------------------------------------
$interceptorExe = Join-Path $RepoRoot '.build\publish\write-interceptor\WotBTreader.WriteInterceptor.exe'
if (-not (Test-Path -LiteralPath $interceptorExe)) {
    Write-CsWt ('FAILED_interceptor_exe_missing=' + $interceptorExe)
    Write-CsWt 'BUILD_IT: dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor'
    exit 6
}

# TargetPid is an offline-validation escape hatch (the synthetic counter);
# the live round auto-discovers the wotblitz game process. Guarded: a
# non-zero TargetPid is used as-is, else the wotblitz lookup runs.
if ($TargetPid -gt 0) {
    Write-CsWt ('target_pid_override=' + $TargetPid + ' (offline validation only)')
}
else {
    $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if ($null -eq $game) {
        Write-CsWt 'FAILED_no_game_process (launch the replay first)'
        exit 6
    }
    $TargetPid = $game.Id
}
Write-CsWt ('game_pid=' + $TargetPid)

# ---------------------------------------------------------------------------
# 4. Run the interceptor: arm the pages, attach, capture every write while
#    the game keeps running, then report.
# ---------------------------------------------------------------------------
$captureJson = Join-Path $env:TEMP ('od-wt-capture-' + [Guid]::NewGuid().ToString('N') + '.json')
$addressCsv = ($armed -join ',')
Write-CsWt ('invoking_interceptor pid=' + $TargetPid + ' seconds=' + $TraceSeconds + ' armed=' + $armed.Count)
$interceptorExit = -1
try {
    $interceptorArgs = @('--interceptor', '-Pid', ([string]$TargetPid), '-Addresses', $addressCsv, '-Seconds', ([string]$TraceSeconds), '-Out', $captureJson)
if ($ArmSourceOnFirstHit) {
    $interceptorArgs += '-ArmSourceOnFirstHit'
    Write-CsWt 'source_arm ON (arm esi copy-source page at first hit)'
}
& $interceptorExe @interceptorArgs
    $interceptorExit = $LASTEXITCODE
}
catch {
    Write-CsWt ('interceptor_THREW ' + $_.Exception.Message)
    exit 6
}
Write-CsWt ('interceptor_exit=' + $interceptorExit)

# Durable capture next to ResultPath: FRESH36 lost modules/rva/registers
# because the TEMP GUID path was ephemeral and never copied. Always promote
# the raw interceptor report when present (success or fail-closed exit).
$durableCapturePath = $ResultPath + '.capture.json'
if (Test-Path -LiteralPath $captureJson) {
    try {
        Copy-Item -LiteralPath $captureJson -Destination $durableCapturePath -Force
        Write-CsWt ('durable_capture=' + $durableCapturePath)
    }
    catch {
        Write-CsWt ('WARN_durable_capture_copy_failed ' + $_.Exception.Message)
    }
}

if ($interceptorExit -ne 0) {
    # 3 = no pages armed (unreadable/committed-check failed), 4 = attach
    # failed (already has a debugger? the x64dbg smoke must not have left
    # one attached), 2 = usage, 5 = unexpected.
    if (Test-Path -LiteralPath $captureJson) {
        $cap = Get-Content -LiteralPath $captureJson -Raw
        if ($cap) { Write-CsWt ('interceptor_report=' + ($cap -replace '\s+', ' ')) }
    }
    exit 6
}

# ---------------------------------------------------------------------------
# 5. Parse the capture report and merge into the odwt-* shapes.
# ---------------------------------------------------------------------------
$capture = $null
try {
    $capture = Get-Content -LiteralPath $captureJson -Raw | ConvertFrom-Json
}
catch {
    # StrictMode+Stop: a missing/empty/truncated capture file after a clean
    # interceptor exit would otherwise crash with a raw error, not the
    # exit-code contract. Fail closed with the diagnostic instead.
    Write-CsWt ('FAILED_capture_parse ' + $_.Exception.Message)
    exit 6
}
$hits = @()
if ($null -ne $capture -and $capture.PSObject.Properties['hits'] -and $null -ne $capture.hits) {
    $hits = @($capture.hits)
}

# Module list from the interceptor attach-time snapshot (basename only).
$modulesOut = @()
if ($null -ne $capture -and $capture.PSObject.Properties['modules'] -and $null -ne $capture.modules) {
    foreach ($mod in @($capture.modules)) {
        $modName = if ($mod.PSObject.Properties['name']) { [string]$mod.name } else { '' }
        $modBase = if ($mod.PSObject.Properties['baseAddress']) { [string]$mod.baseAddress } else { '' }
        $modSize = if ($mod.PSObject.Properties['size'] -and $null -ne $mod.size) { [uint32]$mod.size } else { [uint32]0 }
        $modPath = if ($mod.PSObject.Properties['pathBasename']) { [string]$mod.pathBasename } else { '' }
        $modulesOut += [ordered]@{
            name         = $modName
            baseAddress  = $modBase
            size         = $modSize
            pathBasename = $modPath
        }
    }
}

# Hits text lines in the exact odwt-* shape the campaign greps:
# '0x<addr> 0x<rip>' per captured write.
$hitLines = @()
foreach ($h in $hits) {
    $addrToken = ConvertTo-HexToken ([string]$h.address)
    $ripToken = ConvertTo-HexToken ([string]$h.rip)
    if ($addrToken -and $ripToken) {
        $hitLines += ($addrToken + ' ' + $ripToken)
    }
}
# Always write the hits channel (even when empty - an empty file is valid
# no-write evidence, and greps must never race on file existence). Mirrors
# the x64dbg path's unconditional Set-Content.
Set-Content -LiteralPath $ResultPath -Value $hitLines -Encoding ascii
Write-CsWt ('hits=' + $hits.Count)

# Family report in the x64dbg family-report shape (ResultPath + '.family.json'):
# per-member hits/rips, liveness fields, verdict. The game never paused
# during the window (the interceptor never breakins), so liveness is
# 'running' by construction; values_changed = any captured write.
$memberEntries = @()
foreach ($m in @($family.members)) {
    if (-not $m.PSObject.Properties['address']) { continue }
    $addr = ConvertTo-HexToken ([string]$m.address)
    if (-not $addr) { continue }
    $addrKey = $addr.ToLowerInvariant()
    $rips = @()
    $rvas = @()
    foreach ($h in $hits) {
        $hAddr = ConvertTo-HexToken ([string]$h.address)
        if ($hAddr -and $hAddr.ToLowerInvariant() -eq $addrKey) {
            $hRip = ConvertTo-HexToken ([string]$h.rip)
            if ($hRip -and ($rips -notcontains $hRip)) { $rips += $hRip }
            $hRva = $null
            if ($h.PSObject.Properties['rva'] -and $null -ne $h.rva -and [string]$h.rva) {
                $hRva = [string]$h.rva
            }
            if ($hRva -and ($rvas -notcontains $hRva)) { $rvas += $hRva }
        }
    }
    $bandMin = $null
    $bandMax = $null
    if ($m.PSObject.Properties['shiftBandMinSeconds'] -and $null -ne $m.shiftBandMinSeconds) {
        $bandMin = [double]$m.shiftBandMinSeconds
    }
    elseif ($m.PSObject.Properties['shiftMinSeconds'] -and $null -ne $m.shiftMinSeconds) {
        $bandMin = [double]$m.shiftMinSeconds
    }
    if ($m.PSObject.Properties['shiftBandMaxSeconds'] -and $null -ne $m.shiftBandMaxSeconds) {
        $bandMax = [double]$m.shiftBandMaxSeconds
    }
    elseif ($m.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $m.shiftMaxSeconds) {
        $bandMax = [double]$m.shiftMaxSeconds
    }
    $memberEntries += [ordered]@{
        address             = $addr
        offsetBytes         = if ($m.PSObject.Properties['offsetBytes'] -and $null -ne $m.offsetBytes) { [int]$m.offsetBytes } else { 0 }
        axis                = if ($m.PSObject.Properties['axis']) { [string]$m.axis } else { '?' }
        score               = if ($m.PSObject.Properties['score'] -and $null -ne $m.score) { [double]$m.score } else { 0.0 }
        shiftBandMinSeconds = $bandMin
        shiftBandMaxSeconds = $bandMax
        hits                = $rips.Count
        rips                = $rips
        rvas                = $rvas
    }
}
$hitMembers = @($memberEntries | Where-Object { $_.hits -gt 0 })
$familyVerdict = if ($hitMembers.Count -gt 0) { 'family-hit' } else { 'family-no-hit' }

# Aggregate unique write sites from the capture (durable M2 tail evidence).
$writeSitesByRip = @{}
foreach ($h in $hits) {
    $hRip = ConvertTo-HexToken ([string]$h.rip)
    if (-not $hRip) { continue }
    $ripKey = $hRip.ToLowerInvariant()
    if (-not $writeSitesByRip.ContainsKey($ripKey)) {
        $hRva = 'jit'
        if ($h.PSObject.Properties['rva'] -and $null -ne $h.rva -and [string]$h.rva) {
            $hRva = [string]$h.rva
        }
        $hInstr = $null
        if ($h.PSObject.Properties['instructionHex'] -and $null -ne $h.instructionHex) {
            $hInstr = [string]$h.instructionHex
        }
        $hRegs = $null
        if ($h.PSObject.Properties['registers'] -and $null -ne $h.registers) {
            $hRegs = $h.registers
        }
        $hKind = 'member'
        if ($h.PSObject.Properties['kind'] -and $null -ne $h.kind) {
            $hKind = [string]$h.kind
        }
        $writeSitesByRip[$ripKey] = [ordered]@{
            rip             = $hRip
            rva             = $hRva
            instructionHex  = $hInstr
            hitCount        = 0
            memberAddresses = @()
            registersSample = $hRegs
            kind            = $hKind
        }
    }
    $site = $writeSitesByRip[$ripKey]
    $site.hitCount = [int]$site.hitCount + 1
    $hAddr = ConvertTo-HexToken ([string]$h.address)
    if ($hAddr -and ($site.memberAddresses -notcontains $hAddr)) {
        $site.memberAddresses = @($site.memberAddresses) + @($hAddr)
    }
    if ($null -eq $site.registersSample -and $h.PSObject.Properties['registers'] -and $null -ne $h.registers) {
        $site.registersSample = $h.registers
    }
    if (-not $site.instructionHex -and $h.PSObject.Properties['instructionHex'] -and $null -ne $h.instructionHex) {
        $site.instructionHex = [string]$h.instructionHex
    }
}
$writeSitesOut = @($writeSitesByRip.Values | Sort-Object { $_.rip })

# The interceptor never pauses the game, so 'running' is by construction;
# expose the raw guard/arm counters for a zero-hit diagnosis.
$familyReport = [ordered]@{
    mode               = 'family'
    complete           = [bool]($family.PSObject.Properties['complete'] -and $family.complete)
    axesCovered        = @(if ($family.PSObject.Properties['axesCovered']) { @($family.axesCovered) } else { @() })
    writeSize          = 4
    armedCount         = $armed.Count
    unarmedCount       = $unarmed.Count
    hitsTotal          = $hits.Count
    hitMembers         = $hitMembers.Count
    windowLiveness     = 'running'
    windowCpuDeltaMs   = $null
    windowValuesChanged = if ($hits.Count -gt 0) { 'true' } else { 'false' }
    windowMaxValueDelta = $null
    interceptorPagesArmed = if ($capture.PSObject.Properties['pagesArmed'] -and $null -ne $capture.pagesArmed) { [int]$capture.pagesArmed } else { 0 }
    interceptorGuardEvents = if ($capture.PSObject.Properties['guardEvents'] -and $null -ne $capture.guardEvents) { [int]$capture.guardEvents } else { 0 }
    interceptorArmedPageEvents = if ($capture.PSObject.Properties['armedPageEvents'] -and $null -ne $capture.armedPageEvents) { [int]$capture.armedPageEvents } else { 0 }
    interceptorForeignGuardEvents = if ($capture.PSObject.Properties['foreignGuardEvents'] -and $null -ne $capture.foreignGuardEvents) { [int]$capture.foreignGuardEvents } else { 0 }
    interceptorSourcePagesArmed = if ($capture.PSObject.Properties['sourcePagesArmed'] -and $null -ne $capture.sourcePagesArmed) { [int]$capture.sourcePagesArmed } else { 0 }
    sourceHits         = @($hits | Where-Object { $_.PSObject.Properties['kind'] -and [string]$_.kind -eq 'source' }).Count
    verdict            = $familyVerdict
    # Genuine read of the splat-parity switch: records the invocation mode.
    # od-048 always passes AutoWriteTrace=$true (driver mode); a standalone
    # operator invocation omits it. The value is evidence, not dead state -
    # it also keeps PSReviewUnusedParameter satisfied without a suppression.
    invocationMode     = if ($AutoWriteTrace) { 'auto-write-trace' } else { 'operator' }
    capturePath        = $durableCapturePath
    modules            = $modulesOut
    writeSites         = $writeSitesOut
    members            = $memberEntries
}

$familyResultPath = $ResultPath + '.family.json'
$familyJson = $familyReport | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($familyResultPath, $familyJson, (New-Object System.Text.UTF8Encoding($false)))
Write-CsWt ('family_verdict=' + $familyVerdict + ' hit_members=' + $hitMembers.Count + ' liveness=' + $familyReport.windowLiveness + ' values_changed=' + $familyReport.windowValuesChanged)
Write-CsWt ('family_report=' + $familyResultPath)
Write-CsWt ('write_sites=' + $writeSitesOut.Count + ' modules=' + $modulesOut.Count)

Write-CsWt 'OK trace_window_completed'
exit 0
