# DECISIVE: does {rip} substitute inside a memory-BP command filename?
# Config: bpm(restore) + SetMemoryBreakpointCommand "savedata <f-{rip}.bin>, <addr>, 4"
#         + SetMemoryBreakpointCondition 0  (no break, command runs, instant resume)
# Hardened: kills stray x32dbg (single-instance forwarding), verifies attach paused.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

taskkill //F //IM x32dbg.exe 2>$null | Out-Null
taskkill //F //IM wt-counter-target.exe 2>$null | Out-Null
Start-Sleep -Seconds 1

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-rip-hits'
$sentDir = Join-Path $T 'od-wt-rip-sent'
New-Item -ItemType Directory -Force -Path $hitsDir, $sentDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $hitsDir -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -LiteralPath $sentDir -ErrorAction SilentlyContinue | Remove-Item -Force

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Rip {
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
function Send-Command($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { return '<no-cmdbar>' }
    $vp = $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [WtX64Rip]::SendEnter($hwnd)
    Start-Sleep -Milliseconds 700
    return 'sent'
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

# ---- 1. x32dbg + attach (retry until paused) ------------------------------
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

$attached = $false
for ($try = 1; $try -le 3 -and -not $attached; $try++) {
    Send-Command $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
    Start-Sleep -Seconds 4
    Send-Command $root $win.MainWindowHandle 'pause'
    Start-Sleep -Seconds 2
    $a1 = Get-Progress; Start-Sleep -Seconds 1; $a2 = Get-Progress
    if ($a1 -eq $a2) { $attached = $true; Write-Host ("attach_try" + $try + "_PAUSED progress=" + $a1) }
    else { Write-Host ("attach_try" + $try + "_NOT_PAUSED " + $a1 + "->" + $a2) }
}
if (-not $attached) { Write-Host 'ATTACH_FAILED'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }

# ---- 2. Decisive script ----------------------------------------------------
$hitPattern = Join-Path $hitsDir ("odwt-0x" + $addr.Substring(2).ToUpper() + "-0x{rip}.bin")
function Chk([int]$n) { return ('savedata ' + (Join-Path $sentDir ('S' + $n + '.bin')) + ', ' + $addr + ', 4') }
$scriptFile = Join-Path $hitsDir 'decisive.script'
$lines = @(
    (Chk 1),
    ('bpm {0}, 1, w' -f $addr),
    (Chk 2),
    ('SetMemoryBreakpointCommand {0}, "savedata {1}, {0}, 4"' -f $addr, $hitPattern),
    (Chk 3),
    ('SetMemoryBreakpointCondition {0}, 0' -f $addr),
    (Chk 4),
    'log "ODWT_ARMED count=1"',
    'run'
)
$lines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host '=== SCRIPT ==='
$lines | ForEach-Object { Write-Host ('  ' + $_) }
Send-Command $root $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-Command $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 8

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ('  hit_file=' + $h.Name + ' size=' + $h.Length) }
Write-Host '=== SENTINELS ==='
for ($i = 1; $i -le 4; $i++) { $p = Join-Path $sentDir ('S' + $i + '.bin'); Write-Host ('  S' + $i + '=' + (Test-Path -LiteralPath $p)) }
$m1 = Get-Progress; Start-Sleep -Seconds 1; $m2 = Get-Progress
Write-Host ("progress " + $m1 + " -> " + $m2 + " advancing=" + ($m2 -gt $m1))

Send-Command $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-Command $root $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
