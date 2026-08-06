# Integration probe: replicates scripts/x64dbg-write-trace.ps1 step 5 with
# the PRODUCT's exact helper implementations (copied verbatim) so the new
# UIA attach -> pause -> scriptload/scriptrun -> log harvest path is
# exercised end-to-end against the counter rig before a live run.
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
$hitsDir = Join-Path $T 'od-wt-int-hits'
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile -ErrorAction SilentlyContinue
Get-ChildItem -LiteralPath $hitsDir -ErrorAction SilentlyContinue | Remove-Item -Force

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Ui {
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

# --- verbatim from x64dbg-write-trace.ps1 ----------------------------------
function Find-X64DbgCommandBar {
    param([IntPtr]$Handle)
    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $root = $null
    for ($i = 0; $i -lt 10 -and -not $root; $i++) {
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle) }
        catch { Start-Sleep -Milliseconds 800 }
    }
    if (-not $root) { return $null }
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    foreach ($e in $edits) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Send-X64DbgCommand {
    param($CommandBar, [IntPtr]$Handle, [string]$Text)
    if (-not $CommandBar) { return }
    $vp = $CommandBar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($Text)
    try { $CommandBar.SetFocus() } catch { }
    Start-Sleep -Milliseconds 200
    [WtX64Ui]::SendEnter($Handle)
    Start-Sleep -Milliseconds 700
}
function Read-X64DbgLog {
    param([IntPtr]$Handle)
    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $root = $null
    for ($i = 0; $i -lt 10 -and -not $root; $i++) {
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle) }
        catch { Start-Sleep -Milliseconds 800 }
    }
    if (-not $root) { return @() }
    $tabCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
        if ($t.Current.Name -eq 'Log') {
            try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break }
            catch { try { $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break } catch { } }
        }
    }
    Start-Sleep -Milliseconds 800
    $lines = New-Object System.Collections.Generic.List[string]
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -and ($n -match 'ODWT_|Error executing|Memory breakpoint|Hardware breakpoint')) {
            $lines.Add($n)
        }
    }
    return $lines
}
# ---------------------------------------------------------------------------

# ---- 0. Counter -----------------------------------------------------------
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

# ---- 1. Launch x32dbg (window only, as pre-arm now does) ------------------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Start-Sleep -Seconds 4
Write-Host ("x32dbg_window pid=" + $win.Id)

# ---- 2. Product step 5: UIA attach + pause ---------------------------------
$cmdBar = Find-X64DbgCommandBar -Handle $win.MainWindowHandle
if (-not $cmdBar) { Write-Host 'FAILED_no_command_bar'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Write-Host 'cmdbar_found'
Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2
$p1 = Get-Progress; Start-Sleep -Seconds 1; $p2 = Get-Progress
Write-Host ("after_attach_pause progress " + $p1 + " -> " + $p2 + " paused=" + ($p1 -eq $p2))

# ---- 3. scriptload/scriptrun with sentinel + run ---------------------------
$scriptFile = Join-Path $hitsDir 'int.script'
@(
    ('log "ODWT_INT_ARMED addr=' + $addr + '"'),
    ('bpm {0}, 1, w' -f $addr),
    ('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    ('SetMemoryBreakpointCommand {0}, "savedata {1}, {0}, 4"' -f $addr, (Join-Path $hitsDir ('odwt-' + $addr + '.bin'))),
    'run'
) | Set-Content -LiteralPath $scriptFile -Encoding ascii
Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle ('scriptload "' + $scriptFile + '"')
Start-Sleep -Milliseconds 600
Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 6
$m1 = Get-Progress; Start-Sleep -Seconds 1; $m2 = Get-Progress
Write-Host ("post_run progress " + $m1 + " -> " + $m2 + " advancing=" + ($m2 -gt $m1))

# ---- 4. Log harvest (product step 7) ----------------------------------------
$harvested = @()
foreach ($ln in @(Read-X64DbgLog -Handle $win.MainWindowHandle)) {
    if ($ln -match 'ODWT_HIT addr=(0x[0-9a-fA-F]+) rip=(0x[0-9a-fA-F]+)') { $harvested += ($Matches[1] + ' ' + $Matches[2]) }
}
Write-Host ("harvested=" + $harvested.Count)
$harvested | Select-Object -First 3 | ForEach-Object { Write-Host ('  ' + $_) }
$proof = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("proof_files=" + $proof.Count)
foreach ($f in $proof) { Write-Host ('  ' + $f.Name + ' size=' + $f.Length) }

Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Send-X64DbgCommand -CommandBar $cmdBar -Handle $win.MainWindowHandle 'exit'
Start-Sleep -Seconds 2
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
