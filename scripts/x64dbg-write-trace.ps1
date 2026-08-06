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
    (a complete x/y/z triple wins; else a usable family -- one or more
    members with at least one NOT edge-aligned -- by highest mean member
    score; all-edge decoy families are excluded so a bad-anchor family cannot
    burn the trace window over a real sibling pair), and arms 4-byte write
    breakpoints on the member addresses (Float32 coordinates at 4-byte
    offsets). One trace window maps the write sites of all three coordinate
    components.

  Solo mode (FRESH14): -SoloAddress <hex> arms ONE address without a family
    file. This exists because the strongest artifact the pipeline has
    produced (FRESH12: 0x1FC57238, y@1.000, tight INTERIOR ambiguity band
    [-10,-7.5] = 2.5s, not edge-aligned) was structurally excluded from every
    family -- its +/-16-byte neighbors scored below the family-seed floor, so
    the builder never grouped it and the >=2-member gate could never arm it.
    Solo mode runs the address through the SAME floors as a family member:
    -SoloScore must clear -MinMemberScore and the supplied
    -SoloBandMinSeconds/-SoloBandMaxSeconds must clear -MaxMemberBandSeconds
    (missing band = unknown = refused fail-closed unless the floor is 0). A
    bare override with no correlation evidence requires -MinMemberScore 0
    -MaxMemberBandSeconds 0 (direct investigation). Single-member families in
    a -FamilyFile are accepted by the same path (Test-UsableFamily no longer
    requires >=2 members), so the od-048 report can carry a solo family.

  The generated x64dbg script arms MEMORY write breakpoints (bpm
  <addr>, 1, w - guard-page, fires on ANY thread). Hardware bph was ruled
  out by the FRESH9 probe campaign: bph arms DR registers on the active
  thread only (the x64dbg source has "//TODO: hwbp in multiple threads
  TEST"), and after attach the active thread is the break-in/loader thread,
  not the game's writing thread - so bph provably misses. Each armed
  address gets a SetMemoryBreakpointLog line (logText is formatted with
  stringformatinline, so {rip} renders the writing instruction address)
  and a SetMemoryBreakpointCommand savedata line writing a static
  per-address evidence file (the command shape proven to produce hit
  files; {rip} inside the savedata filename is not reliable across
  builds). Fast-resume and break conditions are NOT used: the engine
  source (debugger.cpp cbGenericBreakpoint) skips log+command when
  fastResume && condition==0, and a non-zero condition breaks the
  debuggee mid-window.

  The script is injected via `scriptload "<file>"` then `scriptrun` into
  the command bar, driven through UI Automation (ValuePattern set + focus
  + PostMessage ENTER) - SendKeys is mangled by the IME on this machine
  (FRESH9: a literal `strref 0, 0, 1` landed in the bar). The driver
  attaches itself: x64dbg parses integer literals as HEX, so `attach 0x<pid>`
  is required (the decimal -p pre-arm flag was the FRESH9 zero-hit root
  cause); the command-bar attach does not pause the debuggee
  (pauseAtAttach is script-only), so `pause` follows before scriptrun.
  After the window, the Log tab is harvested via UIA for the ODWT_HIT
  lines, yielding the addr -> rip write-site evidence.

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
    # Max seconds to wait for the x64dbg/x32dbg main window to appear after
    # pre-arm (window creation is async; attach to a busy game can lag it).
    [int]$WindowWaitSeconds = 20,
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
    [switch]$SkipLivenessCheck,
    # Attach-smoke mode (M2 mid-window pre-flight, driven by od-048
    # -AttachSmokeOnFirstRound): attach to the LIVE game (hex pid) -> pause ->
    # verify the pause stalled -> optional bpm arm/clear on -SmokeProbeAddress
    # -> detach -> verify the game resumed. Writes a JSON smoke report and
    # exits 0 (green) or 1 (red) WITHOUT touching family files, survivors, or
    # the trace window. Exists so the two live-only mechanics (x64dbg attach
    # to the real game, guard-page install) are proven mid-battle, before the
    # correlate + trace window is spent on an undiagnosable no-hit run.
    [switch]$AttachSmoke,
    # For -AttachSmoke: an absolute hex address (0x...) whose guard page is
    # armed and immediately cleared, proving memory-BP install works on the
    # live process. Empty = attach/pause/detach round-trip only.
    [string]$SmokeProbeAddress = '',
    # For -AttachSmoke: JSON report path (default %TEMP%\od-048-attach-smoke.json).
    [string]$SmokeResultPath = $(Join-Path $env:TEMP 'od-048-attach-smoke.json'),
    # For -AttachSmoke: keep the debugger attached + the game resumed
    # (scriptrun-resume) after the smoke instead of detaching, so the M2
    # trace can reuse the SAME debugger. FRESH26: the trace's SECOND attach
    # (fresh re-pre-arm at trace time) was the FRESH25 STOP_gate=Denied root
    # cause - it froze the game (WOW64 attach-freeze class; the operator saw
    # 'not responding'), the host monitor denied the evidence terminally, and
    # the trace's first gate poll read Denied (exit 5) before the window
    # opened. Attach-once keeps ONE debugger from smoke (battle-start,
    # verified, relaunchable) through the trace, eliminating the second
    # attach and the denial with it.
    [switch]$KeepAttached,
    # For the trace window (-AutoWriteTrace): reuse the debugger the smoke
    # left attached instead of attaching a second time (see -KeepAttached).
    # When set, the script skips the `attach` step (the debugger is already
    # attached to the game) and goes straight to pause + scriptload +
    # scriptrun. Passed by od-048 only when its smoke report says
    # keptAttached=true; fail-closed otherwise (a fresh attach is safe when
    # no debugger owns the process).
    [switch]$ReuseAttached,
    # Minimum correlation score for EVERY member of the armed family. A member
    # below the floor is noise, not evidence (FRESH10 live: the gate armed
    # x@0.20 + y@1.00 and the trace burned the green window on the noise
    # member, producing family-no-hit). Families with any below-floor member
    # are refused before arming. 0 disables the floor (direct-investigation
    # override).
    [double]$MinMemberScore = 0.9,
    # Maximum AMBIGUITY-BAND width (shiftMax - shiftMin, seconds) for every
    # member of the armed family. The band is the set of shifts achieving the
    # max match count (width = tolerance / |local slope|); a member whose band
    # covers most of the sweep matches at ANY shift, so its score is cheap and
    # proves nothing about being a written coordinate (FRESH12: 42 of 50
    # FRESH10 results were degenerate y@~1.0 with 20-60s bands on a 10.9-unit
    # ground axis). A member whose band is missing or wider than the floor is
    # refused before arming. 0 disables the floor entirely (unknown bands
    # allowed, direct-investigation override). Default 60s = 1/3 of the
    # od-048 default sweep (+-90s = 180s band). FRESH22: this floor was 20s
    # (1/3 of the OLD +-30s sweep) and was never re-derived when the sweep
    # widened to +-90s (commit 888fb58) -- FRESH21's real z survivors (span
    # 275, band 31.5s = 17.5% of the sweep) were refused and the trace never
    # fired. NOTE: the floor is absolute, not sweep-relative; pair it with
    # the same -MaxTimeShiftSeconds that produced the bands.
    [double]$MaxMemberBandSeconds = 60.0,
    # FRESH22: minimum observed movement SPAN (max-min of the value series,
    # game units) for every member of the armed family. The band floor alone
    # cannot catch the degenerate static class at the widened sweep (FRESH10's
    # y@~1.0 had a 20-60s band that now fits): a value that never moves
    # matches a low-information axis at any shift, so its score is cheap. A
    # member whose span is KNOWN and below the floor is refused; a member with
    # no span on the wire (server-family members predate the field) passes
    # this check and is still guarded by the band + edge floors. 0 disables.
    [double]$MinMemberSpan = 10.0,
    # FRESH14 solo-survivor mode: arm ONE address without a -FamilyFile. This
    # is the direct-investigation escape hatch for the strongest-evidence
    # class the pipeline has produced (FRESH12: 0x1FC57238, tight interior
    # band, structurally excluded from every family because its +/-16-byte
    # neighbors scored below the seed floor). The address is run through the
    # SAME score + band floors as a family member - pass -SoloScore (must
    # clear -MinMemberScore) and -SoloBandMinSeconds/-SoloBandMaxSeconds
    # (width must clear -MaxMemberBandSeconds); a missing band is refused
    # fail-closed unless the band floor is 0. A bare override with no
    # correlation evidence needs -MinMemberScore 0 -MaxMemberBandSeconds 0.
    [string]$SoloAddress = '',
    # Axis label for -SoloAddress (report/metadata only; the breakpoint does
    # not depend on it). Default 'x'.
    [string]$SoloAxis = 'x',
    # Correlation score for -SoloAddress (0 = unknown, fails the default
    # -MinMemberScore 0.9 floor unless raised or the floor is disabled).
    [double]$SoloScore = 0.0,
    # Ambiguity band for -SoloAddress (seconds). Both required for the band
    # floor to pass; either missing = band unknown = refused unless
    # -MaxMemberBandSeconds 0.
    [double]$SoloBandMinSeconds = [double]::MinValue,
    [double]$SoloBandMaxSeconds = [double]::MinValue
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

function Get-X64DbgWindowHandle {
    # Returns [pscustomobject]@{ Id; Handle } for a debugger process that
    # currently HAS a main window, or $null. MainWindowHandle is computed once
    # per Process object and is 0 if sampled mid-creation, so fall back to
    # EnumWindows (which sees Qt windows MainWindowHandle can miss).
    $proc = Get-X64DbgProcess
    if (-not $proc) { return $null }
    $h = $proc.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) { return [pscustomobject]@{ Id = $proc.Id; Handle = $h } }
    # FRESH15b: the EnumWindows fallback must pick the LARGEST-AREA window
    # (the real main window), not the first enumerated one - a transient Qt
    # splash/helper window can precede the main window and never exposes the
    # command bar (smoke detail='no_command_bar', pre-attach).
    $best = [WtX64Gui]::LargestWindowForProcess([uint32]$proc.Id)
    if ($best -ne [IntPtr]::Zero) { return [pscustomobject]@{ Id = $proc.Id; Handle = $best } }
    return $null
}

function Wait-X64DbgWindow {
    # Poll for a window-ready debugger process. Window creation is async after
    # pre-arm launches the process, and attach to a busy game can lag it.
    param([int]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $win = Get-X64DbgWindowHandle
        if ($win) { return $win }
        Start-Sleep -Milliseconds 500
    }
    return $null
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

# -- UIA command-bar channel ------------------------------------------------
# SendKeys is broken on this machine (IME interception), so the command bar
# is driven via UI Automation: find the CommandLineEdit by class, set its
# value through ValuePattern (immune to focus/IME), focus it, and execute
# with PostMessage ENTER (no foreground requirement). Proven end-to-end in
# the FRESH9 probe campaign (markers landed 3/3, log tab readable).
function Find-X64DbgCommandBar {
    param([IntPtr]$Handle)
    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    # FRESH15b: a freshly-launched x32dbg window's UIA tree lags window
    # creation - the smoke sampled in the SAME second the window appeared and
    # the single FindAll found no Edit (detail='no_command_bar', pre-attach).
    # The probe campaign settled 4s before any UIA read; poll both root
    # acquisition AND the edit find for up to 15s so the tree-population race
    # is absorbed instead of failing the smoke.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $root = $null
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle) }
        catch { }
        if ($root) {
            try {
                $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
                foreach ($e in $edits) {
                    if ($e.Current.ClassName -eq 'CommandLineEdit') {
                        # FRESH15f: only accept an element whose ValuePattern
                        # is actually supported - a half-initialized Qt UIA
                        # provider can expose the edit class without the
                        # pattern, and the first Send would throw 'Unsupported
                        # Pattern' (observed mid-run after a UIA rebuild). The
                        # poll loop keeps searching for a pattern-capable one.
                        try {
                            if ($e.GetSupportedPatterns() -contains [System.Windows.Automation.ValuePattern]::Pattern) {
                                return $e
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Send-X64DbgCommand {
    param($CommandBar, [IntPtr]$Handle, [string]$Text)
    if (-not $CommandBar) { return }
    try {
        $vp = $CommandBar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }
    catch {
        # FRESH15f: a ForceForeground or a Qt UIA rebuild can invalidate the
        # cached element between Find and Send ('Unsupported Pattern').
        # Re-find a fresh pattern-capable element and retry once.
        $CommandBar = Find-X64DbgCommandBar -Handle $Handle
        if (-not $CommandBar) { return }
        $vp = $CommandBar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    }
    $vp.SetValue($Text)
    try { $CommandBar.SetFocus() } catch { }
    Start-Sleep -Milliseconds 200
    [WtX64Ui]::SendEnter($Handle)
    Start-Sleep -Milliseconds 700
}

# Activate the Log tab (so its lines are exposed to UIA) and return text
# elements that carry engine log lines of interest.
function Read-X64DbgLog {
    param([IntPtr]$Handle)
    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $root = $null
    for ($i = 0; $i -lt 10 -and -not $root; $i++) {
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle) }
        catch { Start-Sleep -Milliseconds 800 }
    }
    if (-not $root) { return @() }
    $tabCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
        if ($t.Current.Name -eq 'Log') {
            try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }
            catch { try { $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break } catch { } }
        }
    }
    Start-Sleep -Milliseconds 800
    $lines = New-Object System.Collections.Generic.List[string]
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -and ($n -match 'ODWT_|Error executing|Memory breakpoint|Hardware breakpoint')) {
            $lines.Add($n)
        }
    }
    return $lines
}

# -- M2 family helpers --------------------------------------------------------

# Sum of a family's member correlation scores (a bare family JSON may omit
# True when the family JSON marks the clean x/y/z triple complete.
function Test-FamilyComplete {
    param([object]$Family)
    return ($Family.PSObject.Properties['complete'] -and $Family.complete)
}

# True when EVERY member of the family carries a correlation score at or
# above the -MinMemberScore floor. A below-floor member is noise (FRESH10:
# x@0.20 armed alongside y@1.00 -> the trace burned the window on the noise
# member). Scores below the floor can be coincidental matches, not the
# coordinate field, so such a family must never win a trace window.
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

# Member ambiguity-band width in seconds, or $null when the band is unknown.
# Accepts BOTH wire shapes: the correlate response emits shiftMin/MaxSeconds
# (C# camelCase) and the od-048 M1 report re-emits them as
# shiftBandMin/MaxSeconds, so the family file may carry either pair.
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

# True when EVERY member's ambiguity band is known and at or under the
# -MaxMemberBandSeconds floor. A degenerate member matches at any shift and
# carries zero alignment information (FRESH12: FRESH10's armed y@1.00 had a
# [-10,+30] = 40s band on a 60s sweep -> family-no-hit even though its score
# was perfect), so it must never win a trace window. A member with NO band
# fields is refused too (fail-closed: an unknown band is not proven
# discriminating). 0 disables the floor.
function Test-FamilyBanded {
    param([object]$Family)
    if ($MaxMemberBandSeconds -le 0) { return $true }
    if (-not $Family.PSObject.Properties['members'] -or $null -eq $Family.members) {
        return $false
    }
    foreach ($m in @($Family.members)) {
        $width = Get-MemberBandWidth -Member $m
        if ($null -eq $width -or $width -gt $MaxMemberBandSeconds) { return $false }
        # FRESH22 span floor: a member that provably never moves (span below
        # the floor) matched a low-information axis at any shift -- its score
        # is cheap and it must not win the trace window. Unknown span (no
        # field on the wire) passes; the band + edge floors still guard it.
        if ($MinMemberSpan -gt 0 -and $m.PSObject.Properties['span'] -and $null -ne $m.span) {
            if ([double]$m.span -lt $MinMemberSpan) { return $false }
        }
    }
    return $true
}

# True when the family is usable for a trace window: every member scored at
# or above the -MinMemberScore floor (score floor added FRESH11: a below-floor
# member is noise and would burn the window), every member's ambiguity band is
# known and within -MaxMemberBandSeconds (band floor added FRESH13: a
# degenerate member matches at any shift regardless of score), at least ONE
# member (>=2 no longer required since FRESH14: the strongest evidence the
# pipeline has produced - FRESH12's 0x1FC57238, tight interior band - was
# structurally excluded from every family because its +/-16-byte neighbors
# scored below the seed floor; a single-member family whose sole member
# clears both floors is now armable), and at least one member NOT
# edge-aligned (the M2 stop rule, mirrors the od-048 gate). An all-edge
# family is a bad-anchor decoy -- every member rides the sweep edge, so it
# must never win the trace window over a real sibling pair.
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

# Distinct axis count of a family's members (x/z pair = 2, complete triple
# = 3, a same-axis run = 1). The axis count is the primary selection rank:
# a family reproducing MULTIPLE components of one entity is evidence of a
# coordinate vector, while a run of same-axis addresses is a copy buffer.
function Get-FamilyAxisCount {
    param([object]$Family)
    $axes = @{}
    foreach ($m in @($Family.members)) {
        if ($m.PSObject.Properties['axis'] -and $m.axis) { $axes[$m.axis] = $true }
    }
    return $axes.Count
}

# Mean member score. Mean (not sum) is the tie-break: summed score rewards
# member COUNT over member QUALITY -- live OD-049 evidence had a 5-member
# weak x-run (scores ~0.4, sum 2.16) beating a perfect x/z pair (1.0/1.0,
# sum 2.0), which would have armed the trace on the copy buffer instead of
# the coordinate vector.
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

# Pick the family to trace from the report's families array. Priority: (1) a
# complete family (clean x/y/z triple); (2) a usable family (one or more
# members, every member at or above -MinMemberScore, every member's ambiguity
# band within -MaxMemberBandSeconds, at least one non-edge-aligned -- an
# all-edge family must never beat a real sibling pair: live OD-049 evidence
# had the 5-member all-edge decoy out-scoring the genuine x/z pair on summed
# score, which would have armed the trace on fabricated alignment); (3) any
# family clearing BOTH floors, as a direct-investigation fallback when the
# caller (od-048 gate) has already vetted the report. The -MinMemberScore
# floor AND the -MaxMemberBandSeconds floor apply to EVERY tier (tier 3
# included): a below-floor member is noise (FRESH10: x@0.20 + y@1.00 ->
# family-no-hit) and a degenerate/bandless member matches at any shift
# (FRESH12/FRESH13), so neither must ever be armed, even inside an otherwise
# complete triple. A bare score-only family JSON (no band fields) needs
# -MaxMemberBandSeconds 0 to arm via tier 3 (direct investigation), since the
# band floor is fail-closed on unknown bands. Among the candidates the rank is
# (distinct axis count desc, then mean member score desc). Deterministic so
# the same report always selects the same family. Returns $null when no
# family clears both floors (caller must not arm).
function Select-BestFamily {
    param([object[]]$Families)
    # Both predicates parenthesized: `A -and (B)` after a bare command parses
    # `-and` into A's argument binding (PowerShell command-expression
    # precedence), so a family that FAILS Test-FamilyBanded could still be
    # selected. Verified in a harness: the un-parenthesized form selected a
    # bandless family; the parenthesized form correctly refused it.
    $scored = @($Families | Where-Object { (Test-FamilyScored -Family $_) -and (Test-FamilyBanded -Family $_) })
    $complete = @($scored | Where-Object { Test-FamilyComplete -Family $_ })
    if ($complete.Count -gt 0) { return (Select-HighestRankedFamily -Families $complete) }
    $usable = @($scored | Where-Object { Test-UsableFamily -Family $_ })
    if ($usable.Count -gt 0) { return (Select-HighestRankedFamily -Families $usable) }
    if ($scored.Count -gt 0) { return (Select-HighestRankedFamily -Families $scored) }
    return $null
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
    if (Get-X64DbgWindowHandle) { return $true }
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
    # The debugger window may take a moment to appear after launch; wait for
    # the WINDOW, not just the process - Start-Process returns instantly while
    # the Qt main window can lag (FRESH8 FAILED_x64dbg_no_window).
    $win = Wait-X64DbgWindow -TimeoutSeconds 15
    if ($win) { return $true }
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

# FRESH24/25: read the armed addresses' CURRENT float values through the Host
# read API (same mechanism as Test-FamilyLiveness). Returns a hashtable
# address(lowercase) -> double, or $null on any failure. Used to decide
# whether the BATTLE WORLD advanced across the trace window: a playing replay
# moves the armed z addresses by tens of units in 25s; a paused/roster replay
# renders (CPU burns -- liveness=running) but the world is frozen, so the
# values are bit-identical.
function Read-FamilyValues {
    param([string[]]$Addresses)
    $rv = Get-Rendezvous
    if (-not $rv) { return $null }
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
        if ($null -eq $resp -or $null -eq $resp.reads) { return $null }
        $vals = @{}
        foreach ($r in @($resp.reads)) {
            if ($r.readOk -and $null -ne $r.value) {
                $vals[[string]$r.address] = [double]$r.value
            }
        }
        return $vals
    }
    catch {
        return $null
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    // Top-level windows owned by the given pid (Qt windows that
    // Process.MainWindowHandle can miss during creation are found here).
    public static IntPtr[] WindowsForProcess(uint pid) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint wndPid;
            GetWindowThreadProcessId(hWnd, out wndPid);
            if (wndPid == pid) { list.Add(hWnd); }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // FRESH15b: when Process.MainWindowHandle is still 0 mid-creation, the
    // old fallback took the FIRST enumerated window, which can be a transient
    // Qt helper/splash window that never carries the CommandLineEdit - the
    // attach-smoke failed with 'no_command_bar' on such a handle. Select the
    // LARGEST-AREA top-level window instead: that is the real main window
    // (the probe campaign found the command bar 0.1s after it appeared).
    public static IntPtr LargestWindowForProcess(uint pid) {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        RECT r;
        foreach (var h in WindowsForProcess(pid)) {
            if (GetWindowRect(h, out r)) {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
            }
        }
        return best;
    }

    // "title1|title2" diagnostic string of the pid's top-level window titles.
    public static string WindowTitles(uint pid) {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var h in WindowsForProcess(pid)) {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            parts.Add(sb.ToString());
        }
        return string.Join("|", parts);
    }
}
"@
}

if (-not ('WtX64Ui' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;

// PostMessage ENTER for the command bar: immune to foreground/IME, unlike
// SendKeys (proven broken on this machine in FRESH9).
public static class WtX64Ui {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint VK_RETURN = 0x0D;
    public static void SendEnter(IntPtr hwnd) {
        PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
        PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
    }
}
"@
}

# Attach-smoke gate: prove the x64dbg -> live-game attach/pause/detach
# round-trip (hex pid) and, optionally, a guard-page install/clear on a probe
# address. Returns 0 (green) / 1 (red) and writes a JSON report so the caller
# (od-048) can fail closed BEFORE spending the correlate + trace window.
function Invoke-AttachSmoke {
    param(
        [string]$ProbeAddress,
        [string]$ResultPath,
        # FRESH26 attach-once: leave the debugger attached + the game resumed
        # (scriptrun-resume) so the M2 trace reuses it instead of a second
        # attach (the FRESH25 STOP_gate=Denied root cause). Off by default for
        # standalone smoke; od-048 passes it when the auto-trace will follow.
        [switch]$KeepAttached
    )
    $report = [ordered]@{
        smoke          = 'fail'
        ranUtc         = ([DateTime]::UtcNow).ToString('o')
        pid            = 0
        attachedHexPid = ''
        pauseVerified  = $false
        bpmArmed       = 'unverified'
        resumeVerified = $false
        # FRESH26: whether the debugger was left ATTACHED (not detached) so
        # the trace can reuse it. Only true when -KeepAttached was requested
        # AND the scriptrun resume succeeded; otherwise false (fail-closed).
        keptAttached   = $false
        # FRESH15e: the exact game-paused wall window (pause verified ->
        # resume verified). od-048 subtracts it from post-smoke sample stamps
        # so the correlate's wall->tick mapping stays linear across the pause.
        pauseStartUtc   = ''
        resumeUtc       = ''
        probeAddress   = $ProbeAddress
        detail         = ''
    }
    function Write-SmokeReport {
        try {
            $json = $report | ConvertTo-Json -Depth 6
            [System.IO.File]::WriteAllText($ResultPath, $json, (New-Object System.Text.UTF8Encoding($false)))
            Write-Wt ('attach_smoke_report=' + $ResultPath)
        }
        catch {
            Write-Wt ('attach_smoke_report_write_failed: ' + $_.Exception.Message)
        }
    }
    # FRESH26: attach-once (the FRESH25 STOP_gate=Denied root cause). The
    # trace's SECOND x32dbg attach (re-pre-arm at trace time) froze the game
    # (WOW64 attach-freeze class; the operator saw 'not responding'), and the
    # host monitor - polling every 500ms - denied the evidence terminally
    # (evidence.monitor_unhealthy at 21:32:53) so the trace's first gate poll
    # read Denied and exited 5 before the window opened. Keeping ONE debugger
    # attached from the smoke (battle-start, verified, relaunchable) through
    # the trace eliminates the second attach and the denial with it. The
    # smoke leaves the debugger attached + the game RESUMED via scriptrun of
    # a resume script (the proven resume path - command-bar 'run' never
    # resumes this WOW64 combo, scriptrun does), so the trace can reuse it
    # instead of re-attaching.
    function Resume-AttachedAndKeep {
        param([object]$Win, [object]$CmdBar, [string]$GameName = 'wotblitz')
        $game = Get-Process -Name $GameName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if (-not $game) { return $false }
        $resumeScript = Join-Path $env:TEMP 'od-wt-smoke-resume.script'
        try {
            [System.IO.File]::WriteAllText($resumeScript, "log `"ODWT_SMOKE_RESUME`"`r`nrun`r`n", (New-Object System.Text.UTF8Encoding($false)))
            Send-X64DbgCommand -CommandBar $CmdBar -Handle $Win.Handle ('scriptload "' + $resumeScript + '"')
            Start-Sleep -Milliseconds 600
            Send-X64DbgCommand -CommandBar $CmdBar -Handle $Win.Handle 'scriptrun'
        }
        catch { return $false }
        $deadline = (Get-Date).AddSeconds(30)
        $settleRounds = 0
        while ((Get-Date) -lt $deadline) {
            $settleRounds++
            $ta = $game.TotalProcessorTime
            Start-Sleep -Milliseconds 1200
            $tb = $game.TotalProcessorTime
            if ((($tb - $ta).TotalMilliseconds) -gt 0) {
                Write-Wt ('attach_smoke keep_attached resume_settle_rounds=' + $settleRounds + ' verified=True')
                return $true
            }
            Start-Sleep -Milliseconds 800
        }
        Write-Wt ('attach_smoke keep_attached resume_settle_rounds=' + $settleRounds + ' verified=False')
        return $false
    }
    try {
        if ($DryRun) {
            $probeText = if ($ProbeAddress) { 'bpm ' + $ProbeAddress + ' -> bpmc' } else { 'no probe' }
            $finish = if ($KeepAttached) { 'scriptrun-resume -> KEEP ATTACHED (trace reuses)' } else { 'detach -> verify resume' }
            Write-Wt ('attach_smoke DRYRUN would attach 0x<hex> -> pause -> verify -> ' + $probeText + ' -> ' + $finish)
            $report.smoke = 'ok'
            $report.keptAttached = $KeepAttached
            Write-SmokeReport
            return 0
        }
        $win = Get-X64DbgWindowHandle
        if (-not $win) {
            if (-not (Invoke-AutoPreArm)) {
                $report.detail = 'no_debugger_window'
                Write-SmokeReport
                return 1
            }
            $win = Wait-X64DbgWindow -TimeoutSeconds 20
            if (-not $win) {
                $report.detail = 'no_debugger_window_after_prearm'
                Write-SmokeReport
                return 1
            }
        }
        # FRESH15b: the freshly-pre-armed debugger's UIA tree lags window
        # creation (the probe campaign settled 4s before any UIA read; the
        # smoke failed finding the command bar in the same second the window
        # appeared). Settle before probing, mirroring the validated probes.
        Start-Sleep -Seconds 3
        $cmdBar = Find-X64DbgCommandBar -Handle $win.Handle
        if (-not $cmdBar) {
            $report.detail = 'no_command_bar'
            Write-SmokeReport
            return 1
        }
        $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if (-not $game) {
            $report.detail = 'no_game_process'
            Write-SmokeReport
            return 1
        }
        $report.pid = $game.Id
        $report.attachedHexPid = ('0x{0:X}' -f $game.Id)
        [WtX64Gui]::ForceForeground($win.Handle)
        Start-Sleep -Milliseconds 300
        Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle ('attach 0x{0:X}' -f $game.Id)
        Start-Sleep -Seconds 4
        Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'pause'
        # Pause proof: TotalProcessorTime must stall (~0 delta). FRESH15e:
        # in live battle x64dbg is busy right after attach (module/symbol
        # loading), so the pause can land LATE - FRESH15d showed pause=False
        # with the game still consuming CPU in the 2s+1.5s window. Poll for
        # the stall (up to 12s) instead of one-shot checking.
        $pauseDeadline = (Get-Date).AddSeconds(12)
        $report.pauseVerified = $false
        $pauseRounds = 0
        while ((Get-Date) -lt $pauseDeadline -and -not $report.pauseVerified) {
            $pauseRounds++
            $t0 = $game.TotalProcessorTime
            Start-Sleep -Milliseconds 1200
            $t1 = $game.TotalProcessorTime
            $report.pauseVerified = (($t1 - $t0).TotalMilliseconds) -lt 5
            if ($report.pauseVerified -and -not $report.pauseStartUtc) {
                $report.pauseStartUtc = ([DateTime]::UtcNow).ToString('o')
            }
            if (-not $report.pauseVerified) { Start-Sleep -Milliseconds 800 }
        }
        Write-Wt ('attach_smoke pause_settle_rounds=' + $pauseRounds + ' verified=' + $report.pauseVerified)
        if ($ProbeAddress -and $report.pauseVerified) {
            Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle ('bpm {0}, 1, w' -f $ProbeAddress)
            Start-Sleep -Milliseconds 500
            # Best-effort arm check. The UIA log-tab read is LOSSY (FRESH9:
            # returned 0 lines in clean-rig runs), so an empty read is NOT
            # proof of success - 'unverified' is the honest default. An
            # explicit 'Error executing' line means the command failed.
            $logTail = @(Read-X64DbgLog -Handle $win.Handle | Where-Object { $_ -match 'Error executing|Memory breakpoint' })
            if ($logTail -match 'Error executing') {
                $report.bpmArmed = 'no'
                $report.detail = 'bpm_arm_error: ' + ($logTail -join '; ')
            }
            elseif ($logTail -match 'Memory breakpoint') {
                $report.bpmArmed = 'yes'
            }
            # FRESH15d: `bpmc <addr>` (address form) silently FAILS to clear
            # the memory BP - the probe campaign proved the debuggee stays
            # frozen after run+detach (the write-BP re-breaks on the first
            # write, and detach-while-paused leaves it frozen). The no-arg
            # `bpmc` (clear ALL memory BPs) clears reliably and the target
            # resumes cleanly. No other memory BPs exist on the fresh
            # debugger, so clearing all is safe.
            try { Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'bpmc' }
            catch { }
        }
        # FRESH15e: detach directly, then POLL for the game to resume. The
        # probe campaign established three facts about this x64dbg/WOW64
        # combination: (1) the command-bar `run` NEVER resumes the debuggee
        # after attach+pause (0/15 attempts) so it is pointless; (2) detach
        # while paused leaves the game frozen ~2/3 of the time; (3) the freeze
        # is NOT thread suspension (DebugPort=0, threads free) and SELF-HEALS
        # after ~5-20s of WOW64 debugger-cleanup settling. So a single
        # post-detach CPU sample would false-red a healthy game; poll up to
        # 30s for it to resume, and only fail if it never comes back.
        # FRESH26 attach-once: when -KeepAttached is set, resume via scriptrun
        # (the proven resume path) and KEEP the debugger attached so the M2
        # trace reuses it - the second attach was the FRESH25 denial trigger.
        # Only skip the detach when the scriptrun resume verifies; otherwise
        # fall through to the detach path (fail-closed, game must not be left
        # frozen with a stray debugger attached).
        $keptAttachedOk = $false
        if ($KeepAttached) {
            $keptAttachedOk = Resume-AttachedAndKeep -Win $win -CmdBar $cmdBar -GameName $game.ProcessName
            if ($keptAttachedOk) {
                $report.keptAttached = $true
                $report.resumeUtc = ([DateTime]::UtcNow).ToString('o')
                $report.resumeVerified = $true
                Write-Wt ('attach_smoke kept_attached=True pid=' + $report.attachedHexPid + ' (trace will reuse this debugger)')
                $report.smoke = 'ok'
                Write-SmokeReport
                return 0
            }
            Write-Wt 'attach_smoke keep_attached resume failed - falling back to detach path'
        }
        Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'detach'
        $resumeDeadline = (Get-Date).AddSeconds(30)
        $resumeVerified = $false
        $settleRounds = 0
        while ((Get-Date) -lt $resumeDeadline -and -not $resumeVerified) {
            $settleRounds++
            $ta = $game.TotalProcessorTime
            Start-Sleep -Milliseconds 1200
            $tb = $game.TotalProcessorTime
            $resumeVerified = (($tb - $ta).TotalMilliseconds) -gt 0
            if ($resumeVerified) { $report.resumeUtc = ([DateTime]::UtcNow).ToString('o') }
            if (-not $resumeVerified) { Start-Sleep -Milliseconds 800 }
        }
        $report.resumeVerified = $resumeVerified
        Write-Wt ('attach_smoke resume_settle_rounds=' + $settleRounds + ' verified=' + $resumeVerified)
        if (-not $resumeVerified) {
            $report.detail = 'game_frozen_after_detach (no resume in 30s)'
        }
        $report.smoke = if ($report.pauseVerified -and $report.resumeVerified) { 'ok' } else { 'fail' }
        if ($report.smoke -eq 'fail' -and -not $report.detail) {
            $report.detail = 'attach_pause_detach_roundtrip_incomplete'
        }
        Write-Wt ('attach_smoke ' + $report.smoke + ' pid=' + $report.attachedHexPid +
            ' pause=' + $report.pauseVerified + ' bpm=' + $report.bpmArmed + ' resume=' + $report.resumeVerified)
        Write-SmokeReport
        if ($report.smoke -eq 'ok') { return 0 }
        return 1
    }
    catch {
        $report.detail = $_.Exception.Message
        Write-Wt ('attach_smoke THREW ' + $_.Exception.Message)
        Write-SmokeReport
        return 1
    }
}

try {
    # ---- 0. Attach-smoke mode (M2 pre-flight) - bypass everything else ----
    if ($AttachSmoke) {
        exit (Invoke-AttachSmoke -ProbeAddress $SmokeProbeAddress -ResultPath $SmokeResultPath)
    }

    # ---- 1. Resolve input mode: family (M2), solo (FRESH14), or flat survivor
    $mode = 'survivor'
    if (-not [string]::IsNullOrWhiteSpace($FamilyFile)) { $mode = 'family' }
    elseif (-not [string]::IsNullOrWhiteSpace($SoloAddress)) { $mode = 'solo' }
    # Write-breakpoint width: Float32 family members are 4 bytes, the legacy
    # Double survivors 8. -WriteSize overrides the mode default.
    $writeSize = if ($WriteSize -ne 0) { $WriteSize }
        elseif ($mode -eq 'family' -or $mode -eq 'solo') { 4 }
        else { 8 }
    if ($writeSize -ne 4 -and $writeSize -ne 8) {
        Write-Wt ('FAILED_write_size_must_be_4_or_8 got=' + $WriteSize)
        exit 2
    }

    $family = $null
    $familyAxes = @()
    $armed = @()
    $unarmed = @()
    if ($mode -eq 'family' -or $mode -eq 'solo') {
        # Family input: an od-048 correlate report JSON (its `families`
        # array) or a bare family JSON object with `members`. Solo input
        # (FRESH14): -SoloAddress synthesizes a single-member family object
        # (with -SoloAxis/-SoloScore/-SoloBandMinSeconds/-SoloBandMaxSeconds
        # when the caller has correlation evidence) and runs it through the
        # SAME floors, so a lone tight-band survivor like FRESH12's
        # 0x1FC57238 - structurally excluded from every real family because
        # its +/-16-byte neighbors scored below the seed floor - can be armed
        # without a family file. A bare address with no score/band is refused
        # fail-closed unless the caller disables the floors (direct
        # investigation).
        $families = @()
        if ($mode -eq 'solo') {
            $soloToken = ConvertTo-HexToken $SoloAddress
            if (-not $soloToken) {
                Write-Wt ('FAILED_solo_address_invalid=' + $SoloAddress)
                exit 2
            }
            $bandMin = $null
            $bandMax = $null
            if ($SoloBandMinSeconds -ne [double]::MinValue) { $bandMin = $SoloBandMinSeconds }
            if ($SoloBandMaxSeconds -ne [double]::MinValue) { $bandMax = $SoloBandMaxSeconds }
            $soloMember = [pscustomobject]@{
                address             = $soloToken
                offsetBytes         = 0
                axis                = $SoloAxis
                # sign/shiftSeconds are correlation evidence the operator did
                # NOT provide in solo mode - leave them null so the evidence
                # report cannot imply a verdict (evidence-first: unknown stays
                # unknown; never guess). Get-FamilyArmPlan only consumes
                # address/offsetBytes.
                sign                = $null
                shiftSeconds        = $null
                shiftBandMinSeconds = $bandMin
                shiftBandMaxSeconds = $bandMax
                score               = $SoloScore
                edgeAligned         = $false
            }
            $families = @([pscustomobject]@{
                baseAddress = $soloToken
                spanBytes   = 0
                axesCovered = @($SoloAxis)
                complete    = $false
                solo        = $true
                members     = @($soloMember)
            })
            # No address on stdout (privacy rule: addresses never enter stdout;
            # they go to the local evidence file). The axis/score/band describe
            # the arm without leaking the address.
            Write-Wt ('solo axis=' + $SoloAxis + ' score=' + $SoloScore +
                ' band=[' + $(if ($null -eq $bandMin) { 'unknown' } else { $bandMin }) + ',' +
                $(if ($null -eq $bandMax) { 'unknown' } else { $bandMax }) + ']')
        }
        else {
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
            if ($null -ne $familyDoc -and $familyDoc.PSObject.Properties['families']) {
                $families = @($familyDoc.families)
            }
            elseif ($null -ne $familyDoc -and $familyDoc.PSObject.Properties['members']) {
                $families = @($familyDoc)   # bare family object
            }
        }
        if ($families.Count -eq 0) {
            Write-Wt 'FAILED_family_file_no_families'
            exit 2
        }
        $family = Select-BestFamily -Families $families
        if ($null -eq $family) {
            # No family cleared BOTH gates: a below-floor member is noise
            # (FRESH10: x@0.20 armed alongside y@1.00 -> the trace burned the
            # window on the noise member) or a member's ambiguity band is
            # missing/too wide (FRESH13: a degenerate member matches at any
            # shift regardless of score). Arming nothing is the evidence-first
            # outcome: a family that cannot clear both floors would only
            # produce a no-hit window.
            Write-Wt ('FAILED_family_selection no_family_clears_floors min_score=' + $MinMemberScore + ' max_band_s=' + $MaxMemberBandSeconds)
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
    # Memory write breakpoints (bpm <addr>, 1, w: guard page, ANY thread)
    # instead of hardware bph (active-thread-only, provably misses after
    # attach). No fast-resume, no break condition: the engine source skips
    # log+command when fastResume && condition==0, and a non-zero condition
    # breaks the debuggee mid-window. The default (no condition) executes
    # log + command on each hit.
    #
    # savedata string-formats its filename arg (stringformatinline in
    # cmd-memory-operations.cpp), but {rip} inside the savedata filename was
    # NOT reliable across builds in the FRESH9 probe campaign - so the
    # command uses a STATIC per-address file (the one shape proven to
    # produce hit files) and the RIP comes from the SetMemoryBreakpointLog
    # line, harvested from the Log tab after the window.
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
        [void]$scriptLines.Add(('bpm {0}, 1, w' -f $a))
        [void]$scriptLines.Add(('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $a))
        $hitFile = Join-Path $HitsDir ('odwt-{0}.bin' -f $a)
        [void]$scriptLines.Add(('SetMemoryBreakpointCommand {0}, "savedata {1}, {0}, 4"' -f $a, $hitFile))
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
    if (($mode -eq 'family' -or $mode -eq 'solo') -and $AutoWriteTrace -and -not $SkipLivenessCheck -and -not $SkipGateCheck) {
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
    $win = Wait-X64DbgWindow -TimeoutSeconds $WindowWaitSeconds
    if (-not $win) {
        # Diagnostics: is the debugger process alive, responding, and does it
        # have ANY top-level windows? Logging the titles distinguishes a
        # window-lag race from a hung/attached-but-windowless state.
        $dbg = Get-X64DbgProcess
        if ($dbg) {
            $titles = [WtX64Gui]::WindowTitles([uint32]$dbg.Id)
            Write-Wt ('FAILED_x64dbg_no_window pid=' + $dbg.Id + ' responding=' + $dbg.Responding + ' windows="' + $titles + '"')
        }
        else {
            Write-Wt 'FAILED_x64dbg_not_running_run_pre-arm_debugger_first'
        }
        # FRESH26: -ReuseAttached was set (smoke kept its debugger) but no
        # debugger window is here - the smoke's x32dbg died or was closed in
        # the gap. Degrade to a fresh pre-arm + attach instead of failing the
        # window (a second attach on a debugger-free process is the SAFE
        # path; the freeze only bites when a second debugger grabs an already-
        # attached process). Fall through so the attach step re-arms.
        if ($ReuseAttached) {
            Write-Wt 'reuse_attached_debugger_gone - falling back to fresh pre-arm + attach'
            if (-not (Invoke-AutoPreArm)) {
                Write-Wt 'FAILED_prearm_on_reuse_fallback'
                exit 3
            }
            $win = Wait-X64DbgWindow -TimeoutSeconds $WindowWaitSeconds
            if (-not $win) {
                Write-Wt 'FAILED_x64dbg_no_window_after_reuse_fallback'
                exit 3
            }
            Write-Wt ("x64dbg_pid=" + $win.Id + ' (fresh fallback)')
            $doReuse = $false
        }
        else {
            exit 3
        }
    }
    else {
        Write-Wt ("x64dbg_pid=" + $win.Id)
        $doReuse = $ReuseAttached
    }
    if (-not $PSBoundParameters.ContainsKey('ReuseAttached')) {
        $doReuse = $false
    }
    if ($null -eq $doReuse) { $doReuse = $false }

    # ---- 5. Attach + pause, then inject scriptload + scriptrun -------------
    # x64dbg parses integer literals as HEX: `attach <decimal>` silently
    # targets a nonexistent pid (0x42284 != 42284) - the FRESH9 zero-hit
    # root cause. The command-bar attach does not pause the debuggee
    # (pauseAtAttach is script-only), so `pause` follows; scriptrun refuses
    # while the debuggee runs. The command bar is driven via UI Automation
    # ValuePattern + PostMessage ENTER (SendKeys is IME-mangled).
    $cmdBar = Find-X64DbgCommandBar -Handle $win.Handle
    if (-not $cmdBar) {
        Write-Wt 'FAILED_no_command_bar (x64dbg command bar not exposed via UIA)'
        exit 4
    }
    $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    if (-not $game) {
        Write-Wt 'FAILED_no_game_process_to_attach (launch the replay first)'
        exit 4
    }
    [WtX64Gui]::ForceForeground($win.Handle)
    Start-Sleep -Milliseconds 300
    # FRESH26 attach-once: when the smoke left its debugger attached, reuse
    # it - skip the `attach` command entirely (the debugger already owns the
    # process; a second attach is what froze the game and denied the gate in
    # FRESH25). The `pause` below still runs (scriptrun requires the debuggee
    # paused). Fail-closed: -ReuseAttached is only set by od-048 when the
    # smoke report says keptAttached=true, so a mismatch here means the
    # debugger is NOT attached - refuse rather than scriptrun into an
    # unattached debugger.
    if ($doReuse) {
        Write-Wt ('reused_attached_debugger pid=0x{0:X} (no second attach)' -f $game.Id)
    }
    else {
        Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle ('attach 0x{0:X}' -f $game.Id)
        Start-Sleep -Seconds 4
    }
    Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'pause'
    Start-Sleep -Seconds 2
    Write-Wt ('attached pid=0x{0:X}' -f $game.Id)

    Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle ('scriptload "' + $ScriptFile + '"')
    Start-Sleep -Milliseconds 600
    Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'scriptrun'
    Write-Wt 'injected scriptload+scriptrun'

    # FRESH23/24: CPU-liveness discriminator. The smoke verifies resume
    # (resume_settle_rounds verified=True) but the trace never did -- the
    # known WOW64 attach-freeze (~1/3 of runs) can leave the "window" frozen,
    # so a family-no-hit reads as "not written" when the game never executed.
    # A running game burns ~1-2 cores at 60fps; a frozen debuggee burns ~0.
    $cpuWindowStart = $null
    try { $cpuWindowStart = [double]$game.TotalProcessorTime.TotalMilliseconds } catch { }
    # FRESH24/25 value-liveness: snapshot the armed addresses' values at
    # window start; compare with the post-window read in section 6a.
    $valsStart = Read-FamilyValues -Addresses $armed

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
            elseif ($f.Name -match '^odwt-0x([0-9a-fA-F]+)\.bin$') {
                # Static savedata proof file (the proven capture shape).
                $hitKey = ('0x' + $Matches[1] + ' savedata')
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

    # ---- 6a. Window liveness verdict ---------------------------------------
    # Sample the game's CPU time across the window: running => the game
    # executed while the BPs were armed (a no-hit is then a REAL no-write);
    # frozen => the attach/resume failed and the window was an artifact.
    $cpuWindowEnd = $null
    try { $cpuWindowEnd = [double]$game.TotalProcessorTime.TotalMilliseconds } catch { }
    $cpuDeltaMs = $null
    $windowLiveness = 'unknown'
    if ($null -ne $cpuWindowStart -and $null -ne $cpuWindowEnd) {
        $cpuDeltaMs = [int64]($cpuWindowEnd - $cpuWindowStart)
        # >= 50ms of CPU per second of window = the game was executing
        # (a frozen debuggee consumes ~0; even a debugger-busy pause stays
        # far below this).
        $windowLiveness = if ($cpuDeltaMs -ge ([int64]($TraceSeconds * 50))) { 'running' } else { 'frozen' }
    }
    # FRESH24/25 value-liveness: did the BATTLE WORLD advance across the
    # window? A playing replay moves the armed z addresses (the tank drives
    # ~20+ units per 25s); a paused/roster replay renders (CPU burns) but the
    # world is frozen. Threshold 0.5 units on ANY armed address.
    $windowValuesChanged = 'unknown'
    $maxValueDelta = $null
    $valsEnd = Read-FamilyValues -Addresses $armed
    if ($null -ne $valsStart -and $null -ne $valsEnd) {
        $maxDelta = 0.0
        foreach ($a in $armed) {
            $k = ([string]$a).ToLowerInvariant()
            if ($valsStart.ContainsKey($k) -and $valsEnd.ContainsKey($k)) {
                $d = [Math]::Abs($valsEnd[$k] - $valsStart[$k])
                if ($d -gt $maxDelta) { $maxDelta = $d }
            }
        }
        $maxValueDelta = [Math]::Round($maxDelta, 2)
        $windowValuesChanged = if ($maxDelta -ge 0.5) { 'true' } else { 'false' }
    }
    Write-Wt ('window_cpu_delta_ms=' + $(if ($null -eq $cpuDeltaMs) { 'unknown' } else { $cpuDeltaMs }) + ' liveness=' + $windowLiveness + ' values_changed=' + $windowValuesChanged + $(if ($null -ne $maxValueDelta) { ' max_delta=' + $maxValueDelta } else { '' }))

    # ---- 6b. Release the debuggee ------------------------------------------
    # The memory BP pauses the game on its first hit (breakCondition defaults
    # to 1 -> break -> wait), so the trace captures one proof file per armed
    # address and then holds the game paused. Detach to resume it cleanly.
    try {
        Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.Handle 'detach'
        Start-Sleep -Milliseconds 800
        Write-Wt 'released_detach'
    }
    catch {
        Write-Wt 'WARN_release_detach_failed'
    }

    # ---- 7. Write evidence + report ----------------------------------------
    # Harvest the Log tab for ODWT_HIT lines: logText is formatted with
    # stringformatinline (proven in the engine source), so each line carries
    # the substituted {rip} - the writing instruction address (the M2
    # write-site evidence). The static savedata files prove the write
    # occurred per armed address even if the log harvest misses.
    $harvested = @()
    try {
        foreach ($ln in @(Read-X64DbgLog -Handle $win.Handle)) {
            if ($ln -match 'ODWT_HIT addr=(0x[0-9a-fA-F]+) rip=(0x[0-9a-fA-F]+)') {
                $harvested += ($Matches[1] + ' ' + $Matches[2])
            }
        }
    }
    catch { Write-Wt 'WARN_log_harvest_failed' }
    Write-Wt ('log_harvest_hits=' + $harvested.Count)
    foreach ($h in $harvested) {
        if ($hits -notcontains $h) { $hits += $h }
    }

    # Static savedata proof files: odwt-0x<addr>.bin. A write that the log
    # harvest missed is still counted (rip unknown, marked 'savedata').
    $proofAddrs = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $HitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)) {
        if ($f.Name -match '^odwt-0x([0-9a-fA-F]+)\.bin$') {
            $proofAddrs += ('0x' + $Matches[1])
        }
    }
    foreach ($pa in $proofAddrs) {
        if (-not ($hits | Where-Object { $_ -like ($pa + ' *') })) { $hits += ($pa + ' savedata') }
    }

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
            # Normalize the member's ambiguity band (either wire pair) to the
            # report's canonical shiftBandMin/MaxSeconds so a re-run against
            # this .family.json can re-apply the band floor.
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
                address     = $addr
                offsetBytes = if ($m.PSObject.Properties['offsetBytes'] -and $null -ne $m.offsetBytes) { [int]$m.offsetBytes } else { 0 }
                axis        = if ($m.PSObject.Properties['axis']) { [string]$m.axis } else { '?' }
                score       = if ($m.PSObject.Properties['score'] -and $null -ne $m.score) { [double]$m.score } else { 0.0 }
                shiftBandMinSeconds = $bandMin
                shiftBandMaxSeconds = $bandMax
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
            # FRESH23/24: liveness of the window -- 'frozen' means the
            # attach/resume failed and the no-hit is an artifact, not a real
            # absence of writes.
            windowLiveness = $windowLiveness
            windowCpuDeltaMs = $cpuDeltaMs
            # FRESH24/25: whether the battle world advanced across the window
            # (armed-address values moved). 'false' with liveness=running =
            # paused/roster replay: renders but writes nothing.
            windowValuesChanged = $windowValuesChanged
            windowMaxValueDelta = $maxValueDelta
            verdict      = $familyVerdict
            members      = $memberEntries
        }
        $familyResultPath = $ResultPath + '.family.json'
        $familyJson = $familyReport | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($familyResultPath, $familyJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Wt ('family_verdict=' + $familyVerdict + ' hit_members=' + $hitMembers.Count + ' liveness=' + $windowLiveness + ' values_changed=' + $windowValuesChanged)
        Write-Wt ('family_report=' + $familyResultPath)
    }

    Write-Wt 'OK trace_window_completed'
    exit 0
}
catch {
    Write-Wt ("FAILED_unexpected=" + $_.Exception.Message)
    exit 6
}
