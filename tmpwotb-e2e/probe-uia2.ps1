# Reliable UIA command channel for x32dbg:
#   - activate the Log tab (InvokePattern) so log lines are exposed to UIA
#   - set the CommandLineEdit value via ValuePattern
#   - execute with PostMessage WM_KEYDOWN/WM_KEYUP VK_RETURN (no foreground)
# Then: attach hex pid -> verify pause -> scriptload/scriptrun trace script ->
# verify resume + hits + sentinel. The channel is checked 3x with markers.
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
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe6 {
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
    [WtX64Probe6]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}

function Get-Log($root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -match 'ODWT_|Error|error|Attach|attach|Process|breakpoint|Breakpoint|savedata|Debugging|Unable|Invalid|script|Script') {
            $lines.Add($n)
        }
    }
    return $lines
}

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
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

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
Start-Sleep -Seconds 4
$root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
$logTab = Activate-LogTab $root
Write-Host ("log_tab_activated=" + $logTab)
Start-Sleep -Seconds 1

# ---- 2. Marker channel test x3 -------------------------------------------
foreach ($m in @('ODWT_M1_AA11', 'ODWT_M2_BB22', 'ODWT_M3_CC33')) {
    Send-Command $root $win.MainWindowHandle ('log "' + $m + '"')
    $found = (Get-Log $root) -match $m
    Write-Host ("marker " + $m + " -> " + $found)
}

# ---- 3. Attach hex pid ---------------------------------------------------
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 3
$log3 = Get-Log $root
Write-Host '=== LOG AFTER ATTACH ==='
$log3 | Select-Object -First 12 | ForEach-Object { Write-Host ('  ' + $_) }
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
if ($p1 -eq $p2) { Write-Host ("ATTACH_PAUSED progress=" + $p1) }
else { Write-Host ("ATTACH_NOT_PAUSED progress " + $p1 + " -> " + $p2) }

# ---- 4. Trace script via scriptload/scriptrun -----------------------------
# MEMORY breakpoint (bpm) instead of hardware: guard-page BPs fire on ANY
# thread, unlike bph which only arms the active thread (the TODO in
# cmd-breakpoint-control.cpp). This is the multi-thread-safe capture path.
$scriptFile = Join-Path $T 'od-wt-uia2.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
@(
    ("bpm " + $addr + ", w"),
    ('bpmwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetMemoryBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetMemoryBreakpointFastResume ' + $addr),
    ('log "ODWT_ARMED count=1"'),
    'run',
    ('savedata ' + $sentinelFile + ', ' + $addr + ', 4'),
    'log "ODWT_END"'
) | Set-Content -LiteralPath $scriptFile -Encoding ascii
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'

Start-Sleep -Seconds 4
$p3 = Get-Progress
Start-Sleep -Seconds 1
$p4 = Get-Progress
Write-Host ("progress_post_run=" + $p3 + " -> " + $p4 + " " + $(if ($p3 -eq $p4) { 'PAUSED' } else { 'RUNNING' }))
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile
Write-Host ("hits=" + $hits.Count + " sentinel=" + $sentinel)
$logEnd = Get-Log $root
Write-Host '=== LOG AT END ==='
$logEnd | Select-Object -First 15 | ForEach-Object { Write-Host ('  ' + $_) }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
