# Decisive test of the CLI-script mechanism: launch x32dbg with -s <script>
# where the script does attach (script engine => pauseAtAttach=true), sleep to
# let the attach complete, arm bph, then run. No GUI injection at all.
# Checks: resume (counter), hits (odwt-*.bin), sentinel (script completed).
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
Remove-Item -LiteralPath (Join-Path $hitsDir 'odwt-*.bin') -ErrorAction SilentlyContinue
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

# ---- 2. Build the FULL script: attach -> sleep -> arm -> run -> sentinel --
# pid MUST be hex (x64dbg parses all integer constants as hex).
$scriptFile = Join-Path $T 'od-wt-full.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
$script = @(
    ('attach 0x{0:X}' -f $target.Id),
    'sleep 4000',
    ("bph " + $addr + ",w,4"),
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetHardwareBreakpointFastResume ' + $addr),
    ('log "ODWT_ARMED count=1"'),
    'run',
    ('savedata "' + $sentinelFile + '", ' + $addr + ', 4')
)
$script | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host ("script_lines=" + $script.Count)

# ---- 3. Launch x32dbg with -s (no GUI interaction at all) ----------------
$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-s', $scriptFile) -PassThru
Write-Host ("debugger_pid=" + $dbgProc.Id)

# ---- 4. Observe ----------------------------------------------------------
# During the 4s attach-sleep the target should be FROZEN (paused by attach);
# after `run` it should advance. Sample at 5s (mid-sleep) and 12s (post-run).
Start-Sleep -Seconds 5
$progMid = Get-Progress
Start-Sleep -Seconds 1
$progMid2 = Get-Progress
Start-Sleep -Seconds 6
$progA = Get-Progress
Start-Sleep -Seconds 4
$progB = Get-Progress
Write-Host ("progress_mid=" + $progMid + " -> " + $progMid2)
if ($progMid -eq $progMid2) { Write-Host 'MID_PAUSED (attach paused the debuggee during sleep)' }
else { Write-Host ('MID_RUNNING (debuggee NOT paused; attach or script failed) delta=' + ($progMid2 - $progMid)) }
$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile

Write-Host ("progress_a=" + $progA + " progress_b=" + $progB)
if ($progB -gt $progA) { Write-Host ('RESUME_OK counter advancing (+' + ($progB - $progA) + ')') }
else { Write-Host 'RESUME_FAIL counter frozen' }
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ("  hit_file=" + $h.Name + " size=" + $h.Length) }
Write-Host ("sentinel_present=" + $sentinel)
if ($sentinel) { Write-Host ('  sentinel_size=' + (Get-Item -LiteralPath $sentinelFile).Length) }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
