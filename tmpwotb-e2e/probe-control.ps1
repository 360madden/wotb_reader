# Control + ground-truth probe: (A) attach overhead with NO breakpoint,
# (B) memory-BP arm with condition-0 and the `bl` breakpoint-list dump.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-control-hits'
$sentDir = Join-Path $T 'od-wt-control-sent'
New-Item -ItemType Directory -Force -Path $hitsDir, $sentDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $hitsDir -Filter '*.bin' -ErrorAction SilentlyContinue | Remove-Item
Get-ChildItem -LiteralPath $hitsDir -Filter '*.txt' -ErrorAction SilentlyContinue | Remove-Item
Get-ChildItem -LiteralPath $sentDir -Filter 'S*.bin' -ErrorAction SilentlyContinue | Remove-Item

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Ctrl {
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

function Get-CmdLineEdit($root) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    foreach ($e in $edits) { if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e } }
    return $null
}
function Activate-LogTab($root) {
    $tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
    foreach ($t in $tabs) {
        if ($t.Current.Name -eq 'Log') {
            try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
            try { $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { }
            return $false
        }
    }
    return $false
}
function Send-Command($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { return '<no-cmdbar>' }
    $vp = $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [WtX64Ctrl]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}
function Get-Log($root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -and $n.Length -gt 2 -and $n -notmatch '^(Log|Breakpoints|Script|CPU|Symbols|Call Stack|Memory Map|References|Handles|Threads|Source|Trace|Watch|Notes|Data|Graph|Command)$') {
            $lines.Add($n)
        }
    }
    return $lines
}

# ---- 0. Counter ----------------------------------------------------------
if (-not (Test-Path -LiteralPath $exe)) { Write-Host 'MISSING_COUNTER_EXE'; exit 1 }
$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }
$base1 = Get-Progress; Start-Sleep -Seconds 1; $base2 = Get-Progress
Write-Host ("baseline_progress " + $base1 + " -> " + $base2)

# ---- 1. x32dbg + attach ---------------------------------------------------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Start-Sleep -Seconds 4
$root = $null
for ($i = 0; $i -lt 10 -and -not $root; $i++) {
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) }
    catch { Start-Sleep -Milliseconds 800 }
}
if (-not $root) { Write-Host 'NO_UIA_ROOT'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
$logTab = Activate-LogTab $root
Write-Host ("log_tab_activated=" + $logTab)
Start-Sleep -Seconds 1
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Send-Command $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2
$p1 = Get-Progress
Write-Host '=== LOG AFTER ATTACH+PAUSE ==='
(Get-Log $root) | Select-Object -Last 8 | ForEach-Object { Write-Host ('  ' + $_) }

# ---- A. CONTROL: scriptrun with ONLY run (no breakpoints) -----------------
$ctrlScript = Join-Path $hitsDir 'ctrl.script'
@('log "CTRL_ARMED"', 'run') | Set-Content -LiteralPath $ctrlScript -Encoding ascii
Send-Command $root $win.MainWindowHandle ('scriptload "' + $ctrlScript + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 5
$c1 = Get-Progress; Start-Sleep -Seconds 1; $c2 = Get-Progress
Write-Host ("CONTROL_progress " + $c1 + " -> " + $c2 + " rate_per_s=" + ($c2 - $c1))
Send-Command $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2

# ---- B. MEMBP with condition-0 + log + bl dump -----------------------------
$logFile = Join-Path $hitsDir 'odwt-log.txt'
function Chk([int]$n) { return ('savedata ' + (Join-Path $sentDir ('S' + $n + '.bin')) + ', ' + $addr + ', 4') }
$membpScript = Join-Path $hitsDir 'membp.script'
@(
    (Chk 1),
    ('bpm {0}, 1, w' -f $addr),
    (Chk 2),
    ('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    (Chk 3),
    ('SetBreakpointLogFile {0}, "{1}"' -f $addr, $logFile),
    (Chk 4),
    ('SetMemoryBreakpointCondition {0}, 0' -f $addr),
    (Chk 5),
    'log "MEMBP_ARMED"',
    'run'
) | Set-Content -LiteralPath $membpScript -Encoding ascii
Send-Command $root $win.MainWindowHandle ('scriptload "' + $membpScript + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 6
Send-Command $root $win.MainWindowHandle 'bl'
Start-Sleep -Seconds 2
$m1 = Get-Progress; Start-Sleep -Seconds 1; $m2 = Get-Progress
Write-Host ("MEMBP_progress " + $m1 + " -> " + $m2 + " rate_per_s=" + ($m2 - $m1))
Write-Host ("logfile_exists=" + (Test-Path -LiteralPath $logFile))
if (Test-Path -LiteralPath $logFile) { Write-Host ("logfile_lines=" + (Get-Content -LiteralPath $logFile).Count) }
Write-Host '=== LOG AFTER MEMBP + BL ==='
(Get-Log $root) | Select-Object -Last 30 | ForEach-Object { Write-Host ('  ' + $_) }
for ($i = 1; $i -le 5; $i++) { $p = Join-Path $sentDir ('S' + $i + '.bin'); Write-Host ('  S' + $i + '=' + (Test-Path -LiteralPath $p)) }

Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-Command $root $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
