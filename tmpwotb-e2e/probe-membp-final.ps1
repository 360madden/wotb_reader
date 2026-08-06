# Memory-breakpoint capture path: command-bar attach (proven), then a script
# that sets bpm (write, restore) + log + command + condition-0 + run, bisected
# with savedata sentinels. Memory BPs fire on ANY thread (no DR/thread issue).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$tidFile = Join-Path $T 'wt-counter-tid.txt'
$hitsDir = Join-Path $T 'od-wt-membp-hits'
$sentDir = Join-Path $T 'od-wt-membp-sent'
$scriptFile = Join-Path $T 'od-wt-membp.script'
New-Item -ItemType Directory -Force -Path $hitsDir, $sentDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile, $tidFile, $scriptFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $sentDir 'S*.bin') -ErrorAction SilentlyContinue

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64ProbeMbp {
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

function Get-Log($root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -match 'ODWT_|Error|error|Attach|attach|breakpoint|Breakpoint|savedata|Unable|Invalid|script|Script|exception|Exception|set!|SET') {
            $lines.Add($n)
        }
    }
    return $lines
}

function Get-CmdLineEdit($root) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    foreach ($e in $edits) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Send-Command($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { return '<no-cmdbar>' }
    $vp = $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [WtX64ProbeMbp]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}

# ---- 0. Counter -----------------------------------------------------------
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host 'MISSING_COUNTER_EXE'; exit 1
}
$target = Start-Process -FilePath $exe -PassThru
$addr = $null; $mainTid = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim() }
    if (Test-Path -LiteralPath $tidFile) { $mainTid = (Get-Content -LiteralPath $tidFile -Raw).Trim() }
    if ($addr -and $mainTid) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

# ---- 1. x32dbg + UIA + command-bar attach ---------------------------------
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
$engineLog = Join-Path $hitsDir 'engine.log'
Send-Command $root $win.MainWindowHandle ('setlogfile "' + $engineLog + '"')
Start-Sleep -Seconds 1
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Write-Host '=== ENGINE LOG AFTER ATTACH ==='
if (Test-Path -LiteralPath $engineLog) { Get-Content -LiteralPath $engineLog | Select-Object -Last 10 | ForEach-Object { Write-Host ('  ' + $_) } }
# The command-bar attach does NOT pause the debuggee (pauseAtAttach is
# script-only); scriptrun refuses while the debuggee is running. `pause`
# targets the main thread via dwAttachMainThread (handles the breakin-thread
# issue) and leaves the debuggee paused for the script.
Send-Command $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2
Write-Host 'attached_and_paused_via_cmdbar'

# ---- 2. Memory-BP script, bisected with sentinels --------------------------
$logFile = Join-Path $hitsDir 'odwt-log.txt'
Remove-Item -LiteralPath $logFile -ErrorAction SilentlyContinue
function Chk([int]$n) { return ('savedata ' + (Join-Path $sentDir ('S' + $n + '.bin')) + ', ' + $addr + ', 4') }
$scriptLines = @(
    (Chk 1),
    ('bpm {0}, 1, w' -f $addr),
    (Chk 2),
    ('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    (Chk 3),
    ('SetBreakpointLogFile {0}, "{1}"' -f $addr, $logFile),
    (Chk 4),
    ('SetMemoryBreakpointCondition {0}, 0' -f $addr),
    (Chk 5),
    'log "ODWT_ARMED count=1"',
    'run'
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host '=== SCRIPT ==='
$scriptLines | ForEach-Object { Write-Host ('  ' + $_) }

$pBefore = Get-Progress
$logTab = Activate-LogTab $root
Write-Host ("log_tab_activated=" + $logTab)
Start-Sleep -Seconds 1
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 8
Write-Host '=== ENGINE LOG (after trace) ==='
if (Test-Path -LiteralPath $engineLog) { Get-Content -LiteralPath $engineLog | Select-Object -Last 25 | ForEach-Object { Write-Host ('  ' + $_) } }

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter '*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ('  hit_file=' + $h.Name + ' size=' + $h.Length) }
Write-Host '=== SENTINELS ==='
for ($i = 1; $i -le 5; $i++) {
    $p = Join-Path $sentDir ('S' + $i + '.bin')
    Write-Host ('  S' + $i + '=' + (Test-Path -LiteralPath $p))
}
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
Write-Host ("progress " + $p1 + " -> " + $p2 + " advancing=" + ($p2 -gt $p1))

Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-Command $root $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
