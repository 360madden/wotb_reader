# Decisive offline validation of the FINAL write-trace mechanism:
#   plain x32dbg launch -> UIA command channel -> scriptrun of a script that
#   does: attach 0x<hexpid> -> sleep -> switchthread -> bph (w,4) ->
#   bphwlog + SetHardwareBreakpointCommand + SetHardwareBreakpointCondition 0
#   -> log ARMED -> run. Then verify: hits captured, debuggee NEVER paused,
#   detach + exit release the debuggee alive.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-final-hits'
$scriptFile = Join-Path $T 'od-wt-final.script'
Remove-Item -LiteralPath $addrFile, $progressFile, $scriptFile -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64ProbeFinal {
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
    [WtX64ProbeFinal]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}

function Get-Log($root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -and $n.Length -gt 0 -and $n -match 'ODWT_|CHK_|Error|error|Attach|attach|Hardware|savedata|Debugging|Unable|Invalid|script|Script|finished') {
            $lines.Add($n)
        }
    }
    return $lines
}

# ---- 0. Counter target (main-thread write at 40 Hz) ---------------------
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
        while (true) { (*p)++; n++; if ((n % 40) == 0) File.WriteAllText(@"PROGRESS", (*p).ToString()); Thread.Sleep(25); }
    }
}
'@
    $src = $src.Replace('ADDRFILE', $addrFile).Replace('PROGRESS', $progressFile)
    Set-Content -LiteralPath $csFile -Value $src -Encoding ascii
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    & $csc /nologo /target:exe /platform:x86 /unsafe /out:$exe $csFile 2>&1 | Out-Null
}
$target = Start-Process -FilePath $exe -PassThru
$mainTid = ($target.Threads | Select-Object -First 1 -ExpandProperty Id)
Write-Host ("main_thread_id=" + $mainTid)
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

# ---- 1. Launch plain x32dbg (NO -p: the script owns the attach) ----------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Write-Host ("window pid=" + $win.Id)
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

# ---- 2. Marker channel sanity --------------------------------------------
foreach ($m in @('ODWT_F1_AA11', 'ODWT_F2_BB22')) {
    Send-Command $root $win.MainWindowHandle ('log "' + $m + '"')
    $found = (Get-Log $root) -match $m
    Write-Host ("marker " + $m + " -> " + $found)
}

# ---- 3. THE FINAL SCRIPT SHAPE, BISECTED WITH SAVEDATA SENTINELS ------
# savedata writes a file deterministically; the presence/absence of each
# sentinel on disk pinpoints the exact line where the script aborts.
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
$sent = Join-Path $T 'od-wt-final-sent'
New-Item -ItemType Directory -Force -Path $sent | Out-Null
Remove-Item -LiteralPath (Join-Path $sent 'S*.bin') -ErrorAction SilentlyContinue
function Chk([int]$n) { return ('savedata ' + (Join-Path $sent ('S' + $n + '.bin')) + ', ' + $addr + ', 4') }
$scriptLines = @(
    '// od-wt final validation script (no switchthread)',
    ('attach 0x{0:X}' -f $target.Id),
    'sleep 3000',
    (Chk 1),
    ('bph {0},w,4' -f $addr),
    (Chk 2),
    ('bphwlog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    (Chk 3),
    ('SetHardwareBreakpointCommand {0}, "savedata {1} rip 64"' -f $addr, $hit1),
    (Chk 4),
    ('SetHardwareBreakpointCondition {0}, 0' -f $addr),
    (Chk 5),
    'log "ODWT_ARMED count=1"',
    'run'
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host '=== SCRIPT ==='
$scriptLines | ForEach-Object { Write-Host ('  ' + $_) }

$pBefore = Get-Progress
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'

# ---- 4. Wait for attach + arm + run + hits --------------------------------
$logLines = @()
for ($i = 0; $i -lt 16; $i++) {
    Start-Sleep -Milliseconds 500
    $logLines = Get-Log $root
    if ($logLines -match 'ODWT_ARMED') { break }
}
Start-Sleep -Seconds 4   # let the 40 Hz writes accumulate

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
$advanced = ($p2 -gt $p1)
Write-Host ("hits=" + $hits.Count + " progress " + $p1 + " -> " + $p2 + " advanced=" + $advanced)
Write-Host ("pre_script_progress=" + $pBefore)

Write-Host '=== SENTINELS ==='
for ($i = 1; $i -le 8; $i++) {
    $p = Join-Path $sent ('S' + $i + '.bin')
    Write-Host ('  S' + $i + '=' + (Test-Path -LiteralPath $p))
}
Write-Host '=== LOG (raw, all elements) ==='
($logLines | Select-Object -Last 25) | ForEach-Object { Write-Host ('  ' + $_) }

# ---- 5. Graceful release: detach then exit -------------------------------
Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-Command $root $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
$p3 = Get-Progress
Start-Sleep -Seconds 1
$p4 = Get-Progress
Write-Host ("post_detach_progress " + $p3 + " -> " + $p4 + " alive=" + ($p4 -gt $p3))
$dbgAlive = (Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue) -ne $null
$tgtAlive = (Get-Process -Id $target.Id -ErrorAction SilentlyContinue) -ne $null
Write-Host ("debugger_alive_after_exit=" + $dbgAlive + " target_alive_after_detach=" + $tgtAlive)

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
