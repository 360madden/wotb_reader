# Validates the FRESH15b fix offline against a REAL fresh x32dbg window (no
# game session consumed). Two defects were found in the attach-smoke:
#   (1) Get-X64DbgWindowHandle's EnumWindows fallback took the FIRST window,
#       which can be a transient Qt splash/helper window that never exposes
#       CommandLineEdit (detail='no_command_bar', pre-attach); the fix selects
#       the LARGEST-AREA window instead.
#   (2) Find-X64DbgCommandBar did a single FindAll - the UIA tree lags window
#       creation; the fix polls root + FindAll for up to 15s.
# This probe exercises BOTH fixed paths: it never waits for
# Process.MainWindowHandle - it polls the largest-area top-level window from
# launch and times the CommandLineEdit find on that handle.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
if (-not (Test-Path -LiteralPath $dbg)) { Write-Host 'NO_DEBUGGER_EXE'; exit 2 }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64ProbeWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static IntPtr LargestWindowForProcess(uint pid) {
        IntPtr best = IntPtr.Zero; long bestArea = -1; RECT r;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == pid && GetWindowRect(h, out r)) {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
    public static string Titles(uint pid) {
        var parts = new System.Collections.Generic.List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid == pid) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, sb.Capacity); parts.Add(sb.ToString()); }
            return true;
        }, IntPtr.Zero);
        return string.Join("|", parts);
    }
}
"@

$dbgProc = Start-Process -FilePath $dbg -PassThru
try {
    Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction SilentlyContinue
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $found = $null
    $usedHandle = [IntPtr]::Zero
    $attempts = 0
    while ($sw.Elapsed.TotalSeconds -lt 20 -and -not $found) {
        $attempts++
        $h = [WtX64ProbeWin]::LargestWindowForProcess([uint32]$dbgProc.Id)
        if ($h -ne [IntPtr]::Zero) {
            $usedHandle = $h
            $root = $null
            try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($h) } catch { }
            if ($root) {
                try {
                    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
                    foreach ($e in $edits) {
                        if ($e.Current.ClassName -eq 'CommandLineEdit') { $found = $e; break }
                    }
                }
                catch { }
            }
        }
        if (-not $found) { Start-Sleep -Milliseconds 500 }
    }
    $sw.Stop()

    if (-not $found) {
        Write-Host ("CMDBAR_TIMEOUT after=" + [math]::Round($sw.Elapsed.TotalSeconds, 1) + "s attempts=" + $attempts)
        Write-Host ("titles=" + [WtX64ProbeWin]::Titles([uint32]$dbgProc.Id))
        exit 1
    }
    Write-Host ("CMDBAR_FOUND after=" + [math]::Round($sw.Elapsed.TotalSeconds, 1) + "s attempts=" + $attempts + " hwnd=0x" + $usedHandle.ToString('X'))
    # Sanity: the ValuePattern channel must work on this element.
    try {
        $vp = $found.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue('log "ODWT_PROBE_OK"')
        Write-Host 'VALUEPATTERN_SET_OK'
    }
    catch { Write-Host ('VALUEPATTERN_FAIL: ' + $_.Exception.Message) }
    exit 0
}
finally {
    Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
}
