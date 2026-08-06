# Bisect the memory-BP script commands: after each suspect command, a savedata
# checkpoint (unquoted path, known-good) marks how far the script got. The last
# checkpoint present identifies the aborting command.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-probe-hits'
Remove-Item -LiteralPath $addrFile, $progressFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin'), (Join-Path $T 'cpt*.bin') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe7 {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint VK_RETURN = 0x0D;
    public static void SendEnter(IntPtr hwnd) { PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero); PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero); }
}
"@
function Get-CmdLineEdit($root) {
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
    foreach ($e in $edits) { if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e } }
    return $null
}
function Activate-LogTab($root) {
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))
    foreach ($t in $tabs) { if ($t.Current.Name -eq 'Log') { try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { } } }
    return $false
}
function Send-Command($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { return '<no-cmdbar>' }
    $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [WtX64Probe7]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
}
function Get-LogLines($root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) { $n = $el.Current.Name; if ($n -match 'ODWT_|Error|Attach|attach|Process|breakpoint|Breakpoint|savedata|Script|Debugging|Unable|Invalid|memory|Memory') { $lines.Add($n) } }
    return $lines
}

# ---- 0. Target -----------------------------------------------------------
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
for ($i = 0; $i -lt 20; $i++) { if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }; Start-Sleep -Milliseconds 300 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800

# ---- 1. Launch + attach via UIA -------------------------------------------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) { $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue; if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }; Start-Sleep -Milliseconds 500 }
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Write-Host ("window pid=" + $win.Id)
Start-Sleep -Seconds 4
$root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
Activate-LogTab $root | Out-Null
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 3

# ---- 2. Bisect script ------------------------------------------------------
$scriptFile = Join-Path $T 'od-wt-bisect.script'
$cpt = @{
    1 = Join-Path $T 'cpt1-after-bpm.bin'
    2 = Join-Path $T 'cpt2-after-bpmwlog.bin'
    3 = Join-Path $T 'cpt3-after-setmemcmd.bin'
    4 = Join-Path $T 'cpt4-after-setmemfr.bin'
    5 = Join-Path $T 'cpt5-end.bin'
}
$script = @(
    ("bpm " + $addr + ", w"),
    ('savedata ' + $cpt[1] + ', ' + $addr + ', 4'),
    ('bpmwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('savedata ' + $cpt[2] + ', ' + $addr + ', 4'),
    ('SetMemoryBreakpointCommand ' + $addr + ', "savedata C:\Users\mrkoo\AppData\Local\Temp\od-wt-probe-hits\odwt-{rip}.bin rip 64"'),
    ('savedata ' + $cpt[3] + ', ' + $addr + ', 4'),
    ('SetMemoryBreakpointFastResume ' + $addr),
    ('savedata ' + $cpt[4] + ', ' + $addr + ', 4'),
    'run',
    'sleep 3000',
    ('savedata ' + $cpt[5] + ', ' + $addr + ', 4')
)
$script | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host "--- script ---"
$script | ForEach-Object { Write-Host ('  ' + $_) }
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 8

# ---- 3. Results ------------------------------------------------------------
foreach ($k in @(1, 2, 3, 4, 5)) {
    Write-Host ("cpt" + $k + "=" + (Test-Path -LiteralPath $cpt[$k]))
}
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count)
$log = Get-LogLines $root
Write-Host '=== LOG ==='
$log | Select-Object -First 14 | ForEach-Object { Write-Host ('  ' + $_) }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
