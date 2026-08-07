# FRESH33b decisive probe: inject the trace's EXACT generated script shape
# (setlogfile + bpm + SetMemoryBreakpointLog + SetBreakpointLogFile +
# SetMemoryBreakpointCondition 0 + SetMemoryBreakpointCommand savedata + run)
# into the synthetic counter target (writes continuously to a known address).
# Determines which capture channels actually work in this x64dbg build
# (2026-05-27 Qt5): the live game produced ZERO capture on every channel
# (no engine log, no bp log, no savedata hit files) across FRESH29/31/32/33.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-trace-script-capture'
$scriptFile = Join-Path $T 'od-wt-trace-script.script'
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null
Remove-Item -LiteralPath (Join-Path $hitsDir '*') -ErrorAction SilentlyContinue
$engineLog = Join-Path $hitsDir 'od-wt-engine.log'
$bpLog = Join-Path $hitsDir 'od-wt-bp.log'
$hitFile = Join-Path $hitsDir 'odwt-hit.bin'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtTraceScriptProbe {
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
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Send-Command($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { Write-Host 'NO_CMDBAR'; return }
    $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [WtTraceScriptProbe]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
}

# 0. Counter
$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim() }
    if ($addr) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " addr=" + $addr)
Start-Sleep -Milliseconds 800

# 1. x32dbg + attach + pause
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
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) } catch { Start-Sleep -Milliseconds 800 }
}
Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Send-Command $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2
Write-Host 'attached_and_paused'

# 2. The trace's EXACT script shape
$scriptLines = @(
    ('setlogfile "{0}"' -f $engineLog),
    ('bpm {0}, 1, w' -f $addr),
    ('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    ('SetBreakpointLogFile {0}, "{1}"' -f $addr, $bpLog),
    ('SetMemoryBreakpointCondition {0}, 0' -f $addr),
    ('SetMemoryBreakpointCommand {0}, "savedata {1}, {0}, 4"' -f $addr, $hitFile),
    ('log "ODWT_ARMED count=1"'),
    'run'
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host '=== SCRIPT ==='
$scriptLines | ForEach-Object { Write-Host ('  ' + $_) }

function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

# 3. Inject + run. Track the counter's progress BEFORE and AFTER the window:
# a paused debuggee (memory BP fired on the write) FREEZES progress; a
# running one keeps advancing. This is the decisive bpm-fires-or-not test.
$pBefore = Get-Progress
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 10
$pAfter = Get-Progress
Write-Host ("progress " + $pBefore + " -> " + $pAfter + " (delta=" + ($pAfter - $pBefore) + ") paused_after_run=" + ($pAfter -eq $pBefore))

# 4. Which channels materialized?
Write-Host '=== CHANNEL RESULTS ==='
Write-Host ("engine_log_exists=" + (Test-Path -LiteralPath $engineLog))
Write-Host ("bp_log_exists=" + (Test-Path -LiteralPath $bpLog))
Write-Host ("hit_file_exists=" + (Test-Path -LiteralPath $hitFile))
Get-ChildItem -LiteralPath $hitsDir -File | ForEach-Object { Write-Host ('  file: ' + $_.Name + ' size=' + $_.Length) }
if (Test-Path -LiteralPath $bpLog) { Write-Host '=== BP LOG ==='; Get-Content -LiteralPath $bpLog | Select-Object -Last 10 | ForEach-Object { Write-Host ('  ' + $_) } }
if (Test-Path -LiteralPath $engineLog) { Write-Host '=== ENGINE LOG (tail) ==='; Get-Content -LiteralPath $engineLog | Select-Object -Last 10 | ForEach-Object { Write-Host ('  ' + $_) } }

# 5. Detach + cleanup
Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Seconds 1
Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
