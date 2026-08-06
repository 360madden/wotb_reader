# Launch x32dbg with -s <diagnostic script> and READ THE ENGINE LOG via UIA
# to see exactly what the script engine did at each step.
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

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

# ---- Diagnostic script ----------------------------------------------------
$scriptFile = Join-Path $T 'od-wt-diag.script'
$hit1 = Join-Path $hitsDir ("odwt-" + $addr + "-{rip}.bin")
$script = @(
    'log "ODWT_S_START"',
    ('attach 0x{0:X}' -f $target.Id),
    'sleep 3000',
    'log "ODWT_S_AFTER_ATTACH"',
    ("bph " + $addr + ",w,4"),
    'log "ODWT_S_AFTER_BPH"',
    ('bphwlog ' + $addr + ', "ODWT_HIT addr=' + $addr + ' rip={rip}"'),
    ('SetHardwareBreakpointCommand ' + $addr + ', "savedata ' + $hit1 + ' rip 64"'),
    ('SetHardwareBreakpointFastResume ' + $addr),
    'log "ODWT_S_ARMED"',
    'run',
    'sleep 1000',
    'log "ODWT_S_RUN_DONE"',
    ('savedata "' + $sentinelFile + '", ' + $addr + ', 4'),
    'log "ODWT_S_END"'
)
$script | Set-Content -LiteralPath $scriptFile -Encoding ascii
Write-Host ("script_lines=" + $script.Count)

function Get-UiaLog([int]$dbgPid, [string]$label) {
    $win = $null
    for ($i = 0; $i -lt 10; $i++) {
        $pp = Get-Process -Id $dbgPid -ErrorAction SilentlyContinue
        if ($pp -and $pp.MainWindowHandle -ne [IntPtr]::Zero) { $win = $pp; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $win) { Write-Host ("[" + $label + "] NO_WINDOW pid=" + $dbgPid + " alive=" + [bool](Get-Process -Id $dbgPid -ErrorAction SilentlyContinue)); return }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $interesting = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -match 'ODWT_|attach|Attach|Attached|error|Error|failed|Failed|script|Script|breakpoint|Breakpoint|savedata|Unable|Invalid|debug|Debug|Process') {
            $interesting.Add(("[{0}] {1}" -f ($el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''), $n))
        }
    }
    Write-Host ("=== LOG LINES [" + $label + "] ===")
    $interesting | Select-Object -First 40 | ForEach-Object { Write-Host ('  ' + $_) }
}

$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-s', $scriptFile) -PassThru
Write-Host ("debugger_pid=" + $dbgProc.Id)

# Sample the pause state + log at ~3.5s (mid attach-sleep, before run)
Start-Sleep -Seconds 3
$p1 = Get-Progress
Start-Sleep -Seconds 1
$p2 = Get-Progress
Write-Host ("progress_during=" + $p1 + " -> " + $p2 + " " + $(if ($p1 -eq $p2) { '(PAUSED)' } else { '(RUNNING)' }))
Get-UiaLog $dbgProc.Id 'mid-sleep'

Start-Sleep -Seconds 2
$p3 = Get-Progress
Start-Sleep -Seconds 1
$p4 = Get-Progress
Write-Host ("progress_late=" + $p3 + " -> " + $p4 + " " + $(if ($p3 -eq $p4) { '(PAUSED)' } else { '(RUNNING)' }))
Get-UiaLog $dbgProc.Id 'post-run'

# ---- UIA read the log ------------------------------------------------------
$win = $null
for ($i = 0; $i -lt 10; $i++) {
    $pp = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($pp -and $pp.MainWindowHandle -ne [IntPtr]::Zero) { $win = $pp; break }
    Start-Sleep -Milliseconds 500
}
if ($win) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $interesting = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -match 'ODWT_|attach|Attach|Attached|error|Error|failed|Failed|script|Script|breakpoint|Breakpoint|savedata|run |sleep|Process|Debugging|Unable|Invalid') {
            $interesting.Add(("[{0}] {1}" -f ($el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''), $n))
        }
    }
    Write-Host '=== LOG LINES (UIA) ==='
    $interesting | Select-Object -First 40 | ForEach-Object { Write-Host ('  ' + $_) }
}
else { Write-Host 'NO_WINDOW_FOR_LOG_READ' }

$hits = @(Get-ChildItem -LiteralPath $hitsDir -Filter 'odwt-*.bin' -File -ErrorAction SilentlyContinue)
Write-Host ("hits=" + $hits.Count + " sentinel=" + (Test-Path -LiteralPath $sentinelFile))

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
