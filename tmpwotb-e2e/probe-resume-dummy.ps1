# Offline probe: does the x64dbg script mechanism actually RESUME the debuggee?
# Two modes:
#   AutoScript : launch x32dbg with -s <script> (no GUI injection)
#   SendKeys   : replicate the write-trace's GUI injection (click command bar,
#                type scriptload+scriptrun) - the EXACT FRESH9 mechanism
# The target (32-bit powershell) appends a timestamped line every 400ms to a
# tick file. Attach pauses it (ticks freeze); a working `run` resumes it
# (ticks advance). That freeze/thaw is the ground truth.
[CmdletBinding()]
param([ValidateSet('AutoScript', 'SendKeys')][string]$Mode = 'SendKeys')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$ps32 = "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
if (-not (Test-Path -LiteralPath $ps32)) { Write-Host 'NO_PS32'; exit 1 }
$tick = Join-Path $env:TEMP 'wt-tick.txt'
Remove-Item -LiteralPath $tick -ErrorAction SilentlyContinue

# 32-bit target: append timestamped line every 400ms (~5 min cap)
$loop = "for(`$i=1;`$i -le 750;`$i++){Add-Content -LiteralPath '$tick' -Value ((Get-Date).ToString('HH:mm:ss.fff')); Start-Sleep -Milliseconds 400}"
$target = Start-Process -FilePath $ps32 -ArgumentList @('-NoProfile', '-Command', $loop) -PassThru
Start-Sleep -Milliseconds 1600

function Get-TickCount { if (Test-Path -LiteralPath $tick) { @(Get-Content -LiteralPath $tick).Count } else { 0 } }
$before = Get-TickCount
Write-Host ("target_pid=" + $target.Id + " ticks_before=" + $before)

$scriptFile = Join-Path $env:TEMP 'od-wt-probe.script'
@('log "ODWT_PROBE_SCRIPT_START"', 'run', 'log "ODWT_PROBE_RUN_DONE"') |
    Set-Content -LiteralPath $scriptFile -Encoding ascii

$pauseCount = $null
$afterCount = $null

if ($Mode -eq 'AutoScript') {
    $dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-p', "$($target.Id)", '-s', $scriptFile) -PassThru
    Start-Sleep -Seconds 2
    $pauseCount = Get-TickCount
    Start-Sleep -Seconds 4
    $afterCount = Get-TickCount
}
else {
    $dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-p', "$($target.Id)") -PassThru
    $win = $null
    for ($i = 0; $i -lt 20; $i++) {
        $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $win) {
        Write-Host 'NO_WINDOW'
        Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
        Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }
    Write-Host ("window pid=" + $win.Id)
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe {
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
    $rect = New-Object WtX64Probe+RECT
    [WtX64Probe]::WindowRect($win.MainWindowHandle, [ref]$rect) | Out-Null
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int]($rect.Bottom - 16)   # command bar: full-width bottom strip
    [WtX64Probe]::ForceForeground($win.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    [WtX64Probe]::Click($cx, $cy)
    Start-Sleep -Milliseconds 400
    $wshell = New-Object -ComObject WScript.Shell
    $null = $wshell.SendKeys('scriptload "' + $scriptFile + '"')
    Start-Sleep -Milliseconds 300
    $null = $wshell.SendKeys('{ENTER}')
    Start-Sleep -Milliseconds 600
    $null = $wshell.SendKeys('scriptrun')
    Start-Sleep -Milliseconds 300
    $null = $wshell.SendKeys('{ENTER}')
    Write-Host 'injected scriptload+scriptrun'
    Start-Sleep -Seconds 2
    $pauseCount = Get-TickCount
    Start-Sleep -Seconds 4
    $afterCount = Get-TickCount
}

Write-Host ("ticks_pause=" + $pauseCount + " ticks_after=" + $afterCount)
if ($afterCount -gt $pauseCount) {
    Write-Host ('RESUME_OK debuggee running (advance=' + ($afterCount - $pauseCount) + ')')
}
else {
    Write-Host 'RESUME_FAIL debuggee stayed paused (tick file frozen)'
}

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
