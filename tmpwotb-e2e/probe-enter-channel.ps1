# FRESH15d diagnosis: the attach/pause/run/detach smoke showed `run` rarely
# executing (run_delta=0 in 3/4 runs even while attached) while attach/pause
# worked 9/9. Hypothesis: the ENTER keypress posted to the MAIN WINDOW relies
# on the CommandLineEdit holding focus, which is flaky after state changes.
# This probe tests two ENTER delivery modes against a trivial `log` command
# (x64dbg CLEARS the command bar only when a command executes, so
# value-cleared is a reliable execution signal):
#   mode=main : PostMessage ENTER to the main window (current implementation)
#   mode=edit : PostMessage ENTER to the CommandLineEdit's own NativeWindowHandle
param([string]$Mode = 'main')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$dbgProc = Start-Process -FilePath $dbg -PassThru
try {
    $win = $null
    for ($i = 0; $i -lt 30; $i++) {
        $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $win) { Write-Host 'NO_WINDOW'; exit 1 }

    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $cmdBar = $null
    for ($i = 0; $i -lt 30 -and -not $cmdBar; $i++) {
        $root = $null
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle) } catch { }
        if ($root) {
            try {
                foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)) {
                    if ($e.Current.ClassName -eq 'CommandLineEdit') { $cmdBar = $e; break }
                }
            }
            catch { }
        }
        if (-not $cmdBar) { Start-Sleep -Milliseconds 500 }
    }
    if (-not $cmdBar) { Write-Host 'NO_CMDBAR'; exit 1 }

    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64EnterProbe {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static void SendEnter(IntPtr hWnd) {
        const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
    }
}
"@

    $vp = $cmdBar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $editHwnd = [IntPtr]$cmdBar.Current.NativeWindowHandle
    Write-Host ("mode=" + $Mode + " mainHwnd=0x" + $win.MainWindowHandle.ToString('X') + " editHwnd=0x" + $editHwnd.ToString('X'))

    $executed = 0
    for ($i = 1; $i -le 8; $i++) {
        $vp.SetValue('log "PROBE_ENTER"')
        Start-Sleep -Milliseconds 150
        try { $cmdBar.SetFocus() } catch { }
        Start-Sleep -Milliseconds 200
        if ($Mode -eq 'edit') { [WtX64EnterProbe]::SendEnter($editHwnd) }
        else { [WtX64EnterProbe]::SendEnter($win.MainWindowHandle) }
        Start-Sleep -Milliseconds 700
        $left = $vp.Current.Value
        if ($left) { Write-Host ("  #" + $i + " NOT_EXECUTED value=[" + $left + "]") }
        else { $executed++; Write-Host ("  #" + $i + " EXECUTED") }
    }
    Write-Host ("EXECUTED=" + $executed + "/8")
    exit 0
}
finally {
    Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
}
