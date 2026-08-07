# Behavioral decisive test: does `bpm` (or `bph`) via the COMMAND BAR
# actually arm and fire on this x64dbg build? Attach (debuggee RUNNING),
# arm the write BP via the command bar, and watch the DebugStatusLabel:
# Paused within a few seconds = the write BP fired (command works).
param([string]$Variant = 'bpm {0}, 1, w')
$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Start-Sleep -Milliseconds 800

$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Start-Sleep -Seconds 4

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@ -Name WtBeh -Namespace Wt

function Get-CmdLineEdit($root) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Send-Cmd($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { Write-Host 'NO_CMDBAR'; return }
    $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    [Wt.WtBeh]::PostMessage($hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    [Wt.WtBeh]::PostMessage($hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 700
}
function Get-DebugState($root) {
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($el.Current.ClassName -eq 'DebugStatusLabel') { return $el.Current.Name }
    }
    return '<no-state>'
}

$root = $null
for ($i = 0; $i -lt 10 -and -not $root; $i++) {
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) } catch { Start-Sleep -Milliseconds 800 }
}
$p0 = Get-Progress
Send-Cmd $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
$stateAfterAttach = Get-DebugState $root
$p1 = Get-Progress
Write-Host ("after_attach state=[" + $stateAfterAttach + "] progress " + $p0 + " -> " + $p1 + " advancing=" + ($p1 -gt $p0))

# If the debuggee is still running, arm the write BP and watch for a break.
$cmd = $Variant -f $addr
Write-Host ("ARMING: " + $cmd)
Send-Cmd $root $win.MainWindowHandle $cmd
Start-Sleep -Milliseconds 800
$p2 = Get-Progress
for ($i = 0; $i -lt 10; $i++) {
    $state = Get-DebugState $root
    if ($state -match 'Paused') { Write-Host ("BP_FIRED state=[" + $state + "] after " + ($i * 800) + "ms"); break }
    Start-Sleep -Milliseconds 800
}
$p3 = Get-Progress
Write-Host ("post_arm state=[" + (Get-DebugState $root) + "] progress " + $p2 + " -> " + $p3 + " frozen=" + ($p3 -eq $p2))

Send-Cmd $root $win.MainWindowHandle 'bpc'
Send-Cmd $root $win.MainWindowHandle 'bpmc'
Send-Cmd $root $win.MainWindowHandle 'bphc'
Send-Cmd $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
