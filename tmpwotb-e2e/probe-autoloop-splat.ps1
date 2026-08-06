# Probe: replicate od-049-autoloop.ps1's hashtable splat EXACTLY (both with
# and without -AttachSmokeOnFirstRound) and confirm od-048 binds every param
# without a ParameterBindingException. With no game/host running, od-048 must
# fail at its own preflight stage (FAILED_no_rendezvous / exit 1) - a
# binding error would be exit 1 with a different message or a thrown
# ParameterBindingException in the output.
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$od048 = Join-Path $repo 'scripts\od-048-monitor-correlate-session.ps1'
$markerUtc = '2026-08-06T00:00:00Z'

function Test-Splat([bool]$WithSmoke) {
    $m1Args = @{
        ReplayStartWallTimeUtc = $markerUtc
        MaxReadRounds          = 5
        StageTopN              = 2
        StageDelaySeconds      = 2
        AutoWriteTraceOnVerdict = $true
        AutoTraceSeconds       = 25
        ResultPath             = (Join-Path $repo '.data\od-049-splat-probe.json')
    }
    if ($WithSmoke) { $m1Args.AttachSmokeOnFirstRound = $true }
    # Capture all streams (Write-Host in od-048 goes to the info stream 6,
    # which 2>&1 alone misses; *>&1 redirects every stream).
    $out = & $od048 @m1Args *>&1
    $exit = $LASTEXITCODE
    $bindErr = $out | Where-Object { $_ -match 'ParameterBindingException|Cannot bind|Parameter set cannot be resolved' }
    $fatal = $out | Where-Object { $_ -match 'FAILED_no_rendezvous|FAILED_gate_never_verified|preflight_start' } | Select-Object -First 1
    Write-Host ("splat smoke=" + $WithSmoke + " exit=" + $exit + " bindErr=" + [bool]$bindErr + " stage='" + $fatal + "'")
    if ($bindErr) { return $false }
    # Expect the no-host/no-game preflight failure (exit 1), NOT a bind error.
    return ($exit -eq 1 -and [bool]$fatal)
}

$ok1 = Test-Splat -WithSmoke $false
$ok2 = Test-Splat -WithSmoke $true
if ($ok1 -and $ok2) { Write-Host 'SPLAT_PROBE_PASS'; exit 0 }
Write-Host 'SPLAT_PROBE_FAIL'
exit 1
