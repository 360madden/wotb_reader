# Isolate the -s mechanism: Mode trivial = just log a marker; Mode attach =
# attach a hex pid + sleep; report process liveness + UIA log + counter state.
param([ValidateSet('trivial', 'attach')][string]$Mode = 'trivial')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$T = $env:TEMP
$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Read-Log([int]$targetPid) {
    $win = $null
    for ($i = 0; $i -lt 8; $i++) {
        $pp = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if ($pp -and $pp.MainWindowHandle -ne [IntPtr]::Zero) { $win = $pp; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $win) { return "<no-window pid=$targetPid alive=$([bool](Get-Process -Id $targetPid -ErrorAction SilentlyContinue))>" }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($el in $all) {
        $n = $el.Current.Name
        if ($n -match 'ODWT_|Error|error|failed|Failed|Attach|attach|Script|script|Breakpoint|breakpoint|Process|process|Ready|Not') {
            $lines.Add($n)
        }
    }
    return ($lines | Select-Object -First 25) -join ' || '
}

# Counter target for attach mode
$target = $null
$progressFile = Join-Path $T 'wt-counter-progress.txt'
$exe = Join-Path $T 'wt-counter-target.exe'
$addrFile = Join-Path $T 'wt-counter-addr.txt'
if ($Mode -eq 'attach') {
    Remove-Item -LiteralPath $progressFile, $addrFile -ErrorAction SilentlyContinue
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
        while (true) { (*p)++; n++; if ((n % 40) == 0) File.WriteAllText(@"PROGRESS", (*p).ToString()); Thread.Sleep(25); }
    }
}
'@
        $src = $src.Replace('ADDRFILE', $addrFile).Replace('PROGRESS', $progressFile)
        Set-Content -LiteralPath $csFile -Value $src -Encoding ascii
        $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
        & $csc /nologo /target:exe /platform:x86 /unsafe /out:$exe $csFile 2>&1 | Out-Null
    }
    $target = Start-Process -FilePath $exe -PassThru
    Start-Sleep -Seconds 2
    Write-Host ("target_pid=" + $target.Id)
}

$scriptFile = Join-Path $T 'od-wt-isolate.script'
if ($Mode -eq 'trivial') {
    @('log "ODWT_ISOLATE_TRIVIAL_91"', 'sleep 2000', 'log "ODWT_ISOLATE_DONE_91"') | Set-Content -LiteralPath $scriptFile -Encoding ascii
}
else {
    @(
        'log "ODWT_ISOLATE_START"',
        ('attach 0x{0:X}' -f $target.Id),
        'sleep 3000',
        'log "ODWT_ISOLATE_AFTER_ATTACH"',
        'log "ODWT_ISOLATE_END"'
    ) | Set-Content -LiteralPath $scriptFile -Encoding ascii
}

Write-Host ("launching x32dbg -s mode=" + $Mode)
$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-s', $scriptFile) -PassThru
Write-Host ("debugger_pid=" + $dbgProc.Id)

Start-Sleep -Seconds 2
Write-Host ("alive@2s=" + [bool](Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue))
$log2 = Read-Log $dbgProc.Id
Write-Host ("log@2s: " + $log2)
Start-Sleep -Seconds 3
Write-Host ("alive@5s=" + [bool](Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue))
$log5 = Read-Log $dbgProc.Id
Write-Host ("log@5s: " + $log5)
Start-Sleep -Seconds 3
Write-Host ("alive@8s=" + [bool](Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue))
$log8 = Read-Log $dbgProc.Id
Write-Host ("log@8s: " + $log8)

if ($Mode -eq 'attach') {
    function GP { if (Test-Path -LiteralPath $progressFile) { (Get-Content -LiteralPath $progressFile -Raw).Trim() } else { 'none' } }
    $a = GP; Start-Sleep -Seconds 1; $b = GP
    Write-Host ("progress=" + $a + " -> " + $b + " " + $(if ($a -eq $b) { 'PAUSED' } else { 'RUNNING' }))
    Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
}
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
