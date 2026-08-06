# Offline integration test of the REAL x64dbg write-trace script shape.
# A 32-bit counter target writes a stack int every 25ms; the probe attaches
# x32dbg, VERIFIES the attach actually paused the target, then injects the
# exact script commands used by x64dbg-write-trace.ps1 and checks:
#   1. PAUSED  - counter frozen after attach (attach really happened)?
#   2. RESUME  - did `run` get reached (counter advancing)?
#   3. HITS    - did odwt-*.bin files land in the hits dir (breakpoint fired)?
#   4. SENTINEL- did the script run to completion (script-done file)?
# savedata order is file, addr, size (verified against x64dbg source
# cmd-memory-operations.cpp cbInstrSavedata: argv[1]=file argv[2]=addr
# argv[3]=size).
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

# ---- 1. Compile the 32-bit counter target -------------------------------
$csFile = Join-Path $T 'wt-counter-target.cs'
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
            (*p)++;                     // the write we trace (every ~25ms)
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
if (-not (Test-Path -LiteralPath $csc)) { Write-Host 'NO_CSC'; exit 1 }
& $csc /nologo /target:exe /platform:x86 /unsafe /out:$exe $csFile 2>&1 | Out-Null
if (-not (Test-Path -LiteralPath $exe)) { Write-Host 'COMPILE_FAILED'; exit 1 }
Write-Host 'target_compiled'

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

# ---- 2. Attach -----------------------------------------------------------
$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-p', "$($target.Id)") -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host ("window pid=" + $win.Id)

# Let the async attach complete, then verify the target is PAUSED.
Start-Sleep -Seconds 4
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
if ($p1 -eq $p2) { Write-Host ("ATTACH_PAUSED progress=" + $p1) }
else { Write-Host ("ATTACH_NOT_PAUSED progress " + $p1 + " -> " + $p2 + ' (attach may have failed or not completed)') }

# ---- 3. Generate the REAL-shape script ----------------------------------
$scriptFile = Join-Path $T 'od-wt-probe-real.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
$scriptLines = @(
    ("bph " + $addr + ",w,4"),
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetHardwareBreakpointFastResume ' + $addr),
    ('log "ODWT_ARMED count=1"'),
    ('run'),
    ('savedata "' + $sentinelFile + '", ' + $addr + ', 4')
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host ("script_lines=" + $scriptLines.Count)

# ---- 4. Inject via the exact write-trace mechanism ----------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Probe2 {
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
$rect = New-Object WtX64Probe2+RECT
[WtX64Probe2]::WindowRect($win.MainWindowHandle, [ref]$rect) | Out-Null
$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int]($rect.Bottom - 16)
[WtX64Probe2]::ForceForeground($win.MainWindowHandle)
Start-Sleep -Milliseconds 300
[WtX64Probe2]::Click($cx, $cy)
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

# ---- 5. Observe ---------------------------------------------------------
Start-Sleep -Seconds 3
$progA = Get-Progress
Start-Sleep -Seconds 4
$progB = Get-Progress
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile

Write-Host ("progress_a=" + $progA + " progress_b=" + $progB)
if ($progB -gt $progA) { Write-Host ('RESUME_OK run reached (counter advancing +' + ($progB - $progA) + ')') }
else { Write-Host 'RESUME_FAIL counter frozen (script aborted before run, or never loaded)' }
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ("  hit_file=" + $h.Name + " size=" + $h.Length) }
Write-Host ("sentinel_present=" + $sentinel)

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
