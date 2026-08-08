# Synthetic-only validation for the instruction-first x86 helper.
# No game, replay, host, private artifact, or externally supplied PID is used.
param(
    [string]$Exe = 'C:\work\wotb_reader\.build\publish\instruction-snapshot-helper\WotBTreader.InstructionSnapshotHelper.exe'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host 'MISSING_EXE_publish_first'
    exit 1
}

$reportPath = Join-Path '.build' ('execute-snapshot-synthetic-' + [guid]::NewGuid().ToString('N') + '.json')
& $Exe '--snapshot-self-test' '-Out' $reportPath | Out-Null
$captureExit = $LASTEXITCODE
if ($captureExit -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) {
    Write-Host ('FAIL_self_test exit=' + $captureExit)
    exit 1
}

$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$hits = @($report.hits)
$distinctX = @($hits | ForEach-Object { $_.vector.x } | Select-Object -Unique)

if ([string]$report.schema -ne 'wotbtreader.execute-object-snapshot.v1') { Write-Host 'FAIL_schema'; exit 1 }
if ([string]$report.mode -ne 'execute-object-snapshot') { Write-Host 'FAIL_mode'; exit 1 }
if ([string]$report.status -ne 'completed') { Write-Host 'FAIL_status'; exit 1 }
if (-not [bool]$report.target.instructionMatched) { Write-Host 'FAIL_instruction_match'; exit 1 }
if ([string]$report.target.expectedInstructionHex -ne '8B83A0000000') { Write-Host 'FAIL_instruction_pin'; exit 1 }
if ([int]$report.threadsArmed -lt 1) { Write-Host 'FAIL_no_thread_armed'; exit 1 }
if ([int]$report.maxThreads -ne 256) { Write-Host 'FAIL_thread_bound'; exit 1 }
if ([int]$report.threadsRestored -lt 1) { Write-Host 'FAIL_no_thread_restored'; exit 1 }
if (-not [bool]$report.cleanupProven -or -not [bool]$report.detached) { Write-Host 'FAIL_cleanup'; exit 1 }
if (-not [bool]$report.coordinatorIdentityPinned) { Write-Host 'FAIL_coordinator_not_pinned'; exit 1 }
if (-not [bool]$report.debuggerExitTerminatesTarget) { Write-Host 'FAIL_crash_containment'; exit 1 }
if ($hits.Count -ne 4 -or -not [bool]$report.truncated) { Write-Host 'FAIL_hit_bound'; exit 1 }
if ($distinctX.Count -lt 2) { Write-Host 'FAIL_values_static'; exit 1 }

foreach ($hit in $hits) {
    if (-not [bool]$hit.vector.readOk -or -not [bool]$hit.vector.finite) { Write-Host 'FAIL_vector'; exit 1 }
    if ([int]$hit.vector.bytesRead -ne 12) { Write-Host 'FAIL_read_width'; exit 1 }
    if (-not [bool]$hit.sameDebugEvent -or -not [bool]$hit.debugEventProcessSuspended) { Write-Host 'FAIL_event_provenance'; exit 1 }
    if (-not [bool]$hit.singleRead12Bytes) { Write-Host 'FAIL_single_read'; exit 1 }
    if ([bool]$hit.hardwareAtomicReadProven -or [bool]$hit.sameDecodedClockProven -or
        [bool]$hit.viewpointIdentityProven -or [bool]$hit.stableRootProven) {
        Write-Host 'FAIL_overclaim'
        exit 1
    }
}

if ((Get-Item -LiteralPath $reportPath).Length -gt 65536) { Write-Host 'FAIL_report_bound'; exit 1 }

# Production mode accepts only inherited pipe capabilities. Raw target switches
# are intentionally not part of its command-line contract.
& $Exe '--execute-object-snapshot' '-Pid' '1' | Out-Null
$rawPidExit = $LASTEXITCODE
if ($rawPidExit -ne 2) { Write-Host ('FAIL_raw_pid_not_rejected exit=' + $rawPidExit); exit 1 }

& $Exe '--interceptor' '-Pid' '1' '-Addresses' '0x1' '-Seconds' '1' '-Out' 'ignored.json' | Out-Null
$legacyModeExit = $LASTEXITCODE
if ($legacyModeExit -ne 2) { Write-Host ('FAIL_legacy_mode_present exit=' + $legacyModeExit); exit 1 }

$hostOutput = Join-Path $PSScriptRoot '..\src\WotBTreader.Host.Web\bin\Release\net10.0'
$hostExe = Join-Path $hostOutput 'WotBTreader.Host.Web.exe'
$hostDll = Join-Path $hostOutput 'WotBTreader.Host.Web.dll'
$verificationNonce = [Guid]::NewGuid().ToString('N')
$verificationJson = & $Exe '--verify-coordinator-file' '-Path' $hostExe `
    '-AssemblyPath' $hostDll '-Nonce' $verificationNonce
$verificationExit = $LASTEXITCODE
$verification = $verificationJson | ConvertFrom-Json
if ($verificationExit -ne 0 -or
    [string]$verification.schema -ne 'wotbtreader.instruction-snapshot-helper.verify.v1' -or
    [string]$verification.nonce -ne $verificationNonce -or -not [bool]$verification.verified) {
    Write-Host 'FAIL_coordinator_verification_contract'
    exit 1
}

# A caller-created pipe set is not authorization. Even with a structurally
# valid, server-pinned game target, the helper must reject this PowerShell
# parent before any target process handle or debugger attach is attempted.
$planPipe = New-Object IO.Pipes.AnonymousPipeServerStream(
    [IO.Pipes.PipeDirection]::Out, [IO.HandleInheritability]::Inheritable)
$resultPipe = New-Object IO.Pipes.AnonymousPipeServerStream(
    [IO.Pipes.PipeDirection]::In, [IO.HandleInheritability]::Inheritable)
$cancelPipe = New-Object IO.Pipes.AnonymousPipeServerStream(
    [IO.Pipes.PipeDirection]::Out, [IO.HandleInheritability]::Inheritable)
try {
    $current = Get-Process -Id $PID
    $fakePlan = @{
        coordinatorProcessId = $PID
        coordinatorProcessStartIdentity = $current.StartTime.ToUniversalTime().ToFileTimeUtc()
        coordinatorCanonicalExecutablePath = $current.Path
        coordinatorExecutableSha256 = (Get-FileHash -LiteralPath $current.Path -Algorithm SHA256).Hash
        coordinatorManagedAssemblyPath = $hostDll
        coordinatorManagedAssemblySha256 = (Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash
        processId = 1
        processStartIdentity = 1
        canonicalExecutablePath = 'C:\invalid\wotblitz.exe'
        productVersion = '11.19.0.10'
        executableSha256 = '1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d'
        moduleName = 'wotblitz.exe'
        rva = 8141227
        expectedInstructionHex = '8B83A0000000'
        objectDisplacement = 144
        durationMilliseconds = 1000
        maxHits = 1
        minimumObjectSampleIntervalMilliseconds = 750
    }
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Exe
    $startInfo.Arguments = '--execute-object-snapshot -PlanPipe ' +
        $planPipe.GetClientHandleAsString() + ' -ResultPipe ' +
        $resultPipe.GetClientHandleAsString() + ' -CancelPipe ' +
        $cancelPipe.GetClientHandleAsString()
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $direct = [Diagnostics.Process]::Start($startInfo)
    $planPipe.DisposeLocalCopyOfClientHandle()
    $resultPipe.DisposeLocalCopyOfClientHandle()
    $cancelPipe.DisposeLocalCopyOfClientHandle()
    $writer = New-Object IO.StreamWriter($planPipe, (New-Object Text.UTF8Encoding($false)))
    $writer.Write(($fakePlan | ConvertTo-Json -Compress))
    $writer.Dispose()
    $reader = New-Object IO.StreamReader($resultPipe)
    $directReportJson = $reader.ReadToEnd()
    $reader.Dispose()
    if (-not $direct.WaitForExit(5000)) { $direct.Kill(); Write-Host 'FAIL_direct_parent_timeout'; exit 1 }
    $directReport = $directReportJson | ConvertFrom-Json
    if ($direct.ExitCode -ne 2 -or [bool]$directReport.attached -or
        -not [bool]$directReport.cleanupProven -or -not [bool]$directReport.detached) {
        Write-Host 'FAIL_direct_parent_not_rejected'
        exit 1
    }
    if (@($directReport.diagnostics) -notcontains 'coordinator_identity_invalid') {
        Write-Host 'FAIL_direct_parent_reason'
        exit 1
    }
}
finally {
    $planPipe.Dispose()
    $resultPipe.Dispose()
    $cancelPipe.Dispose()
}

Write-Host ('PASS_execute_snapshot hits=' + $hits.Count +
    ' threads_armed=' + $report.threadsArmed +
    ' threads_restored=' + $report.threadsRestored +
    ' distinct_x=' + $distinctX.Count)
exit 0
