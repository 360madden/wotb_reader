# Decisive test of the FULL write-trace via the -s CLI path:
#   x32dbg -s <script> where the script does attach(hex) -> sleep -> bph
#   (w,4) -> bphwlog -> SetHardwareBreakpointCommand -> SetHardwareBreakpoint
#   Condition 0 -> log ARMED -> run -> sleep window -> detach.
# Verifies: sentinels (where the script aborts, if anywhere), hit files,
# target alive after detach, and that the write site is captured.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$tidFile = Join-Path $T 'wt-counter-tid.txt'
$hitsDir = Join-Path $T 'od-wt-scli-hits'
$sentDir = Join-Path $T 'od-wt-scli-sent'
$scriptFile = Join-Path $T 'od-wt-scli.script'
New-Item -ItemType Directory -Force -Path $hitsDir, $sentDir | Out-Null
Remove-Item -LiteralPath $addrFile, $progressFile, $tidFile, $scriptFile -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $sentDir 'S*.bin') -ErrorAction SilentlyContinue

# ---- 0. Counter target: writes addr + OWN thread id, ticks 40 Hz ---------
$csFile = Join-Path $T 'wt-counter-target.cs'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $csFile)) {
    $src = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
public static class CounterTarget {
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public static unsafe void Main() {
        int* p = stackalloc int[1];
        *p = 0;
        File.WriteAllText(@"ADDRFILE", ((long)p).ToString("X8"));
        File.WriteAllText(@"TIDFILE", GetCurrentThreadId().ToString());
        long n = 0;
        while (true) { (*p)++; n++; if ((n % 40) == 0) File.WriteAllText(@"PROGRESS", (*p).ToString()); Thread.Sleep(25); }
    }
}
'@
    $src = $src.Replace('ADDRFILE', $addrFile).Replace('PROGRESS', $progressFile).Replace('TIDFILE', $tidFile)
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
$mainTid = $null
for ($i = 0; $i -lt 20; $i++) {
    if (Test-Path -LiteralPath $tidFile) { $mainTid = (Get-Content -LiteralPath $tidFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 200
}
if (-not $addr -or -not $mainTid) { Write-Host 'NO_ADDR_OR_TID'; Stop-Process -Id $target.Id -Force; exit 1 }
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr + " main_tid=" + $mainTid)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

# ---- 1. THE FINAL SCRIPT (product shape, -s CLI) --------------------------
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
function Chk([int]$n) { return ('savedata ' + (Join-Path $sentDir ('S' + $n + '.bin')) + ', ' + $addr + ', 4') }
$scriptLines = @(
    '// od-wt final validation via -s CLI',
    ('attach 0x{0:X}' -f $target.Id),
    'sleep 3000',
    (Chk 1),
    ('bph {0},w,4' -f $addr),
    (Chk 2),
    ('bphwlog {0}, "ODWT_HIT addr={0} rip={{rip}}"' -f $addr),
    (Chk 3),
    ('SetHardwareBreakpointCommand {0}, "savedata {1} rip 64"' -f $addr, $hit1),
    (Chk 4),
    ('SetHardwareBreakpointCondition {0}, 0' -f $addr),
    (Chk 5),
    'log "ODWT_ARMED count=1"',
    'run',
    'sleep 6000',
    (Chk 6),
    'detach',
    (Chk 7)
)
$scriptLines | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host '=== SCRIPT ==='
$scriptLines | ForEach-Object { Write-Host ('  ' + $_) }

# ---- 2. Launch x32dbg -s (runs script then exits) -------------------------
$pBefore = Get-Progress
$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-s', $scriptFile) -PassThru
Write-Host ("x32dbg_pid=" + $dbgProc.Id)
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    if ($dbgProc.HasExited) { break }
    Start-Sleep -Milliseconds 500
}
$exited = $dbgProc.HasExited
Write-Host ("x32dbg_exited=" + $exited + " exit_code=" + $(if ($exited) { $dbgProc.ExitCode } else { 'TIMEOUT' }))
if (-not $exited) {
    Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# ---- 3. Results -----------------------------------------------------------
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ('  hit_file=' + $h.Name + ' size=' + $h.Length) }
Write-Host '=== SENTINELS ==='
for ($i = 1; $i -le 7; $i++) {
    $p = Join-Path $sentDir ('S' + $i + '.bin')
    Write-Host ('  S' + $i + '=' + (Test-Path -LiteralPath $p))
}
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
Write-Host ("progress " + $p1 + " -> " + $p2 + " target_alive=" + (-not $target.HasExited))
Write-Host ("target_still_running_after_all=" + (-not $target.HasExited))

Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
