# Test which breakpoint-command syntax works in this x64dbg build. The UIA
# tree dump proved: (1) log content is exposed as DataItem elements via the
# VALUE pattern (the old Name-property reads only saw chrome), and (2) the
# trace's `bpm 0x..., 1, w` errored with "Error executing command!".
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
'@ -Name WtVariants -Namespace Wt

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
    [Wt.WtVariants]::PostMessage($hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    [Wt.WtVariants]::PostMessage($hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 700
}
function Read-LogValues($root) {
    # The log view text is exposed as DataItem elements' VALUE pattern.
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem -or
            $el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text) {
            $v = ''
            try { $v = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
            $n = $el.Current.Name
            if ($v) { $lines.Add($v) }
            elseif ($n -match 'Error|error|Paused|Running') { $lines.Add($n) }
        }
    }
    return $lines
}

$root = $null
for ($i = 0; $i -lt 10 -and -not $root; $i++) {
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) } catch { Start-Sleep -Milliseconds 800 }
}
Send-Cmd $root $win.MainWindowHandle ('attach 0x{0:X}' -f $target.Id)
Start-Sleep -Seconds 4
Send-Cmd $root $win.MainWindowHandle 'pause'
Start-Sleep -Seconds 2

# Test each variant; after each, read the log VALUES to see the result line.
$variants = @(
    ('bpm {0}, 1, w' -f $addr),
    ('bpm {0}, 1' -f $addr),
    ('bpm {0}, w' -f $addr),
    ('bph {0}, w' -f $addr),
    ('bph {0}, 1, w' -f $addr),
    ('bph {0}' -f $addr)
)
function Read-LogStatus($root) {
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($el.Current.ClassName -eq 'LogStatusLabel') { return $el.Current.Name }
    }
    return '<no-status-label>'
}
function Read-AllDataItemValues($root) {
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem) {
            try { $v = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value; if ($v) { $lines.Add($v) } } catch { }
        }
    }
    return $lines
}

foreach ($v in $variants) {
    Send-Cmd $root $win.MainWindowHandle $v
    Start-Sleep -Milliseconds 400
    # Ask x64dbg to LIST the breakpoints; the output goes to the log view.
    Send-Cmd $root $win.MainWindowHandle 'bl'
    Start-Sleep -Milliseconds 800
    $status = Read-LogStatus $root
    $bl = @(Read-AllDataItemValues $root | Select-Object -Last 12)
    Write-Host ("VARIANT [" + $v + "] status=[" + $status + "]")
    $bl | ForEach-Object { Write-Host ('    bl> ' + $_) }
    # clear whatever was set before next variant
    Send-Cmd $root $win.MainWindowHandle 'bpc'
    Send-Cmd $root $win.MainWindowHandle 'bpmc'
    Send-Cmd $root $win.MainWindowHandle 'bphc'
    Start-Sleep -Milliseconds 400
}

Send-Cmd $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
