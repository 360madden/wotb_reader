# Drive x32dbg's command bar via UI Automation ValuePattern (no SendKeys -
# immune to IME/focus). Flow: launch plain -> UIA-set 'attach <pid>' -> verify
# PAUSED -> UIA-set scriptload/scriptrun of the full trace script -> verify
# resume + hits + sentinel. Reads the log via UIA after each step.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-probe-hits'
$sentinelFile = Join-Path $T 'od-wt-probe-done.bin'
Remove-Item -LiteralPath $addrFile, $progressFile, $sentinelFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# ---- 0. Counter target ---------------------------------------------------
$csFile = Join-Path $T 'wt-counter-target.cs'
if (-not (Test-Path -LiteralPath $exe)) {
    $src = @'
using System;
using System.IO;
using System.Threading;
public static class CounterTarget {
    public static unsafe void Main() {
        int* p = stackalloc int[1];
        *p = 0;
        File.WriteAllText(@"ADDRFILE", ((long)p).ToString("X8"));
        long n = 0;
        while (true) {
            (*p)++;
            n++;
            if ((n % 40) == 0) File.WriteAllText(@"PROGRESS", (*p).ToString());
            Thread.Sleep(25);
        }
    }
}
'@
    $src = $src.Replace('ADDRFILE', $addrFile).Replace('PROGRESS', $progressFile)
    Set-Content -LiteralPath $csFile -Value $src -Encoding ascii
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    & $csc /nologo /target:exe /platform:x86 /unsafe /out:$exe $csFile 2>&1 | Out-Null
}
$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR_FILE'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

# ---- UIA helpers ---------------------------------------------------------
function Get-Root([IntPtr]$hwnd) {
    return [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
}
function Get-AllText([IntPtr]$hwnd) {
    $root = Get-Root $hwnd
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($n)) { $parts.Add($n) }
    }
    return ($parts -join ' | ')
}
function Get-LogText([IntPtr]$hwnd) {
    # The log tab content: find elements that look like log lines (contain
    # known engine markers) or the biggest text block after "Log".
    $txt = Get-AllText $hwnd
    return $txt
}
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe5 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    public static void ForceForeground(IntPtr hWnd) {
        uint unused; uint target = GetWindowThreadProcessId(hWnd, out unused);
        uint current = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow(); uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out unused) : 0;
        if (fgThread != 0) AttachThreadInput(current, fgThread, true);
        if (target != 0) AttachThreadInput(current, target, true);
        SetForegroundWindow(hWnd);
        if (target != 0) AttachThreadInput(current, target, false);
        if (fgThread != 0) AttachThreadInput(current, fgThread, false);
    }
}
"@
function Send-Command([IntPtr]$hwnd, [string]$cmd) {
    $root = Get-Root $hwnd
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    $bar = $null
    foreach ($e in $edits) {
        # The x32dbg command line is a Qt CommandLineEdit with an EMPTY name.
        if ($e.Current.ClassName -eq 'CommandLineEdit') { $bar = $e; break }
    }
    if (-not $bar) { return '<no-cmdbar>' }
    $vp = $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($cmd)
    # SendKeys {ENTER} goes to the FOREGROUND window, so make x32dbg active
    # first or the command never executes.
    [WtX64Probe5]::ForceForeground($hwnd)
    Start-Sleep -Milliseconds 200
    $bar.SetFocus()
    Start-Sleep -Milliseconds 150
    $wshell = New-Object -ComObject WScript.Shell
    $null = $wshell.SendKeys('{ENTER}')
    Start-Sleep -Milliseconds 700
    return 'sent'
}

# ---- 1. Launch plain x32dbg ----------------------------------------------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Write-Host ("window pid=" + $win.Id)
Start-Sleep -Seconds 3

# ---- 2. Marker test ------------------------------------------------------
Send-Command $win.MainWindowHandle 'log "ODWT_MARKER_3C88"'
$txt = Get-LogText $win.MainWindowHandle
Write-Host ("marker_in_log=" + ($txt -match 'ODWT_MARKER_3C88'))

# ---- 3. Attach via UIA command (pid MUST be hex - x64dbg parses all
# integer constants as hex; a decimal pid becomes a huge nonexistent pid)
Send-Command $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
$txt = Get-LogText $win.MainWindowHandle
Write-Host ("attach_log_matches=" + (($txt | Select-String -Pattern 'attach|Attached|Attach|debug' -AllMatches).Matches.Value -join ','))
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
if ($p1 -eq $p2) { Write-Host ("ATTACH_PAUSED progress=" + $p1) }
else { Write-Host ("ATTACH_NOT_PAUSED progress " + $p1 + " -> " + $p2) }

# ---- 4. Run the full trace script -----------------------------------------
$scriptFile = Join-Path $T 'od-wt-uia.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
@(
    ("bph " + $addr + ",w,4"),
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetHardwareBreakpointFastResume ' + $addr),
    ('log "ODWT_ARMED count=1"'),
    ('run'),
    ('savedata "' + $sentinelFile + '", ' + $addr + ', 4')
) | Set-Content -LiteralPath $scriptFile -Encoding ascii
Send-Command $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Send-Command $win.MainWindowHandle 'scriptrun'

Start-Sleep -Seconds 3
$progA = Get-Progress
Start-Sleep -Seconds 4
$progB = Get-Progress
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile

Write-Host ("progress_a=" + $progA + " progress_b=" + $progB)
if ($progB -gt $progA) { Write-Host ('RESUME_OK counter advancing (+' + ($progB - $progA) + ')') }
else { Write-Host 'RESUME_FAIL counter frozen' }
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ("  hit_file=" + $h.Name + " size=" + $h.Length) }
Write-Host ("sentinel_present=" + $sentinel)

$finalLog = Get-LogText $win.MainWindowHandle
$interesting = @(($finalLog -split ' \| ') | Where-Object { $_ -match 'ODWT_|error|Error|failed|Failed|breakpoint|Breakpoint|script|Script|attached|Attached|savedata|run' } | Select-Object -First 20)
Write-Host '=== LOG LINES OF INTEREST ==='
$interesting | ForEach-Object { Write-Host ('  ' + $_) }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
