# Final focused offline test of the complete write-trace mechanism:
#   -s script: attach (hex pid) -> sleep -> bph -> SetHardwareBreakpointCommand
#   (savedata, unquoted path) -> run -> sleep (trace window) -> sentinel savedata
#   (unquoted) -> END. NO fast-resume. Expect: >=1 odwt-*.bin hit file + sentinel.
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
$addr = '0x' + $addr
Write-Host ("target_pid=" + $target.Id + " counter_addr=" + $addr)
Start-Sleep -Milliseconds 800
function Get-Progress { if (Test-Path -LiteralPath $progressFile) { [long](Get-Content -LiteralPath $progressFile -Raw).Trim() } else { -1 } }

$scriptFile = Join-Path $T 'od-wt-final.script'
$hitFile = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
# savedata order: file, addr, size. Paths unquoted (no spaces in $T).
$script = @(
    ('attach 0x{0:X}' -f $target.Id),
    'sleep 3000',
    # bph arms DRs on the ACTIVE thread only; switchthread (no args) selects
    # the MAIN thread - the counter loop runs there.
    'switchthread',
    ("bph " + $addr + ",w,4"),
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hitFile + ' rip 64"'),
    ('log "ODWT_ARMED count=1"'),
    'run',
    'sleep 4000',
    ('savedata ' + $sentinelFile + ', ' + $addr + ', 4'),
    'log "ODWT_END"'
)
$script | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host ("script_lines=" + $script.Count)
Write-Host ("script_content:")
$script | ForEach-Object { Write-Host ('  ' + $_) }

$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-s', $scriptFile) -PassThru
Write-Host ("debugger_pid=" + $dbgProc.Id)

# Sample: pause during attach-sleep (~4s), running after run (~8s), done (~12s)
Start-Sleep -Seconds 4
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
Write-Host ("progress@4s=" + $p1 + " -> " + $p2 + " " + $(if ($p1 -eq $p2) { 'PAUSED' } else { 'RUNNING' }))
Start-Sleep -Seconds 4
$p3 = Get-Progress
Start-Sleep -Seconds 1
$p4 = Get-Progress
Write-Host ("progress@9s=" + $p3 + " -> " + $p4 + " " + $(if ($p3 -eq $p4) { 'PAUSED' } else { 'RUNNING' }))
Start-Sleep -Seconds 4
Write-Host ("debugger_alive@13s=" + [bool](Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue))

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
$sentinel = Test-Path -LiteralPath $sentinelFile
Write-Host ("hits=" + $hits.Count)
foreach ($h in $hits) { Write-Host ("  hit_file=" + $h.Name + " size=" + $h.Length) }
Write-Host ("sentinel_present=" + $sentinel)
if ($sentinel) { Write-Host ("  sentinel_size=" + (Get-Item -LiteralPath $sentinelFile).Length) }
if ($hits.Count -gt 0 -and $sentinel) { Write-Host 'MECHANISM_OK' } else { Write-Host 'MECHANISM_INCOMPLETE' }

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
