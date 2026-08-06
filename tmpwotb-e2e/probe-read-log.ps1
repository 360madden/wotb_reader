# Read the x32dbg log tab via UI Automation (x64dbg ships with Qt
# accessibility enabled), then test whether command-bar SendKeys lands.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-LogText([IntPtr]$hwnd, [int]$maxLen = 4000) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        if (-not $root) { return '<no-root>' }
        $cond = [System.Windows.Automation.Condition]::TrueCondition
        $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($el in $items) {
            $txt = $null
            try { $txt = $el.Current.Name } catch { $txt = $null }
            if (-not [string]::IsNullOrWhiteSpace($txt)) { $parts.Add($txt) }
        }
        $joined = $parts -join ' | '
        if ($joined.Length -gt $maxLen) { $joined = $joined.Substring($joined.Length - $maxLen) }
        return $joined
    }
    catch { return ('<uia-error: ' + $_.Exception.Message + '>') }
}

# ---- 1. Launch plain x32dbg ----------------------------------------------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id -Force; exit 1 }
Write-Host ("window pid=" + $win.Id + " hwnd=" + $win.MainWindowHandle)
Start-Sleep -Seconds 3

$baseline = Get-LogText $win.MainWindowHandle
Write-Host ''
Write-Host '=== LOG BASELINE ==='
Write-Host $baseline
Write-Host '===================='

# ---- 2. Send a marker via command-bar SendKeys --------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe4 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static void ForceForeground(IntPtr hWnd) {
        uint unused; uint target = GetWindowThreadProcessId(hWnd, out unused);
        uint current = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow(); uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out unused) : 0;
        if (fgThread != 0) AttachThreadInput(current, fgThread, true);
        if (target != 0) AttachThreadInput(current, target, true);
        SetForegroundWindow(hWnd);
        if (target != 0) AttachThreadInput(current, target, false);
        if (fgThread != 0) AttachThreadInput(current, fgThread, false);
    }
    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
    public static bool WindowRect(IntPtr hWnd, out RECT r) { r = new RECT(); return GetWindowRect(hWnd, out r); }
}
"@
$rect = New-Object WtX64Probe4+RECT
[WtX64Probe4]::WindowRect($win.MainWindowHandle, [ref]$rect) | Out-Null
$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int]($rect.Bottom - 16)
Write-Host ("cmdbar click at " + $cx + "," + $cy + " rect=" + $rect.Left + "," + $rect.Top + "," + $rect.Right + "," + $rect.Bottom)
[WtX64Probe4]::ForceForeground($win.MainWindowHandle)
Start-Sleep -Milliseconds 300
[WtX64Probe4]::Click($cx, $cy)
Start-Sleep -Milliseconds 400
$wshell = New-Object -ComObject WScript.Shell
$marker = 'log "ODWT_MARKER_7A91"'
$null = $wshell.SendKeys($marker)
Start-Sleep -Milliseconds 300
$null = $wshell.SendKeys('{ENTER}')
Start-Sleep -Milliseconds 800

$after = Get-LogText $win.MainWindowHandle
Write-Host ''
Write-Host '=== LOG AFTER SENDKEYS ==='
Write-Host $after
Write-Host '=========================='
if ($after -match 'ODWT_MARKER_7A91') { Write-Host 'SENDKEYS_CHANNEL_OK marker landed' }
else { Write-Host 'SENDKEYS_CHANNEL_FAIL marker NOT in log' }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
