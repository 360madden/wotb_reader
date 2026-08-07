# Dump the FULL UIA tree of the x64dbg window while a trace-style script is
# injected, to determine whether the log-view TEXT is exposed at all (via
# Name, Value, or Text patterns) or only UI chrome (menus/tabs).
$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 0. Counter
$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Start-Sleep -Milliseconds 800

# 1. x32dbg
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force; exit 1 }
Start-Sleep -Seconds 4

function Get-CmdLineEdit($root) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
        if ($e.Current.ClassName -eq 'CommandLineEdit') { return $e }
    }
    return $null
}
function Send-Cmd($root, [IntPtr]$hwnd, [string]$cmd) {
    $bar = Get-CmdLineEdit $root
    if (-not $bar) { return }
    $bar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($cmd)
    $bar.SetFocus()
    Start-Sleep -Milliseconds 200
    Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@ -Name WtUiaTree -Namespace Wt -ErrorAction SilentlyContinue
    [Wt.WtUiaTree]::PostMessage($hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    [Wt.WtUiaTree]::PostMessage($hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
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

# 2. Script that arms a BP and logs (stays paused - no run)
$script = Join-Path $T 'od-wt-uia-tree.script'
@(
    ('log "ODWT_TREE_MARKER count=1"'),
    ('bpm {0}, 1, w' -f $addr),
    ('SetMemoryBreakpointLog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    'log "ODWT_ARMED count=1"'
) | Set-Content -LiteralPath $script -Encoding ascii
Send-Cmd $root $win.MainWindowHandle ('scriptload "' + $script + '"')
Start-Sleep -Milliseconds 600
Send-Cmd $root $win.MainWindowHandle 'scriptrun'
Start-Sleep -Seconds 3

# 3. Full UIA dump - everything: control type, class, name, value, text
$out = Join-Path $env:TEMP 'od-wt-uia-tree-dump.txt'
$sb = New-Object System.Text.StringBuilder
function Dump($el, [int]$depth) {
    try {
        $name = $el.Current.Name
        $ct = $el.Current.ControlType.ProgrammaticName
        $cls = $el.Current.ClassName
        $aid = $el.Current.AutomationId
        $val = ''
        try { $val = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        $text = ''
        try { $text = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(200) } catch { }
        if ($name -or $aid -or $cls -or $val -or $text) {
            $line = ('  ' * $depth) + $ct.Replace('ControlType.','') + ' cls=' + $cls + ' aid=' + $aid + ' name=[' + $name + '] val=[' + ($val -replace "`r?`n",' ') + ']'
            if ($text) { $line += ' TEXT=[' + ($text -replace "`r?`n", ' | ') + ']' }
            [void]$sb.AppendLine($line)
        }
    } catch { }
    if ($depth -lt 6) {
        foreach ($c in $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
            Dump $c ($depth + 1)
        }
    }
}
Dump $root 0
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("tree_dump_lines=" + $sb.Length + " -> " + $out)

# grep-ish summary of interesting lines
$all = $sb.ToString()
Write-Host '=== LOG-CONTENT CANDIDATES ==='
$all -split "`n" | Where-Object { $_ -match 'ODWT_|bpm|savedata|Log|log|Edit|Text|List' } | Select-Object -First 40 | ForEach-Object { Write-Host $_ }

Send-Cmd $root $win.MainWindowHandle 'detach'
Start-Sleep -Milliseconds 800
Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
