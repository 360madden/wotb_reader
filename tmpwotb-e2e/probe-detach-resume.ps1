# Validates the FRESH15c fix offline (no game session consumed): x64dbg's
# `detach` while the debuggee is PAUSED leaves it frozen (the attach-smoke
# found the game consuming 0 CPU after detach). The fix resumes (`run`)
# BEFORE `detach`. This probe drives a 32-bit counter target through the SAME
# UIA command-bar channel the smoke uses (polled CommandLineEdit find,
# ValuePattern set, PostMessage ENTER) and verifies the target's CPU time
# advances after run+detach - the identical TotalProcessorTime measurement.
param([switch]$SkipBpm, [switch]$UseBph, [switch]$FireAndClear, [switch]$ClearAll, [switch]$DetachPaused, [switch]$WaitOnly, [switch]$ScriptRun, [switch]$SuspendResume, [switch]$NoPause)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
Remove-Item -LiteralPath $addrFile, $progressFile -ErrorAction SilentlyContinue

# ---- 1. Compile + start the 32-bit counter target (as in the FRESH9 probe)
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

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64DetachProbe {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static void SendEnter(IntPtr hWnd) {
        const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
    }
}
"@

# ---- 2. Launch fresh x32dbg, wait for the largest-area window, find the
# command bar with the FRESH15b poll logic.
$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue; exit 1 }

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
if (-not $cmdBar) { Write-Host 'NO_CMDBAR'; Stop-Process -Id $dbgProc.Id, $target.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host 'cmdbar_found'

function Send-Cmd([string]$Text) {
    $vp = $cmdBar.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($Text)
    try { $cmdBar.SetFocus() } catch { }
    Start-Sleep -Milliseconds 200
    [WtX64DetachProbe]::SendEnter($win.MainWindowHandle)
    Start-Sleep -Milliseconds 700
}

function Get-TargetCpuDelta([int]$Ms) {
    $t0 = $target.TotalProcessorTime
    Start-Sleep -Milliseconds $Ms
    $t1 = $target.TotalProcessorTime
    return [math]::Round(($t1 - $t0).TotalMilliseconds, 1)
}

function Read-X64DbgLogTab {
    # Best-effort log-tab dump (known lossy, but 'Breakpoint'/'Error' lines
    # are exactly what we need to see whether the clear command landed).
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
        if (-not $root) { return @() }
        $tabCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::TabItem)
        foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
            if ($t.Current.Name -eq 'Log') {
                try { $t.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
                break
            }
        }
        Start-Sleep -Milliseconds 800
        $out = New-Object System.Collections.Generic.List[string]
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            $n = $el.Current.Name
            if ($n -and ($n -match 'Breakpoint|Error executing|Deleting')) { $out.Add($n) }
        }
        return $out
    }
    catch { return @() }
}

try {
    # ---- 3. attach. With -NoPause (the clean-path test): the command-bar
    # attach does NOT pause (pauseAtAttach=false), so the debuggee keeps
    # running; detach-while-RUNNING should be the clean release with no
    # freeze risk. Otherwise the original pause -> verify sequence.
    Send-Cmd ('attach 0x{0:X}' -f $target.Id)
    Start-Sleep -Seconds 4
    if ($NoPause) {
        $attachRunningDelta = Get-TargetCpuDelta 1500
        Write-Host ("attach_running_delta_ms=" + $attachRunningDelta + " (still running while attached?)")
        Send-Cmd 'detach'
        Start-Sleep -Seconds 2
        $noPauseDelta = Get-TargetCpuDelta 1500
        Write-Host ("post_detach_delta_ms=" + $noPauseDelta)
        if ($noPauseDelta -gt 0) { Write-Host 'DETACH_FROM_RUNNING_OK' }
        else { Write-Host 'DETACH_FROM_RUNNING_FAIL (frozen even without pause)' }
        exit 0
    }
    Send-Cmd 'pause'
    Start-Sleep -Seconds 2
    $pauseDelta = Get-TargetCpuDelta 1500
    Write-Host ("pause_delta_ms=" + $pauseDelta)
    if ($pauseDelta -ge 5) { Write-Host 'PAUSE_FAIL'; exit 1 }
    Write-Host 'PAUSE_OK'

        # ---- 4. arm + clear a BP on the counter address. -SkipBpm isolates
    # whether the write-BP re-break freezes the target; -UseBph tests the
    # HARDWARE breakpoint pair (bph/bphc, what the real trace path uses)
    # against the memory pair (bpm/bpmc, what the smoke currently uses).
    if ($FireAndClear) {
        # Variant 1: arm, run WITHOUT clearing - does the write-BP fire and
        # re-break the target? Then clear while re-broken, run, and verify.
        if ($UseBph) { Send-Cmd ('bph {0}, w' -f $addr) }
        else { Send-Cmd ('bpm {0}, 1, w' -f $addr) }
        Start-Sleep -Milliseconds 500
        Send-Cmd 'run'
        Start-Sleep -Seconds 2
        $rebreakDelta = Get-TargetCpuDelta 1500
        Write-Host ("post_arm_run_delta_ms=" + $rebreakDelta)
        # Clear with the no-arg (clear-all) form - the address form failed
        # every time in the isolation runs.
        if ($UseBph) { Send-Cmd 'bphc' }
        else { Send-Cmd 'bpmc' }
        Start-Sleep -Milliseconds 700
        Send-Cmd 'run'
        Start-Sleep -Seconds 2
        $afterClearDelta = Get-TargetCpuDelta 1500
        Write-Host ("post_clear_run_delta_ms=" + $afterClearDelta)
        if ($afterClearDelta -gt 0) { Write-Host 'CLEAR_AFTER_FIRE_OK' }
        else { Write-Host 'CLEAR_AFTER_FIRE_FAIL' }
        Send-Cmd 'detach'
        Start-Sleep -Seconds 2
        $detachDelta = Get-TargetCpuDelta 1500
        Write-Host ("post_detach_delta_ms=" + $detachDelta)
        if ($detachDelta -gt 0) { Write-Host 'DETACH_RESUME_OK' }
        else { Write-Host 'DETACH_RESUME_FAIL' }
        exit 0
    }
    if (-not $SkipBpm) {
        if ($UseBph) {
            Send-Cmd ('bph {0}, w' -f $addr)
            Start-Sleep -Milliseconds 500
            if ($ClearAll) { Send-Cmd 'bphc' }
            else { Send-Cmd ('bphc {0}' -f $addr) }
        }
        else {
            Send-Cmd ('bpm {0}, 1, w' -f $addr)
            Start-Sleep -Milliseconds 500
            if ($ClearAll) { Send-Cmd 'bpmc' }
            else { Send-Cmd ('bpmc {0}' -f $addr) }
        }
        Start-Sleep -Milliseconds 500
    }

    # ---- 5. detach. -DetachPaused detaches while the debuggee is paused
    # (the command-bar `run` never resumed the target in ANY probe run, so the
    # smoke should not depend on it); otherwise run+nudge first, then detach.
    if ($ScriptRun) {
        # The FRESH9 campaign proved `run` INSIDE a script (scriptrun)
        # resumes the debuggee (counter advanced, hits landed 3/3) while the
        # command-bar `run` never has (15/15 attempts). Test the script path:
        # a 1-line script `run`, executed via scriptload+scriptrun.
        $runScript = Join-Path $T 'od-wt-run.script'
        'run' | Set-Content -LiteralPath $runScript -Encoding ascii
        Send-Cmd ('scriptload "' + $runScript + '"')
        Start-Sleep -Milliseconds 500
        Send-Cmd 'scriptrun'
        Start-Sleep -Seconds 3
        $runDelta = Get-TargetCpuDelta 1500
        Write-Host ("scriptrun_delta_ms=" + $runDelta + " (running while attached?)")
    }
    elseif (-not $DetachPaused) {
        # Command-bar run: up to 5 attempts, 1s apart.
        $runDelta = 0
        for ($r = 1; $r -le 5; $r++) {
            Send-Cmd 'run'
            Start-Sleep -Seconds 1
            $d = Get-TargetCpuDelta 1200
            Write-Host ("  run#" + $r + " delta=" + $d)
            if ($d -gt 0) { $runDelta = $d; break }
        }
        Write-Host ("run_delta_ms=" + $runDelta + " (running while attached?)")
    }
    Send-Cmd 'detach'
    Start-Sleep -Seconds 2
    $resumeDelta = Get-TargetCpuDelta 1500
    Write-Host ("resume_delta_ms=" + $resumeDelta)
    if ($resumeDelta -gt 0) {
        Write-Host 'DETACH_RESUME_OK'
        exit 0
    }
    if (-not $DetachPaused -and $runDelta -gt 0) {
        # It WAS running before detach - the detach froze it. Nudge again.
        Send-Cmd 'run'
        Start-Sleep -Seconds 1
        Send-Cmd 'detach'
        Start-Sleep -Seconds 2
        $resumeDelta = Get-TargetCpuDelta 1500
        Write-Host ("resume_delta2_ms=" + $resumeDelta + " (detach retry)")
        if ($resumeDelta -gt 0) {
            Write-Host 'DETACH_RESUME_OK'
            exit 0
        }
    }
    # Fail-safe diagnostics: is the debugger STILL attached (debug port
    # active) or detached-but-frozen? NtQueryInformationProcess DebugPort.
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64DbgPort {
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, out IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static IntPtr DebugPort(IntPtr h) {
        IntPtr port; int ret;
        if (NtQueryInformationProcess(h, 7, out port, Marshal.SizeOf(typeof(IntPtr)), out ret) == 0) { return port; }
        return IntPtr.Zero;
    }
}
"@
    $handle = $target.Handle
    $portBefore = [WtX64DbgPort]::DebugPort($handle)
    Write-Host ("debug_port_after_detach=" + $portBefore.ToString('X'))

    # Fail-safe: the detach left the debuggee suspended. Resume every thread
    # externally (unwinds the debugger's suspend counts), then re-verify.
    Write-Host '-- frozen after detach, trying external thread resume --'
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64Resume {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] public static extern uint ResumeThread(IntPtr hThread);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] public static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 te);
    [DllImport("kernel32.dll")] public static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 te);
    [StructLayout(LayoutKind.Sequential)] public struct THREADENTRY32 {
        public uint dwSize; public uint cntUsage; public uint th32ThreadID; public uint th32OwnerProcessID;
        public int tpBasePri; public int tpDeltaPri; public uint dwFlags;
    }
    public static int ResumeAll(uint pid) {
        int total = 0; var snap = CreateToolhelp32Snapshot(0x00000004, pid); // TH32CS_SNAPTHREAD
        if (snap == (IntPtr)(-1)) return -1;
        var te = new THREADENTRY32(); te.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));
        if (Thread32First(snap, ref te)) {
            do {
                if (te.th32OwnerProcessID == pid) {
                    var h = OpenThread(0x0002, false, te.th32ThreadID); // THREAD_SUSPEND_RESUME
                    if (h != IntPtr.Zero) {
                        int guard = 0;
                        while (ResumeThread(h) != 0xFFFFFFFF && guard++ < 16) { total++; }
                        CloseHandle(h);
                    }
                }
            } while (Thread32Next(snap, ref te));
        }
        return total;
    }
}
"@
    if ($SuspendResume) {
        # NtSuspendProcess/NtResumeProcess cycle: forces every thread through
        # a schedule transition, which can kick stuck WOW64 handoff threads
        # that plain ResumeThread (no-op when the suspend count is 0) cannot.
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WtX64SusRes {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
}
"@
        $recovered = $false
        for ($r = 1; $r -le 8; $r++) {
            [WtX64SusRes]::NtSuspendProcess($target.Handle) | Out-Null
            Start-Sleep -Milliseconds 300
            [WtX64SusRes]::NtResumeProcess($target.Handle) | Out-Null
            Start-Sleep -Milliseconds 1200
            $postResumeDelta = Get-TargetCpuDelta 1000
            Write-Host ("  susres" + $r + " delta=" + $postResumeDelta)
            if ($postResumeDelta -gt 0) { $recovered = $true; break }
        }
    }
    else {
        # Polled recovery: resume+wait (or wait-only with -WaitOnly - the
        # threads are not suspended, so recovery may be pure settling).
        $recovered = $false
        for ($r = 1; $r -le 12; $r++) {
            if (-not $WaitOnly) {
                $null = [WtX64Resume]::ResumeAll([uint32]$target.Id)
                Start-Sleep -Milliseconds 600
            }
            else { Start-Sleep -Milliseconds 1000 }
            $postResumeDelta = Get-TargetCpuDelta 1000
            Write-Host ("  round" + $r + " delta=" + $postResumeDelta)
            if ($postResumeDelta -gt 0) { $recovered = $true; break }
        }
    }
    $portAfter = [WtX64DbgPort]::DebugPort($handle)
    Write-Host ("debug_port_after_resume=" + $portAfter.ToString('X'))
    if ($recovered) { Write-Host 'EXTERNAL_RESUME_OK' }
    else { Write-Host 'EXTERNAL_RESUME_FAIL (target still frozen)' }
    exit 1
}
finally {
    Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
}
