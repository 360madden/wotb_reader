#Requires -Version 5.1
<#
.SYNOPSIS
  G1 offline mechanism test (offset-promotion-checklist Mechanism A,
  write-observation): proves the guard-page interceptor family
  (tools/WriteInterceptor) observes EVERY write to a position page and can
  discriminate a no-write window, so a live poll can honestly conclude "no
  write to the ring record's position bytes occurred across a read window".

.DESCRIPTION
  Drives the interceptor's own synthetic counter mode (writes a float via a
  real CRT memcpy at a 25 ms cadence; the value increases exactly 0.5 per
  iteration, so a captured value sequence with no 0.5-gap is proof that no
  write was missed) and its interceptor mode (arm PAGE_GUARD on the page
  holding the published address, capture every write with post-write value,
  i386 registers, and module RVA). Asserts:

    1. Capture completeness - consecutive captured values differ by exactly
       0.5 and increase strictly (a missed write would skip a step).
    2. No-write-window discrimination - with the counter process suspended
       mid-window, zero hits land in the suspended span (a broken attach
       would fail to arm/attach or fail the liveness checks).
    3. Liveness both sides - hits exist before the suspension and after it,
       proving the zero-hit span is a real no-write, not a dead capture.
    4. Attribution - hits are kind=member, resolve to a module RVA (the CRT
       memcpy copy loop), and carry the i386 esi/edi registers of the copy
       ABI (the game's coordinate is copied by the same VCRUNTIME memcpy).
    5. Double discriminator (2026-08-10 regression) - a second counter in
       --double mode publishes an 8-byte Double replayTime-mimic (advances
       0.016s/frame); the interceptor with -ValueSize 8 must capture a
       stream of DISTINCT 8-byte valueHex patterns. A float-epsilon
       discriminator would miss every write (the low dword as float is a
       ~1e-38 denormal), so this phase pins the byte-exact compare that the
       OD-044 replayTime plan depends on.

  Offline only: no game, no live poll, no product code touched. The G1 live
  poll (one new approved session) applies the same machinery to the
  ring-record position page.

  Evidence lands under .data/diagnostics/g1-mechanism-<timestamp>/ (raw
  interceptor report + this run's summary); .data/ is gitignored, so the
  durable record is the summary quoted in the ledger / promotion checklist.

  Requires the x86 publish (auto-built if missing):
    dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor

.EXITCODES
  0  All assertions passed.
  1  An assertion failed (FAIL_* lines name the failure).
  2  Usage / environment error (missing publish and auto-publish failed).
#>
[CmdletBinding()]
param(
    # Total interceptor capture window in seconds.
    [int]$Seconds = 8,
    # How long the counter process stays suspended mid-window.
    [int]$SuspendSeconds = 2,
    # Seconds of warmup before suspending the counter (attach + arm + hits).
    [int]$WarmupSeconds = 3,
    # Override the interceptor publish path (default: repo .build publish).
    [string]$ExePath = '',
    # Override the evidence output directory (default: .data/diagnostics).
    [string]$OutDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:counterId = 0
$script:interceptorId = 0

function Write-Test([string]$Message) {
    Write-Host ("g1wot: " + $Message)
}

function Cleanup([int]$code) {
    if ($script:interceptorId -ne 0) {
        Stop-Process -Id $script:interceptorId -Force -ErrorAction SilentlyContinue
    }
    if ($script:counterId -ne 0) {
        Stop-Process -Id $script:counterId -Force -ErrorAction SilentlyContinue
    }
    exit $code
}

if (-not $ExePath) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
    $ExePath = Join-Path $RepoRoot '.build/publish/write-interceptor/WotBTreader.WriteInterceptor.exe'
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Test 'MISSING_EXE building x86 publish'
    $repo = (Resolve-Path (Join-Path (Split-Path -Parent $ExePath) '..\..\..')).Path
    Push-Location $repo
    try {
        & dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor
        if ($LASTEXITCODE -ne 0) {
            Write-Test 'FAIL_publish_build'
            Cleanup 2
        }
    }
    finally {
        Pop-Location
    }
    if (-not (Test-Path -LiteralPath $ExePath)) {
        Write-Test 'FAIL_publish_missing_after_build'
        Cleanup 2
    }
}

if (-not $OutDir) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
    $OutDir = Join-Path $RepoRoot (Join-Path '.data/diagnostics' ('g1-mechanism-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Test ('evidence_dir=' + $OutDir)

$addrFile = Join-Path $OutDir 'counter-addr.txt'
$progressFile = Join-Path $OutDir 'counter-progress.txt'
$reportPath = Join-Path $OutDir 'interceptor-report.json'
$summaryPath = Join-Path $OutDir 'summary.txt'

try {
    # 1. Start the synthetic counter and read its published Dest address.
    $counter = Start-Process -FilePath $ExePath -ArgumentList @('--counter', '-AddrFile', $addrFile, '-ProgressFile', $progressFile) -PassThru -WindowStyle Hidden
    $script:counterId = $counter.Id
    Write-Test ('counter_started pid=' + $counter.Id)

    $addr = $null
    for ($i = 0; $i -lt 40 -and -not $addr; $i++) {
        if (Test-Path -LiteralPath $addrFile) {
            $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim()
        }
        if (-not $addr) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $addr) {
        Write-Test 'FAIL_no_published_address'
        Cleanup 1
    }
    Write-Test ('counter_address=0x' + $addr)

    # 2. Arm the interceptor on that page for the full window.
    $interceptorArgs = @('--interceptor', '-Pid', ([string]$counter.Id), '-Addresses', ('0x' + $addr), '-Seconds', ([string]$Seconds), '-Out', $reportPath)
    $interceptor = Start-Process -FilePath $ExePath -ArgumentList $interceptorArgs -PassThru -WindowStyle Hidden
    $script:interceptorId = $interceptor.Id
    Write-Test ('interceptor_started pid=' + $interceptor.Id)

    # Warmup: attach + arm + a steady stream of captured writes.
    Start-Sleep -Seconds $WarmupSeconds

    # 3. Suspend the counter mid-window (the no-write span under test).
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class G1SuspendNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr hProcess);
    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr hProcess);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
}
'@
    $tSuspend = [DateTimeOffset]::UtcNow
    $h = [G1SuspendNative]::OpenProcess(0x0800, $false, $counter.Id)  # PROCESS_SUSPEND_RESUME
    if ($h -eq [IntPtr]::Zero) {
        Write-Test ('FAIL_open_process win32=' + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
        Cleanup 1
    }
    [void][G1SuspendNative]::NtSuspendProcess($h)
    Write-Test ('counter_suspended at=' + $tSuspend.ToString('o'))
    Start-Sleep -Seconds $SuspendSeconds
    [void][G1SuspendNative]::NtResumeProcess($h)
    [void][G1SuspendNative]::CloseHandle($h)
    $tResume = [DateTimeOffset]::UtcNow
    Write-Test ('counter_resumed at=' + $tResume.ToString('o'))

    # 4. Wait for the interceptor window to complete.
    try {
        Wait-Process -Id $interceptor.Id -Timeout 90 -ErrorAction Stop
    }
    catch {
        Write-Test 'FAIL_interceptor_timeout'
        Cleanup 1
    }
    $script:interceptorId = 0
    $interceptorExit = $interceptor.ExitCode
    Write-Test ('interceptor_exit=' + $interceptorExit)

    if (-not (Test-Path -LiteralPath $reportPath)) {
        Write-Test 'FAIL_missing_report'
        Cleanup 1
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $failures = New-Object System.Collections.Generic.List[string]

    # 5. Assertions.
    if ($report.exitCode -ne 0) { $failures.Add('interceptor_report_exit=' + $report.exitCode) }
    if ($interceptorExit -ne 0) { $failures.Add('interceptor_process_exit=' + $interceptorExit) }
    if ($report.pagesArmed -lt 1) { $failures.Add('no_page_armed') }

    $hits = @()
    if ($null -ne $report.hits) { $hits = @($report.hits) }
    Write-Test ('hits=' + $hits.Count + ' guard_events=' + $report.guardEvents)

    if ($hits.Count -lt 30) { $failures.Add('hit_count_too_low=' + $hits.Count) }

    # Completeness: consecutive captured values differ by exactly 0.5 and
    # increase strictly (the counter's deterministic progression).
    if ($hits.Count -ge 2) {
        for ($i = 1; $i -lt $hits.Count; $i++) {
            $delta = [double]$hits[$i].value - [double]$hits[$i - 1].value
            if ([math]::Abs($delta - 0.5) -gt 0.0001) {
                $failures.Add(('value_progression_gap at=' + $i + ' delta=' + $delta))
                break
            }
        }
    }

    # No-write window: zero hits inside the suspended span (trimmed for
    # in-flight boundary events), and liveness on both sides.
    $inWindow = @($hits | Where-Object { [DateTimeOffset]$_.utc -gt $tSuspend.AddSeconds(0.25) -and [DateTimeOffset]$_.utc -lt $tResume.AddSeconds(-0.25) })
    $beforeCount = @($hits | Where-Object { [DateTimeOffset]$_.utc -lt $tSuspend.AddSeconds(-0.5) }).Count
    $afterCount = @($hits | Where-Object { [DateTimeOffset]$_.utc -gt $tResume.AddSeconds(0.5) }).Count
    if ($inWindow.Count -ne 0) { $failures.Add('hits_in_suspended_window=' + $inWindow.Count) }
    if ($beforeCount -lt 1) { $failures.Add('no_hits_before_suspend') }
    if ($afterCount -lt 1) { $failures.Add('no_hits_after_resume') }

    # Attribution: member kind, module RVA, and the i386 esi/edi copy ABI.
    $attributed = @($hits | Where-Object { $_.kind -eq 'member' -and $_.rva -and $_.rva -ne 'jit' -and $null -ne $_.registers -and $null -ne $_.registers.esi -and $null -ne $_.registers.edi }).Count
    if ($hits.Count -gt 0 -and ($attributed / $hits.Count) -lt 0.8) {
        $failures.Add('attribution_below_80pct=' + [math]::Round(100.0 * $attributed / $hits.Count, 1))
    }

    # 5. Double (replayTime-mimic) capture - the -ValueSize 8 discriminator
    #    regression (2026-08-10). A monotonic 8-byte Double advances 0.016s/
    #    frame; its low dword reinterpreted as float is a ~1e-38 denormal, so
    #    a float-epsilon discriminator misses EVERY write. The byte-exact
    #    compare must capture a stream of DISTINCT 8-byte patterns (a missed
    #    write would collapse two steps onto one distinct value).
    Write-Test 'double_mode_capture start'
    $doubleCounter = Start-Process -FilePath $ExePath -ArgumentList @('--counter', '-AddrFile', (Join-Path $OutDir 'double-addr.txt'), '-ProgressFile', (Join-Path $OutDir 'double-prog.txt'), '--double') -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    $doubleAddr = (Get-Content -LiteralPath (Join-Path $OutDir 'double-addr.txt') -Raw).Trim()
    if (-not $doubleAddr) {
        Stop-Process -Id $doubleCounter.Id -Force -ErrorAction SilentlyContinue
        $failures.Add('double_no_published_address')
    }
    else {
        $doubleReport = Join-Path $OutDir 'interceptor-double-report.json'
        $doubleInterceptor = Start-Process -FilePath $ExePath -ArgumentList @('--interceptor', '-Pid', ([string]$doubleCounter.Id), '-Addresses', ('0x' + $doubleAddr), '-Seconds', '4', '-ValueSize', '8', '-Out', $doubleReport) -PassThru -WindowStyle Hidden
        try {
            Wait-Process -Id $doubleInterceptor.Id -Timeout 60 -ErrorAction Stop
        }
        catch {
            $failures.Add('double_interceptor_timeout')
        }
        Stop-Process -Id $doubleCounter.Id -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $doubleReport) {
            $doubleReportDoc = Get-Content -LiteralPath $doubleReport -Raw | ConvertFrom-Json
            $doubleHits = @()
            if ($null -ne $doubleReportDoc.hits) { $doubleHits = @($doubleReportDoc.hits) }
            $distinctHex = @($doubleHits | ForEach-Object { [string]$_.valueHex } | Sort-Object -Unique).Count
            Write-Test ('double_hits=' + $doubleHits.Count + ' distinct_hex=' + $distinctHex)
            if ($doubleHits.Count -lt 20) { $failures.Add('double_hits_too_low=' + $doubleHits.Count) }
            if ($distinctHex -lt 20) { $failures.Add('double_distinct_values_too_low=' + $distinctHex) }
            # Every hit must carry the exact 8-byte hex (16 chars) - the
            # byte-exact discriminator's output contract.
            $badHex = @($doubleHits | Where-Object { [string]$_.valueHex -notmatch '^[0-9A-Fa-f]{16}$' }).Count
            if ($badHex -gt 0) { $failures.Add('double_bad_valueHex=' + $badHex) }
        }
        else {
            $failures.Add('double_missing_report')
        }
    }
    Write-Test 'double_mode_capture end'

    # 6. Summary + verdict.
    $progressionOk = $failures.Count -eq 0
    $summaryLines = @(
        'g1 offline write-observation mechanism test',
        'date=' + (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ'),
        'interceptor_exit=' + $interceptorExit,
        'report_exit=' + $report.exitCode,
        'pages_armed=' + $report.pagesArmed,
        'guard_events=' + $report.guardEvents,
        'hits=' + $hits.Count,
        'hits_before_suspend=' + $beforeCount,
        'hits_after_resume=' + $afterCount,
        'hits_in_suspended_window=' + $inWindow.Count,
        'attributed_member_rva_esi_edi=' + $attributed,
        'all_assertions=' + $(if ($progressionOk) { 'pass' } else { 'fail' })
    )
    [System.IO.File]::WriteAllLines($summaryPath, $summaryLines)

    if ($failures.Count -gt 0) {
        Write-Test 'RESULT=FAIL'
        foreach ($f in $failures) { Write-Test ('FAIL_' + $f) }
        Cleanup 1
    }

    Write-Test ('RESULT=PASS hits=' + $hits.Count + ' before=' + $beforeCount + ' after=' + $afterCount + ' suspended_window=0 attributed=' + $attributed)
    Cleanup 0
}
catch {
    Write-Test ('FAIL_unexpected ' + $_.Exception.Message)
    Cleanup 1
}
