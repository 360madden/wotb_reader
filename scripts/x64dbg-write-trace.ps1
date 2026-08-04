#Requires -Version 5.1
<#
.SYNOPSIS
  Automated x64dbg hardware write-breakpoint write-trace (operator step optional).

.DESCRIPTION
  Replaces the operator's interactive Find-what-writes step inside the held
  green window (OD-044 Track C2 pilot):

  1. Reads the rolling driver's staged survivors (%TEMP%\od-survivors.txt,
     one absolute hex address per line, 0x prefix, 8-byte aligned Double
     addresses).
  2. Generates an x64dbg script that arms a hardware WRITE breakpoint
     (bph <addr>,w,8) on up to 4 addresses (the DR0-DR3 x64 limit), sets a
     bphwlog line that string-formats {rip} on hit, sets a breakpoint command
     that runs savedata - and savedata string-formats its own filename arg
     (stringformatinline in cmd-memory-operations.cpp), so each hit writes
     <HitsDir>\odwt-0x<addr>-<rip>.bin containing 64 bytes of memory at RIP
     (the writing instruction bytes). The hit filename is the automatable
     evidence channel - no GUI scraping required. Fast-resume keeps the
     replay playing so all hits in the window are captured.
  3. Injects `scriptload "<file>"` then `scriptrun` into the RUNNING x64dbg
     command bar via GUI automation (activate window, click the command bar,
     SendKeys). x64dbg's CLI only supports `-p <pid>` attach - there is no
     script-execution flag (verified against help.x64dbg.com Commandline
     docs), so command-bar injection is the only no-new-install path.
  4. Polls the hits dir + the Host gate for -TraceSeconds; stops early on
     gate loss. Writes captured addr -> rip evidence to -ResultPath (local,
     untracked). Prints aggregate counts only on stdout (privacy rule:
     addresses never enter the repo or stdout; they go to the local file).

  Cap: only 4 hardware breakpoints exist on x64 (DR0-DR3). If the survivor
  file has more than 4 addresses, the first 4 are armed and the rest are
  reported as unarmed (matches the CE-era "armed 4, skipped beyond limit"
  sessions).

  Requires the pre-armed x64dbg (scripts/pre-arm-debugger.ps1 -AutoAttach)
  or a running x64dbg attached to wotblitz.exe. Use -DryRun to generate the
  script + evidence plan without touching the debugger.

.EXITCODES
  0  Success - trace window completed (hits captured, or clean no-hit window, gate held)
  2  Survivor file missing / empty / unparseable
  3  x64dbg not found / not running
  4  Command-bar injection failed (window/click/send)
  5  Gate lost during the trace window
  6  Unexpected error
#>
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
    [switch]$SkipGateCheck
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
    # ---- 1. Load + validate survivors --------------------------------------
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
    Write-Wt ("survivors_total=" + $addrs.Count + " armed=" + $armed.Count + " dr_limit=4")
    if ($addrs.Count -gt 4) {
        Write-Wt ("WARN_unarmed_beyond_dr_limit skipped=" + ($addrs.Count - 4))
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
        [void]$scriptLines.Add(('bph {0},w,8' -f $a))
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
        Write-Wt ("DRYRUN hitsdir=" + $HitsDir)
        Write-Wt ('DRYRUN script=' + ($scriptLines -join ' | '))
        # DryRun still emits the (empty) result file so callers can rely on it.
        Set-Content -LiteralPath $ResultPath -Value '' -Encoding ascii
        Write-Wt 'OK dryrun'
        exit 0
    }

    # ---- 3. Locate x64dbg + ensure attached --------------------------------
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

    # ---- 4. Inject scriptload + scriptrun into the command bar -------------
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

    # ---- 5. Poll hits + gate for the trace window --------------------------
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
            $lastAnnounce = Get-Date
        }
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    # ---- 6. Write evidence + report ----------------------------------------
    Set-Content -LiteralPath $ResultPath -Value $hits -Encoding ascii
    Write-Wt ("hits=" + $hits.Count)
    Write-Wt 'OK trace_window_completed'
    exit 0
}
catch {
    Write-Wt ("FAILED_unexpected=" + $_.Exception.Message)
    exit 6
}
