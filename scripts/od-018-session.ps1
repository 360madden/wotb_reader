#Requires -Version 5.1
# Temporary OD session driver (untracked): wait for the verified gate, then
# pre-arm the debugger and run the rolling increased driver within the 120s lease.
param(
    [int]$WaitVerifiedSeconds = 300,
    [int]$TransitionSeconds = 3,
    # OD-034 two-phase transition passthrough to the rolling driver: below
    # TailThreshold survivors the pulse drops to TailTransitionSeconds so more
    # tail rounds fit the fixed 120s lease (replayTime advances every frame,
    # so a short tail pulse still registers increases).
    [int]$TailThreshold = 200,
    [int]$TailTransitionSeconds = 1,
    # OD-035 snapshot retained-byte budget passthrough (0 = engine ceiling,
    # unchanged). A positive budget shrinks the round-1 66M walk to open the
    # operator window with lease to spare.
    [long]$SnapshotMaxBytes = 0,
    # OD-035 round-limit passthrough: with the snapshot budget the rounds are
    # cheap (1s tail pulses), so raising MaxRounds past 22 lets the tail keep
    # pulsing instead of stopping at the old 22-round cap.
    [int]$MaxRounds = 22,
    [string]$TargetSurvivors = '10',
    [string]$AddressFile = '',
    # After rolling lands <=10, hold this many seconds while polling the gate
    # so the operator can run interactive Find-what-writes on the staged
    # survivors inside the live green window (OD-024). The loop exits early on
    # gate loss, so this is a cap, not a hard timer: OD-027 showed the gate
    # stays green past 60s, so a longer cap gives the operator the whole
    # remaining lease. 0 = no hold.
    [int]$HoldAfterRollSeconds = 240,
    # Replace the operator prompt with the automated x64dbg write-trace
    # (scripts/x64dbg-write-trace.ps1) during the held green window. The
    # trace script reads the staged survivors, arms bphw write breakpoints in
    # the pre-armed x64dbg via command-bar injection, and captures the writing
    # RIP to %TEMP%\od-wt-hits.txt - no operator at the keyboard.
    [switch]$AutoWriteTrace,
    # Time-box for the automated write-trace (capped by the hold window).
    [int]$WriteTraceSeconds = 120,
    # Legacy blind pre-snapshot wait. OD-026 moved steady-state detection into
    # the rolling driver (snapshot candidate-count sanity gate with discard +
    # gate-aware retry), so this default is 0. Keep the param for manual use;
    # a blind wait burns lease without measuring the game state.
    [int]$PreSnapshotSettleSeconds = 0,
    [string]$RepoRoot = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

if ([string]::IsNullOrWhiteSpace($AddressFile)) {
    $AddressFile = Join-Path $env:TEMP 'od-survivors.txt'
}

# Freshness prerequisite for the x64dbg staging: the address file must not
# exist before rolling, or a stale survivor set from a prior session could be
# staged in the debugger (OD-RECOVERY-020 review finding).
Remove-Item -LiteralPath $AddressFile -Force -ErrorAction SilentlyContinue

function Get-Rv {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $f = Get-ChildItem $dir -File -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return $null }
    return (Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json)
}

function Get-Gate($rv) {
    try {
        return Invoke-RestMethod -Uri ($rv.baseUri + '/api/v1/game/state') -Headers @{
            'X-WotBTreader-Capability' = [string]$rv.capability
        }
    }
    catch { return $null }
}

Write-Host 'od018: waiting_for_verified_gate'
$deadline = (Get-Date).AddSeconds($WaitVerifiedSeconds)
$state = $null
while ((Get-Date) -lt $deadline) {
    try { $rv = Get-Rv } catch { $rv = $null }
    if ($rv) { $state = Get-Gate $rv }
    if ($state -and $state.verificationState -eq 'OfflineReplayVerified') {
        Write-Host 'od018: gate=OfflineReplayVerified'
        break
    }
    $vs = if ($state) { [string]$state.verificationState } else { 'no-host' }
    Write-Host ("od018: waiting gate=" + $vs)
    Start-Sleep -Seconds 5
}
if (-not $state -or $state.verificationState -ne 'OfflineReplayVerified') {
    Write-Host 'od018: FAILED gate_never_verified'
    exit 3
}

$preArm = Join-Path $RepoRoot 'scripts\pre-arm-debugger.ps1'
Write-Host 'od018: prearm_start'
& $preArm -AutoAttach
Write-Host ("od018: prearm_exit=" + $LASTEXITCODE)

if ($PreSnapshotSettleSeconds -gt 0) {
    Write-Host ("od018: settle_before_snapshot=" + $PreSnapshotSettleSeconds + "s")
    $settleDeadline = (Get-Date).AddSeconds($PreSnapshotSettleSeconds)
    while ((Get-Date) -lt $settleDeadline) {
        try { $rv = Get-Rv } catch { $rv = $null }
        $st = if ($rv) { Get-Gate $rv } else { $null }
        $vs = if ($st) { [string]$st.verificationState } else { 'no-host' }
        if ($vs -ne 'OfflineReplayVerified') {
            Write-Host ("od018: settle_aborted gate=" + $vs)
            exit 4
        }
        Start-Sleep -Seconds 3
    }
    Write-Host 'od018: settle_done gate=OfflineReplayVerified'
}

$roll = Join-Path $RepoRoot 'scripts\roll-replay-time-increased.ps1'
Remove-Item -LiteralPath $AddressFile -Force -ErrorAction SilentlyContinue
Write-Host 'od018: rolling_start'
& $roll -TargetSurvivors $TargetSurvivors -TransitionSeconds $TransitionSeconds -TailThreshold $TailThreshold -TailTransitionSeconds $TailTransitionSeconds -SnapshotMaxBytes $SnapshotMaxBytes -MaxRounds $MaxRounds -AddressFile $AddressFile
Write-Host ("od018: rolling_exit=" + $LASTEXITCODE)
Write-Host ("od018: addresses=" + $AddressFile)
if ($LASTEXITCODE -ne 0) {
    # Diagnose a failed roll: a 400 from a discarded scan session usually
    # means the lease expired mid-roll (gate no longer verified), not an API
    # fault. Report the gate so the operator can distinguish the two.
    try { $rv = Get-Rv } catch { $rv = $null }
    $post = if ($rv) { Get-Gate $rv } else { $null }
    Write-Host ("od018: post_roll_gate=" + $(if ($post) { $post.verificationState } else { 'no-host' }))
}

if ($HoldAfterRollSeconds -gt 0) {
    if ($AutoWriteTrace) {
        $traceSecs = [Math]::Min($WriteTraceSeconds, $HoldAfterRollSeconds)
        Write-Host ("od018: AUTOMATED WRITE-TRACE for up to " + $traceSecs + "s via x64dbg-write-trace.ps1 (no operator step)")
        $wt = Join-Path $RepoRoot 'scripts\x64dbg-write-trace.ps1'
        & $wt -SurvivorFile $AddressFile -TraceSeconds $traceSecs
        Write-Host ("od018: write_trace_exit=" + $LASTEXITCODE)
        if (Test-Path -LiteralPath (Join-Path $env:TEMP 'od-wt-hits.txt')) {
            $hitLines = @(Get-Content -LiteralPath (Join-Path $env:TEMP 'od-wt-hits.txt') -ErrorAction SilentlyContinue | Where-Object { $_ })
            Write-Host ("od018: write_trace_hits=" + $hitLines.Count + " evidence=%TEMP%\od-wt-hits.txt")
        }
    }
    else {
        Write-Host ("od018: OPERATOR WINDOW OPEN for up to " + $HoldAfterRollSeconds + "s - in x64dbg (pre-armed attached), load %TEMP%\od-survivors.txt and set a hardware write breakpoint (bphw) on each survivor address, then let the replay play.")
    }
    $holdDeadline = (Get-Date).AddSeconds($HoldAfterRollSeconds)
    # Start the announce clock at now so the first periodic re-announce does
    # not fire immediately after the OPEN line (double-announce at open).
    $lastAnnounce = Get-Date
    while ((Get-Date) -lt $holdDeadline) {
        try { $rv = Get-Rv } catch { $rv = $null }
        $st = if ($rv) { Get-Gate $rv } else { $null }
        $vs = if ($st) { [string]$st.verificationState } else { 'no-host' }
        if ($vs -ne 'OfflineReplayVerified') {
            Write-Host ("od018: operator_window_closed gate=" + $vs)
            break
        }
        # Re-announce periodically so the instruction stays visible on the
        # live transcript for the whole window (OD-028).
        if (((Get-Date) - $lastAnnounce).TotalSeconds -ge 30) {
            if ($AutoWriteTrace) {
                Write-Host ("od018: HOLD WINDOW STILL OPEN (gate=" + $vs + ") - automated write-trace completed; holding for the remaining lease")
            }
            else {
                Write-Host ("od018: OPERATOR WINDOW STILL OPEN (gate=" + $vs + ") - in x64dbg, set a hardware write breakpoint (bphw) on each od-survivors.txt address, then let the replay play.")
            }
            $lastAnnounce = Get-Date
        }
        Start-Sleep -Seconds 2
    }
    try { $rv = Get-Rv } catch { $rv = $null }
    $final = if ($rv) { Get-Gate $rv } else { $null }
    Write-Host ("od018: operator_window_final gate=" + $(if ($final) { $final.verificationState } else { 'no-host' }))
}
