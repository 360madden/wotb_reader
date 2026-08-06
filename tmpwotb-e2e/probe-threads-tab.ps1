# Ground truth on the x64dbg thread list after attach: read the Threads tab
# via UIA (which threads exist), then switchthread to the main thread and
# test whether bph captures writes on it.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$tidFile = Join-Path $T 'wt-counter-tid.txt'
$hitsDir = Join-Path $T 'od-wt-threads-hits'
$scriptFile = Join-Path $T 'od-wt-threads.script'
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile, $tidFile, $scriptFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64ProbeThr {
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
    foreach ($e in $edits) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Activate-Tab($root, [string]$name) {
    $tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
    foreach ($t in $tabs) {
        if ($t.Current.Name -eq $name) {
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
    [WtX64ProbeThr]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}
function Dump-ThreadsTab($root) {
    # The Threads tab is a QTableView: rows of cells. Dump row-by-row cell text.
    $out = New-Object System.Collections.Generic.List[string]
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $rows = @{}
    foreach ($el in $all) {
        $n = $el.Current.Name
        if (-not $n -or $n.Trim().Length -eq 0) { continue }
        # A table row exposes its cells as descendants; collect by parent chain is
        # unreliable, so dump every text-bearing element in the tab order.
        if ($el.Current.ClassName -match 'QModelIndex|QTableWidget|Cell|View|ModelIndex') { continue }
        $out.Add(($n -replace '\s+', ' '))
    }
    return $out
}

# ---- 0. Counter -----------------------------------------------------------
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host 'MISSING_COUNTER_EXE - run probe-final-s-cli.ps1 once first'; exit 1
}
$target = Start-Process -FilePath $exe -PassThru
$addr = $null; $mainTid = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim() }
    if (Test-Path -LiteralPath $tidFile) { $mainTid = (Get-Content -LiteralPath $tidFile -Raw).Trim() }
    if ($addr -and $mainTid) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr -or -not $mainTid) { Write-Host 'NO_ADDR_OR_TID'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " addr=" + $addr + " true_main_tid=" + $mainTid)
$dotnetTids = @($target.Threads | ForEach-Object { $_.Id })
Write-Host ('dotnet_threads=' + ($dotnetTids -join ','))
Start-Sleep -Milliseconds 800

# ---- 1. x32dbg + UIA ------------------------------------------------------
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
foreach ($m in @('ODWT_T1_AA11', 'ODWT_T2_BB22')) {
    Send-Command $root $win.MainWindowHandle ('log "' + $m + '"')
}
Write-Host 'channel_markers_sent'

# ---- 2. Command-bar attach (proven path) ----------------------------------
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 5

# ---- 3. Read the Threads tab ----------------------------------------------
$activated = Activate-Tab $root 'Threads'
Write-Host ("threads_tab_activated=" + $activated)
Start-Sleep -Seconds 1
$tabContent = Dump-ThreadsTab $root
Write-Host '=== THREADS TAB ELEMENTS ==='
($tabContent | Select-Object -Last 40) | ForEach-Object { Write-Host ('  ' + $_) }

# ---- 4. switchthread to the true main tid via command bar ------------------
Send-Command $root $win.MainWindowHandle ('switchthread 0x{0:X}' -f ([int64]$mainTid))
Start-Sleep -Seconds 1
$tabContent2 = Dump-ThreadsTab $root
Write-Host '=== THREADS TAB AFTER SWITCH ==='
($tabContent2 | Select-Object -Last 20) | ForEach-Object { Write-Host ('  ' + $_) }

# ---- 5. bph script (no attach/switch in script now) ------------------------
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
$scriptLines = @(
    ('bph {0},w,4' -f $addr),
    ('bphwlog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    ('SetHardwareBreakpointCommand {0}, "savedata {1} rip 64"' -f $addr, $hit1),
    ('SetHardwareBreakpointCondition {0}, 0' -f $addr),
    'log "ODWT_ARMED count=1"',
    'run'
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 8

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ('  hit_file=' + $h.Name) }

# ---- 6. Cleanup: detach + exit ---------------------------------------------
Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-Command $root $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
