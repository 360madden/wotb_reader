# Single-variant decisive test: arm ONE BP via the command bar, then `bl`,
# and dump the RAW DataItem values from the log view to see the actual
# breakpoint-list entry (proves whether the BP command worked).
param([string]$Variant = 'bpm {0}, 1, w')
$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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
'@ -Name WtSingle -Namespace Wt

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
    [Wt.WtSingle]::PostMessage($hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    [Wt.WtSingle]::PostMessage($hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 700
}

$root = $null
for ($i = 0; $i -lt 10 -and -not $root; $i++) {
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) } catch { Start-Sleep -Milliseconds 800 }
}
Send-Cmd $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Send-Cmd $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2

$cmd = $Variant -f $addr
Write-Host ("ARMING: " + $cmd)
Send-Cmd $root $win.MainWindowHandle $cmd
Start-Sleep -Milliseconds 600
Send-Cmd $root $win.MainWindowHandle 'bl'
Start-Sleep -Milliseconds 1000

# Dump raw DataItem values + the log status label
$all = @()
foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($el.Current.ClassName -eq 'LogStatusLabel') { Write-Host ('STATUS: [' + $el.Current.Name + ']') }
    if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem) {
        try { $v = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value; if ($v) { $all += $v } } catch { }
    }
}
Write-Host ('DATAITEMS=' + $all.Count)
$needle = $addr.Substring(2).ToUpper()
$addrLower = $addr.ToLower()
$hits = @($all | Where-Object { $_ -match ('0x' + $needle) -or $_ -match $addrLower -or $_ -match 'mwb|hb |HW|Memory breakpoint|bpm|bph' })
Write-Host ('MATCHES=' + $hits.Count)
$hits | Select-Object -First 15 | ForEach-Object { Write-Host ('  MT> ' + $_) }

Send-Cmd $root $win.MainWindowHandle 'bpc'
Send-Cmd $root $win.MainWindowHandle 'bpmc'
Send-Cmd $root $win.MainWindowHandle 'bphc'
Send-Cmd $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
