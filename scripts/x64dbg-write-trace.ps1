#Requires -Version 5.1
<#
.SYNOPSIS
  Automated x64dbg hardware write-breakpoint write-trace (operator step
  optional). Supports the M2 surviving-family input and the -AutoWriteTrace
  driver mode.

.DESCRIPTION
  Replaces the operator's interactive Find-what-writes step inside the held
  green window (OD-044 Track C2 pilot). Two input modes:

  Survivor mode (legacy): reads the rolling driver's staged survivors
    (%TEMP%\od-survivors.txt, one absolute hex address per line, 0x prefix,
    8-byte aligned Double addresses) and arms 8-byte write breakpoints.

  Family mode (M2): reads the od-048 correlate report JSON (-FamilyFile,
    its `families` array) or a bare family JSON, selects the best family
    (a complete x/y/z triple wins; else the highest summed member score),
    and arms 4-byte write breakpoints on the member addresses (Float32
    coordinates at 4-byte offsets). One trace window maps the write sites
    of all three coordinate components.

  The generated x64dbg script arms hardware WRITE breakpoints (bph
  <addr>,w,<4|8>) on up to 4 addresses (the DR0-DR3 x64 limit), sets a
  bphwlog line that string-formats {rip} on hit, sets a breakpoint command
  that runs savedata - and savedata string-formats its own filename arg
  (stringformatinline in cmd-memory-operations.cpp), so each hit writes
  <HitsDir>\odwt-0x<addr>-<rip>.bin containing 64 bytes of memory at RIP
  (the writing instruction bytes). The hit filename is the automatable
  evidence channel - no GUI scraping required. Fast-resume keeps the
  replay playing so all hits in the window are captured.

  Script execution is injected via `scriptload "<file>"` then `scriptrun`
  into the RUNNING x64dbg command bar via GUI automation (activate window,
  click the command bar, SendKeys). x64dbg's CLI only supports `-p <pid>`
  attach - there is no script-execution flag (verified against
  help.x64dbg.com Commandline docs), so command-bar injection is the only
  no-new-install path.

  The driver polls the hits dir + the Host gate for -TraceSeconds; stops
  early on gate loss. Writes captured addr -> rip evidence to -ResultPath
  (local, untracked) and, in family mode, a per-member hit report to
  <ResultPath>.family.json. Prints aggregate counts only on stdout (privacy
  rule: addresses never enter the repo or stdout; they go to the local
  file).

  -AutoWriteTrace is the roadmap M2 driver invocation: pre-arms the
  debugger when missing (pre-arm-debugger.ps1 -AutoAttach), gate-prechecks,
  probes the replay play-state (must be `playing` - a paused replay writes
  nothing), re-reads the family addresses for liveness (a fresh replay
  launch reallocates structures), then runs the trace window. Use -DryRun
  to generate the script + arm plan without touching the debugger.

  Cap: only 4 hardware breakpoints exist on x64 (DR0-DR3). If the input has
  more than 4 addresses, the first 4 are armed and the rest are reported as
  unarmed (matches the CE-era "armed 4, skipped beyond limit" sessions).

  Requires the pre-armed x64dbg (scripts/pre-arm-debugger.ps1 -AutoAttach)
  or a running x64dbg attached to wotblitz.exe. Use -DryRun to generate the
  script + evidence plan without touching the debugger.

.EXITCODES
  0  Success - trace window completed (hits captured, or clean no-hit window, gate held)
  2  Input missing / empty / unparseable (survivor file or family JSON)
  3  x64dbg not found / not running / auto-pre-arm failed
  4  Command-bar injection failed (window/click/send)
  5  Gate not verified or lost during the trace window
  6  Unexpected error
  7  Replay play-state confirmed `paused` and never reached `playing` in time (a paused replay writes nothing)
  8  Family liveness re-read failed - the family addresses are stale for the current process
#>
# X64DbgExe is read by Find-X64DbgExe (a child function) via script-scope
# dynamic lookup; PSSA's PSReviewUnusedParameter cannot see cross-function
# script-parameter use and would report it as dead. NOTE: the suppression is
# file-scoped -- a genuinely dead parameter added to this script later will
# also go un-flagged; review new parameters manually.
[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'X64DbgExe is consumed by Find-X64DbgExe via script-scope lookup.')]
[CmdletBinding()]
param(
    # Rolling driver's staged survivor file (one absolute hex address per line).
    [string]$SurvivorFile = $(Join-Path $env:TEMP 'od-survivors.txt'),
    # How long to keep the write-trace window open (capped by the held green window).
    [int]$TraceSeconds = 120,
    [int]$PollIntervalSeconds = 2,
    # Directory receiving the savedata evidence files (created if missing).
    [string]$HitsDir = $(Join-Path $env:TEMP 'od-wt-hits'),
    # Generated x64dbg script file (loaded via scriptload).
    [string]$ScriptFile = $(Join-Path $env:TEMP 'od-wt-x64dbg.script'),
    # Local (untracked) file receiving captured addr -> rip evidence lines.
    [string]$ResultPath = $(Join-Path $env:TEMP 'od-wt-hits.txt'),
    # Optional explicit x64dbg exe; when empty, resolved from the pre-arm
    # marker or the running x64dbg process.
    [string]$X64DbgExe = '',
    # Generate the script + print the plan, but do not touch x64dbg.
    [switch]$DryRun,
    # Inject a final `run` after scriptrun (resumes the debuggee if the
    # pre-arm attach paused it; harmless if already running).
    [switch]$NoResume,
    # Do not poll the Host gate (playback-only); time-boxed by -TraceSeconds.
    [switch]$SkipGateCheck,
    # M2 family input: path to an od-048 correlate report JSON (its
    # `families` array) OR a bare family JSON object with `members`. When
    # set, the script arms the surviving family's member addresses
    # (Float32, w,4) instead of the flat Double survivor file. A bare
    # top-level JSON ARRAY of families is not accepted - wrap it in
    # { "families": [...] } or pass a single family object.
    [string]$FamilyFile = '',
    # Write-breakpoint width in bytes (4 or 8). 0 = auto: 4 for family
    # input (Float32 members), 8 for the flat survivor file (Double).
    [int]$WriteSize = 0,
    # M2 driver mode (the roadmap's write-trace invocation): pre-arm the
    # debugger when missing, gate-precheck, probe the replay play-state
    # (must be playing), re-read the family addresses for liveness, then
    # run the trace window to completion.
    [switch]$AutoWriteTrace,
    # Pre-arm via pre-arm-debugger.ps1 -AutoAttach when no x64dbg/x32dbg
    # process is running yet (implied by -AutoWriteTrace).
    [switch]$AutoPreArm,
    # Do not probe the replay HUD icon for the play state (HUD hidden /
    # headless validation). Without it, the driver requires the icon to
    # read `playing` before arming: a paused replay produces no position
    # writes, so a paused window is a false negative by construction.
    [switch]$SkipPlayProbe,
    # How long to wait for the replay icon to show `playing` before
    # failing with exit 7.
    [int]$PlayProbeTimeoutSeconds = 60,
    # Do not re-read the armed family addresses through the Host read API
    # before arming. Without it, stale addresses from an earlier session
    # fail fast (exit 8) instead of producing a no-hit window.
    [switch]$SkipLivenessCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Wt([string]$Message) {
    Write-Host ("wt_x64: " + $Message)
}

function Get-PreArmMarker {
    $marker = Join-Path $env:TEMP 'od-prearmed-debugger.json'
    if (-not (Test-Path -LiteralPath $marker)) { return $null }
    try {
        return (Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json)
    }
    catch { return $null }
}

function Find-X64DbgExe {
    if (-not [string]::IsNullOrWhiteSpace($X64DbgExe)) {
        if (Test-Path -LiteralPath $X64DbgExe) { return $X64DbgExe }
        return $null
    }
    $marker = Get-PreArmMarker
    if ($marker -and $marker.x64dbgExe -and (Test-Path -LiteralPath ([string]$marker.x64dbgExe))) {
        return [string]$marker.x64dbgExe
    }
    $roots = @('C:\work\tools\x64dbg', 'C:\x64dbg', 'C:\tools\x64dbg')
    foreach ($r in $roots) {
        # pre-arm launches x32\x32dbg.exe directly (x86 build for the x86
        # game); x64 build as fallback.
        $x = Join-Path $r 'release\x32\x32dbg.exe'
        if (Test-Path -LiteralPath $x) { return $x }
        $p = Join-Path $r 'release\x64\x64dbg.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

function Get-X64DbgProcess {
    # pre-arm launches x32\x32dbg.exe directly for the x86 (WOW64) game, so
    # the attached debugger process is named x32dbg. Match both for safety.
    return Get-Process -Name x64dbg, x32dbg -ErrorAction SilentlyContinue |
        Select-Object -First 1
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
    try {
        $rv = Get-Rendezvous
        if (-not $rv) { return $null }
        return Invoke-RestMethod -Uri ("{0}/api/v1/game/state" -f $rv.baseUri) -Headers @{
            'X-WotBTreader-Capability' = [string]$rv.capability
        } -TimeoutSec 5
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

# Parse a savedata evidence filename: odwt-0x<addr>-0x<rip>.bin (rip hex may
# be uppercase / no 0x depending on x64dbg's {rip} formatting).
function ConvertFrom-HitFilename([string]$Name) {
    if ($Name -notmatch '^odwt-0x([0-9a-fA-F]+)-([0-9a-fA-F]{4,16})\.bin$') { return $null }
    return [pscustomobject]@{
        Addr = '0x' + $Matches[1]
        Rip  = '0x' + $Matches[2]
    }
}

function Add-SendKeysType([ref]$Type, [string]$Text) {
    # WScript.Shell SendKeys treats + ^ % ~ ( ) { } [ ] as special; escape by
    # wrapping each in braces so the literal character is typed.
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $Text.ToCharArray()) {
        if ('+^%~(){}[]'.Contains($ch)) {
            [void]$sb.Append('{')
            [void]$sb.Append($ch)
            [void]$sb.Append('}')
        }
        else {
            [void]$sb.Append($ch)
        }
    }
    $Type.Value = $sb.ToString()
}

# -- M2 family helpers --------------------------------------------------------

# Sum of a family's member correlation scores (a bare family JSON may omit
# the score fields, so guard every member access under StrictMode).
function Get-FamilyScore {
    param([object]$Family)
    $total = 0.0
    if (-not $Family.PSObject.Properties['members'] -or $null -eq $Family.members) {
        return $total
    }
    foreach ($m in @($Family.members)) {
        if ($m.PSObject.Properties['score'] -and $null -ne $m.score) {
            $total += [double]$m.score
        }
    }
    return $total
}

# True when the family JSON marks the clean x/y/z triple complete.
function Test-FamilyComplete {
    param([object]$Family)
    return ($Family.PSObject.Properties['complete'] -and $Family.complete)
}

# Pick the family to trace from the report's families array: a complete
# family (clean x/y/z triple) wins over any incomplete one, and among the
# candidates the highest summed member score breaks the tie. Deterministic
# so the same report always selects the same family.
function Select-BestFamily {
    param([object[]]$Families)
    $pool = @()
    $complete = @($Families | Where-Object { Test-FamilyComplete -Family $_ })
    if ($complete.Count -gt 0) { $pool = @($complete) }
    else { $pool = @($Families) }
    if ($pool.Count -eq 0) { return $null }
    $best = $null
    $bestScore = [double]::MinValue
    foreach ($f in $pool) {
        $s = Get-FamilyScore -Family $f
        if ($s -gt $bestScore) {
            $bestScore = $s
            $best = $f
        }
    }
    return $best
}

# Build the arm plan for one family: member absolute addresses ordered by
# base-relative offset, deduplicated, first <=4 armed (DR0-DR3), the rest
# reported unarmed. Returns a hashtable with Armed and Unarmed arrays.
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

# Probe the replay play-state via the bottom-center HUD icon probe. Returns
# 'paused' | 'playing' | 'unknown' (unknown = HUD hidden / capture failed;
# never guessed).
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

# Pre-arm the debugger (pre-arm-debugger.ps1 -AutoAttach) and wait for the
# x64dbg/x32dbg process to appear. Returns $true when a debugger process is
# (or became) available.
function Invoke-AutoPreArm {
    if (Get-X64DbgProcess) { return $true }
    # pre-arm-debugger.ps1 -AutoAttach exits 0 even when it skips attach
    # (no game process), so without this check the 15s wait below always
    # burns in full when the game is absent. A write-trace needs the game
    # running anyway - fail fast instead of stalling.
    $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if (-not $game) {
        Write-Wt 'FAILED_prearm_no_game_process (nothing to attach to; launch the replay first)'
        return $false
    }
    $preArm = Join-Path $PSScriptRoot 'pre-arm-debugger.ps1'
    if (-not (Test-Path -LiteralPath $preArm)) {
        Write-Wt 'FAILED_prearm_script_missing'
        return $false
    }
    Write-Wt 'prearm invoking pre-arm-debugger.ps1 -AutoAttach'
    & $preArm -AutoAttach
    if ($LASTEXITCODE -ne 0) {
        Write-Wt ('FAILED_prearm_exit=' + $LASTEXITCODE)
        return $false
    }
    # The debugger window may take a moment to appear after launch.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (Get-X64DbgProcess) { return $true }
        Start-Sleep -Milliseconds 500
    }
    Write-Wt 'FAILED_prearm_no_window'
    return $false
}

# Re-read the armed family addresses through the guarded Host read API
# (Float, 4 bytes - the width the monitor used) to confirm they are still
# live in the CURRENT process. A fresh game launch reallocates the family
# structures, so a stale family must fail before arming, not produce a
# no-hit window. Returns $true only when every armed address readOk.
function Test-FamilyLiveness {
    param([string[]]$Addresses)
    $rv = Get-Rendezvous
    if (-not $rv) { return $false }
    try {
        $body = @{
            Addresses = @($Addresses)
            ValueKind = 'Float'
            ValueSize = 4
        } | ConvertTo-Json -Compress
        $resp = Invoke-RestMethod -Uri ($rv.baseUri + '/api/v1/game/discover/read') -Method Post -TimeoutSec 10 -Headers @{
            'X-WotBTreader-Capability' = [string]$rv.capability
            'Content-Type'             = 'application/json'
        } -Body $body
        if ($null -eq $resp -or $null -eq $resp.reads) { return $false }
        $ok = @($resp.reads | Where-Object { $_.readOk })
        return ($ok.Count -eq $Addresses.Count)
    }
    catch {
        return $false
    }
}

if (-not ('WtX64Gui' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WtX64Gui {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static void ForceForeground(IntPtr hWnd) {
        uint unused;
        uint target = GetWindowThreadProcessId(hWnd, out unused);
        uint current = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out unused) : 0;
        if (fgThread != 0) AttachThreadInput(current, fgThread, true);
        if (target != 0) AttachThreadInput(current, target, true);
        SetForegroundWindow(hWnd);
        if (target != 0) AttachThreadInput(current, target, false);
        if (fgThread != 0) AttachThreadInput(current, fgThread, false);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    public static bool WindowRect(IntPtr hWnd, out RECT r) {
        r = new RECT();
        return GetWindowRect(hWnd, out r);
    }
}
"@
}

try {
    # ---- 1. Resolve input mode: family (M2) or flat survivor file ----------
    $mode = 'survivor'
    if (-not [string]::IsNullOrWhiteSpace($FamilyFile)) { $mode = 'family' }
    # Write-breakpoint width: Float32 family members are 4 bytes, the legacy
    # Double survivors 8. -WriteSize overrides the mode default.
    $writeSize = if ($WriteSize -ne 0) { $WriteSize }
        elseif ($mode -eq 'family') { 4 }
        else { 8 }
    if ($writeSize -ne 4 -and $writeSize -ne 8) {
        Write-Wt ('FAILED_write_size_must_be_4_or_8 got=' + $WriteSize)
        exit 2
    }

    $family = $null
    $familyAxes = @()
    $armed = @()
    $unarmed = @()
    if ($mode -eq 'family') {
        # Family input: an od-048 correlate report JSON (its `families`
        # array) or a bare family JSON object with `members`.
        if (-not (Test-Path -LiteralPath $FamilyFile)) {
            Write-Wt ('FAILED_family_file_missing=' + $FamilyFile)
            exit 2
        }
        $familyDoc = $null
        try {
            $familyDoc = Get-Content -LiteralPath $FamilyFile -Raw -ErrorAction Stop | ConvertFrom-Json
        }
        catch {
            Write-Wt ('FAILED_family_file_unparseable=' + $FamilyFile)
            exit 2
        }
        $families = @()
        if ($null -ne $familyDoc -and $familyDoc.PSObject.Properties['families']) {
            $families = @($familyDoc.families)
        }
        elseif ($null -ne $familyDoc -and $familyDoc.PSObject.Properties['members']) {
            $families = @($familyDoc)   # bare family object
        }
        if ($families.Count -eq 0) {
            Write-Wt 'FAILED_family_file_no_families'
            exit 2
        }
        $family = Select-BestFamily -Families $families
        if ($null -eq $family) {
            Write-Wt 'FAILED_family_selection'
            exit 2
        }
        $plan = Get-FamilyArmPlan -Family $family
        $armed = @($plan.Armed)
        $unarmed = @($plan.Unarmed)
        if ($armed.Count -eq 0) {
            # Every member address was missing/invalid - arming nothing would
            # produce a "clean no-hit window" that reads as evidence instead
            # of a bad input. Fail fast like an empty survivor file.
            Write-Wt 'FAILED_family_no_armed_members (no member addresses resolved)'
            exit 2
        }
        if ($family.PSObject.Properties['axesCovered'] -and $null -ne $family.axesCovered) {
            $familyAxes = @($family.axesCovered)
        }
        $completeFlag = if (Test-FamilyComplete -Family $family) { 'true' } else { 'false' }
        Write-Wt ('family complete=' + $completeFlag + ' axes=' + ($familyAxes -join ',') + ' write_size=' + $writeSize)
        Write-Wt ('family_members_armed=' + $armed.Count + ' unarmed=' + $unarmed.Count + ' dr_limit=4')
        if ($unarmed.Count -gt 0) {
            Write-Wt ('WARN_unarmed_beyond_dr_limit skipped=' + $unarmed.Count)
        }
    }
    else {
        if (-not (Test-Path -LiteralPath $SurvivorFile)) {
            Write-Wt ("FAILED_survivor_file_missing=" + $SurvivorFile)
            exit 2
        }
        $rawLines = @(Get-Content -LiteralPath $SurvivorFile -ErrorAction Stop |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($rawLines.Count -eq 0) {
            Write-Wt 'FAILED_survivor_file_empty'
            exit 2
        }
        $seen = @{}
        $addrs = @()
        foreach ($line in $rawLines) {
            $tok = ConvertTo-HexToken $line
            if (-not $tok) {
                Write-Wt ("FAILED_survivor_unparseable_line=" + $line)
                exit 2
            }
            if (-not $seen.ContainsKey($tok)) {
                $seen[$tok] = $true
                $addrs += $tok
            }
        }
        # x64 DR0-DR3: only 4 hardware breakpoints exist.
        $armCount = [Math]::Min($addrs.Count, 4)
        $armed = @($addrs | Select-Object -First $armCount)
        $unarmed = @($addrs | Select-Object -Skip $armCount)
        Write-Wt ("survivors_total=" + $addrs.Count + " armed=" + $armed.Count + " dr_limit=4 write_size=" + $writeSize)
        if ($unarmed.Count -gt 0) {
            Write-Wt ("WARN_unarmed_beyond_dr_limit skipped=" + $unarmed.Count)
        }
    }

    # ---- 2. Generate the x64dbg script -------------------------------------
    # savedata string-formats its filename arg (stringformatinline in
    # cmd-memory-operations.cpp), so {rip} renders into the evidence filename.
    # The hits dir path is embedded unquoted; to stay quote-safe the script
    # rejects a HitsDir containing spaces (8.3 short paths are the workaround).
    if ($HitsDir -match '\s') {
        Write-Wt ('FAILED_hitsdir_contains_spaces use_8.3_short_path: ' + $HitsDir)
        exit 2
    }
    $null = New-Item -ItemType Directory -Path $HitsDir -Force -ErrorAction Stop
    $scriptLines = New-Object System.Collections.Generic.List[string]
    [void]$scriptLines.Add('// od-wt write-trace script generated by x64dbg-write-trace.ps1')
    foreach ($a in $armed) {
        [void]$scriptLines.Add(('bph {0},w,{1}' -f $a, $writeSize))
        [void]$scriptLines.Add(('bphwlog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $a))
        $hitFile = Join-Path $HitsDir ('odwt-{0}-{{rip}}.bin' -f $a)
        [void]$scriptLines.Add(('SetHardwareBreakpointCommand {0}, "savedata {1} rip 64"' -f $a, $hitFile))
        [void]$scriptLines.Add(('SetHardwareBreakpointFastResume {0}' -f $a))
    }
    [void]$scriptLines.Add(('log "ODWT_ARMED count={0}"' -f $armed.Count))
    if (-not $NoResume) {
        [void]$scriptLines.Add('run')
    }
    Set-Content -LiteralPath $ScriptFile -Value $scriptLines -Encoding ascii
    Write-Wt ("script_written=" + $ScriptFile + " lines=" + $scriptLines.Count)

    if ($DryRun) {
        Write-Wt 'DRYRUN skipping x64dbg injection'
        Write-Wt ("DRYRUN mode=" + $mode + " write_size=" + $writeSize + " armed=" + $armed.Count)
        Write-Wt ("DRYRUN hitsdir=" + $HitsDir)
        Write-Wt ('DRYRUN script=' + ($scriptLines -join ' | '))
        # DryRun still emits the (empty) result file so callers can rely on it.
        Set-Content -LiteralPath $ResultPath -Value '' -Encoding ascii
        Write-Wt 'OK dryrun'
        exit 0
    }

    # ---- 3. Driver-mode setup: pre-arm + green-window prechecks -----------
    # -AutoWriteTrace is the roadmap's M2 write-trace invocation: pre-arm
    # the debugger, verify the gate, confirm the replay is PLAYING (a paused
    # replay produces no position writes, so a paused window is a false
    # negative), and confirm the family addresses are still live in THIS
    # process (a fresh launch reallocates them). Each check is skippable.
    if ($AutoWriteTrace -or $AutoPreArm) {
        if (-not (Invoke-AutoPreArm)) {
            Write-Wt 'FAILED_prearm'
            exit 3
        }
    }

    if ($AutoWriteTrace -and -not $SkipGateCheck) {
        $st = Get-GateState
        $vs = if ($st -and $st.verificationState) { [string]$st.verificationState } else { 'unreachable' }
        if ($vs -ne 'OfflineReplayVerified') {
            Write-Wt ("FAILED_gate_precheck=" + $vs)
            exit 5
        }
        Write-Wt 'gate=OfflineReplayVerified'
    }

    # Replay play-state awareness: fail closed on a confirmed pause, proceed
    # with a warning on unknown (HUD hidden / capture failed) - a hidden HUD
    # must not block a legitimate trace, but a paused replay must not burn a
    # window producing zero hits by construction.
    if ($AutoWriteTrace -and -not $SkipPlayProbe) {
        $playState = Get-ReplayPlayState
        Write-Wt ('play_state=' + $playState)
        if ($playState -eq 'paused') {
            $playDeadline = (Get-Date).AddSeconds($PlayProbeTimeoutSeconds)
            Write-Wt ('waiting_for_playing up_to=' + $PlayProbeTimeoutSeconds + 's (press SPACE in the game to resume the replay)')
            while ((Get-Date) -lt $playDeadline) {
                Start-Sleep -Seconds 2
                $playState = Get-ReplayPlayState
                if ($playState -ne 'paused') { break }
            }
            if ($playState -eq 'paused') {
                Write-Wt 'FAILED_replay_paused (a paused replay writes nothing; press SPACE and rerun, or -SkipPlayProbe)'
                exit 7
            }
            Write-Wt ('play_state=' + $playState)
        }
    }

    # Family liveness: re-read the armed addresses through the Host read API
    # so a stale family (fresh replay launch) fails fast instead of producing
    # a clean-looking no-hit window. Requires a verified gate (the read API
    # is gate-gated); skippable with -SkipLivenessCheck.
    if ($mode -eq 'family' -and $AutoWriteTrace -and -not $SkipLivenessCheck -and -not $SkipGateCheck) {
        if (-not (Test-FamilyLiveness -Addresses $armed)) {
            Write-Wt 'FAILED_family_stale (addresses not live in this process; re-run od-048 on THIS launch, or -SkipLivenessCheck)'
            exit 8
        }
        Write-Wt ('family_liveness_ok armed=' + $armed.Count)
    }

    # ---- 4. Locate x64dbg + ensure attached --------------------------------
    $x64Exe = Find-X64DbgExe
    if (-not $x64Exe) {
        Write-Wt 'FAILED_x64dbg_not_found'
        exit 3
    }
    $proc = Get-X64DbgProcess
    if (-not $proc) {
        Write-Wt 'FAILED_x64dbg_not_running_run_pre-arm_debugger_first'
        exit 3
    }
    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) {
        Write-Wt 'FAILED_x64dbg_no_window'
        exit 3
    }
    Write-Wt ("x64dbg_pid=" + $proc.Id)

    # ---- 5. Inject scriptload + scriptrun into the command bar -------------
    $rect = New-Object WtX64Gui+RECT
    if (-not [WtX64Gui]::WindowRect($proc.MainWindowHandle, [ref]$rect)) {
        Write-Wt 'FAILED_get_window_rect'
        exit 4
    }
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int]($rect.Bottom - 16)   # command bar is the full-width bottom strip
    [WtX64Gui]::ForceForeground($proc.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    [WtX64Gui]::Click($cx, $cy)
    Start-Sleep -Milliseconds 400

    $wshell = New-Object -ComObject WScript.Shell
    $esc = ''
    Add-SendKeysType ([ref]$esc) ('scriptload "' + $ScriptFile + '"')
    $null = $wshell.SendKeys($esc)
    Start-Sleep -Milliseconds 300
    $null = $wshell.SendKeys('{ENTER}')
    Start-Sleep -Milliseconds 600
    $null = $wshell.SendKeys('scriptrun')
    Start-Sleep -Milliseconds 300
    $null = $wshell.SendKeys('{ENTER}')
    Write-Wt 'injected scriptload+scriptrun'

    # ---- 6. Poll hits + gate for the trace window --------------------------
    $deadline = (Get-Date).AddSeconds($TraceSeconds)
    $hits = @()
    $lastAnnounce = Get-Date
    while ((Get-Date) -lt $deadline) {
        foreach ($f in @(Get-ChildItem -LiteralPath $HitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)) {
            $parsed = ConvertFrom-HitFilename $f.Name
            if ($parsed) {
                $hitKey = ($parsed.Addr + ' ' + $parsed.Rip)
                if ($hits -notcontains $hitKey) { $hits += $hitKey }
            }
        }

        if (-not $SkipGateCheck) {
            $st = Get-GateState
            $vs = if ($st -and $st.verificationState) { [string]$st.verificationState } else { 'unreachable' }
            if ($vs -ne 'OfflineReplayVerified') {
                Write-Wt ("STOP_gate=" + $vs)
                Write-Wt ("hits=" + $hits.Count)
                exit 5
            }
        }

        if (((Get-Date) - $lastAnnounce).TotalSeconds -ge 30) {
            Write-Wt ("trace_open remaining_s=" + [int]($deadline - (Get-Date)).TotalSeconds + " hits=" + $hits.Count)
            # Advisory mid-window play-state probe: if the replay paused
            # mid-window, position writes stall and the remaining window
            # captures nothing. Warn so a partial hit set is not misread as
            # a clean no-hit result. Advisory only - the gate poll is the
            # hard stop.
            if ($AutoWriteTrace -and -not $SkipPlayProbe) {
                $psNow = Get-ReplayPlayState
                if ($psNow -eq 'paused') {
                    Write-Wt 'WARN_replay_paused_mid_window - resume the replay (SPACE) to keep capturing writes'
                }
            }
            $lastAnnounce = Get-Date
        }
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    # ---- 7. Write evidence + report ----------------------------------------
    Set-Content -LiteralPath $ResultPath -Value $hits -Encoding ascii
    Write-Wt ("hits=" + $hits.Count)

    if ($mode -eq 'family') {
        # Per-member evidence: map each captured addr -> rip line back to its
        # family member (axis/offset) so the write sites are attributable to
        # a coordinate component, then write the family report.
        $memberList = if ($family.PSObject.Properties['members'] -and $null -ne $family.members) { @($family.members) } else { @() }
        $memberEntries = @()
        foreach ($m in $memberList) {
            if (-not $m.PSObject.Properties['address']) { continue }
            $addr = ConvertTo-HexToken ([string]$m.address)
            if (-not $addr) { continue }
            $addrKey = $addr.ToLowerInvariant()
            $rips = @()
            foreach ($h in $hits) {
                $parts = $h -split ' '
                if ($parts.Count -ge 2 -and $parts[0].ToLowerInvariant() -eq $addrKey) {
                    $rips += $parts[1]
                }
            }
            $memberEntries += [ordered]@{
                address     = $addr
                offsetBytes = if ($m.PSObject.Properties['offsetBytes'] -and $null -ne $m.offsetBytes) { [int]$m.offsetBytes } else { 0 }
                axis        = if ($m.PSObject.Properties['axis']) { [string]$m.axis } else { '?' }
                score       = if ($m.PSObject.Properties['score'] -and $null -ne $m.score) { [double]$m.score } else { 0.0 }
                hits        = $rips.Count
                rips        = $rips
            }
        }
        $hitMembers = @($memberEntries | Where-Object { $_.hits -gt 0 })
        $familyVerdict = if ($hitMembers.Count -gt 0) { 'family-hit' } else { 'family-no-hit' }
        $familyReport = [ordered]@{
            mode         = 'family'
            complete     = Test-FamilyComplete -Family $family
            axesCovered  = @($familyAxes)
            writeSize    = $writeSize
            armedCount   = $armed.Count
            unarmedCount = $unarmed.Count
            hitsTotal    = $hits.Count
            hitMembers   = $hitMembers.Count
            verdict      = $familyVerdict
            members      = $memberEntries
        }
        $familyResultPath = $ResultPath + '.family.json'
        $familyJson = $familyReport | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($familyResultPath, $familyJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Wt ('family_verdict=' + $familyVerdict + ' hit_members=' + $hitMembers.Count)
        Write-Wt ('family_report=' + $familyResultPath)
    }

    Write-Wt 'OK trace_window_completed'
    exit 0
}
catch {
    Write-Wt ("FAILED_unexpected=" + $_.Exception.Message)
    exit 6
}
