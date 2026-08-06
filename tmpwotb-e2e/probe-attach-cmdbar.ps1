# Decisive offline test of the proposed FRESH10 fix: drive `attach <pid>`
# through x32dbg's command bar (the channel proven to work for scriptload/
# scriptrun), verify the target actually PAUSES, then run the real
# write-trace script and check resume + hits + script-completion sentinel.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$hitsDir = Join-Path $T 'od-wt-probe-hits'
$sentinelFile = Join-Path $T 'od-wt-probe-done.bin'
Remove-Item -LiteralPath $addrFile, $progressFile, $sentinelFile -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $hitsDir | Out-Null

# ---- 1. Compile + start the 32-bit counter target -----------------------
$csFile = Join-Path $T 'wt-counter-target.cs'
if (-not (Test-Path -LiteralPath $exe)) {
    $src = @'
using System;
using System.IO;
using System.Threading;
public static class CounterTarget {
    public static unsafe void Main() {
        int* p = stackalloc int[1];
        *p = 0;
        File.WriteAllText(@"ADDRFILE", ((long)p).ToString("X8"));
        long n = 0;
        while (true) {
            (*p)++;
            n++;
            if ((n % 40) == 0) File.WriteAllText(@"PROGRESS", (*p).ToString());
            Thread.Sleep(25);
        }
    }
}
'@
    $src = $src.Replace('ADDRFILE', $addrFile).Replace('PROGRESS', $progressFile)
    Set-Content -LiteralPath $csFile -Value $src -Encoding ascii
    $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    & $csc /nologo /target:exe /platform:x86 /unsafe /out:$exe $csFile 2>&1 | Out-Null
}
$target = Start-Process -FilePath $exe -PassThru
$addr = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 300
}
if (-not $addr) { Write-Host 'NO_ADDR_FILE'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800

function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe3 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
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
    public static string WindowTitles(uint pid) {
        var parts = new System.Collections.Generic.List<string>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            uint wndPid; GetWindowThreadProcessId(hWnd, out wndPid);
            if (wndPid == pid) { var sb = new System.Text.StringBuilder(256); GetWindowText(hWnd, sb, sb.Capacity); parts.Add(sb.ToString()); }
            return true;
        }, IntPtr.Zero);
        return string.Join("|", parts);
    }
}
"@

# ---- 2. Launch x32dbg WITHOUT args (no attach), wait for window ----------
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host ("window pid=" + $win.Id)

function Send-CmdBar([string]$Text) {
    $rect = New-Object WtX64Probe3+RECT
    [WtX64Probe3]::WindowRect($win.MainWindowHandle, [ref]$rect) | Out-Null
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int]($rect.Bottom - 16)
    [WtX64Probe3]::ForceForeground($win.MainWindowHandle)
    Start-Sleep -Milliseconds 250
    [WtX64Probe3]::Click($cx, $cy)
    Start-Sleep -Milliseconds 350
    $wshell = New-Object -ComObject WScript.Shell
    $null = $wshell.SendKeys($Text)
    Start-Sleep -Milliseconds 250
    $null = $wshell.SendKeys('{ENTER}')
    Start-Sleep -Milliseconds 350
}

# ---- 3. Attach via a SCRIPT (pauseAtAttach only when ScriptIsExecutingCommand) ---
# x64dbg source cmd-debug-control.cpp cbDebugAttach:
#   init.pauseAtAttach = ScriptIsExecutingCommand();
# so `attach` must run via scriptrun to PAUSE the debuggee at attach.
$attachScript = Join-Path $T 'od-wt-attach.script'
('attach ' + $target.Id) | Set-Content -LiteralPath $attachScript -Encoding ascii
Send-CmdBar ('scriptload "' + $attachScript + '"')
Send-CmdBar 'scriptrun'
Write-Host 'sent attach-via-script'
Start-Sleep -Seconds 4

# ---- 4. Verify PAUSED ---------------------------------------------------
$titles = [WtX64Probe3]::WindowTitles([uint32]$win.Id)
Write-Host ("dbg_windows=" + $titles)
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
if ($p1 -eq $p2) { Write-Host ("ATTACH_PAUSED progress=" + $p1) }
else { Write-Host ("ATTACH_NOT_PAUSED progress " + $p1 + " -> " + $p2) }

# ---- 5. Run the real script ----------------------------------------------
$scriptFile = Join-Path $T 'od-wt-probe-real.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
@(
    ("bph " + $addr + ",w,4"),
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetHardwareBreakpointFastResume ' + $addr),
    ('log "ODWT_ARMED count=1"'),
    ('run'),
    ('savedata "' + $sentinelFile + '", ' + $addr + ', 4')
) | Set-Content -LiteralPath $scriptFile -Encoding ascii
Send-CmdBar ('scriptload "' + $scriptFile + '"')
Send-CmdBar 'scriptrun'
Write-Host 'sent scriptload+scriptrun'

Start-Sleep -Seconds 3
$progA = Get-Progress
Start-Sleep -Seconds 4
$progB = Get-Progress
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile

Write-Host ("progress_a=" + $progA + " progress_b=" + $progB)
if ($progB -gt $progA) { Write-Host ('RESUME_OK counter advancing (+' + ($progB - $progA) + ')') }
else { Write-Host 'RESUME_FAIL counter frozen' }
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ("  hit_file=" + $h.Name + " size=" + $h.Length) }
Write-Host ("sentinel_present=" + $sentinel)

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
