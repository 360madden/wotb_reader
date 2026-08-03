#Requires -Version 5.1
<#
.SYNOPSIS
  Rolling replayTime Double "increased" campaign against the managed gate.

.DESCRIPTION
  Implements the canonical OD rolling recipe (OD-013/015/016/017): create a
  Double snapshot (8-byte aligned, private/mapped only), then per round wait
  for the operator to advance the replay (Space pause/resume pulse) and
  compare with CompareMode=increased and RollingBaseline=true. Prints
  aggregate counts only - never candidate addresses or values (privacy rule).
  Stops at the target survivor count (default 10) or gate loss, then discards
  the retained scanner session.

  The operator owns the replay transition (workflow rule: the guarded input
  adapter is unavailable). -AutoSpace is an explicit opt-in pulse loop for
  unattended rounds; only use it when the game window is foreground.

.EXITCODES
  0  Rolling completed (target reached or rounds exhausted)
  2  Rendezvous / host missing
  3  Gate not OfflineReplayVerified
  4  Snapshot / compare HTTP failure
  5  Unexpected error
#>
[CmdletBinding()]
param(
    # Target survivor set. -TargetRetained is a deprecated alias (pre-OD-018
    # name; survivors are increasedCount, never retainedCount).
    [Alias('TargetRetained')]
    [int]$TargetSurvivors = 10,
    [int]$MaxRounds = 15,
    [int]$TransitionSeconds = 4,
    # OD-034 two-phase transition: once the survivor set drops to
    # TailThreshold, use the shorter TailTransitionSeconds pulse so more tail
    # rounds fit the fixed 120s lease. The target field (replayTime Double)
    # advances every frame, so a 1s pulse still registers an increase; the
    # occasionally-ticking stragglers that only changed on long pulses get
    # shed, which is the desired tail convergence. Round 1 always uses the
    # full TransitionSeconds (survivors is -1 before the first compare).
    [int]$TailThreshold = 200,
    [int]$TailTransitionSeconds = 1,
    [switch]$AutoSpace,
    [int]$MaxCandidates = 1,
    [int]$Alignment = 8,
    # OD-035 snapshot retained-byte budget passthrough (OffsetSnapshotRequest
    # MaxBytes). Zero = engine ceiling (512 MiB), unchanged behavior. A
    # positive budget soft-caps the RETAINED regions (enumerated low->high,
    # stopping when the next chunk would exceed) and shrinks the round-1 66M
    # walk that dominates the 120s lease. Staged survivor addresses across
    # sessions all live below ~1 GB, so trimming the high tail is safe for the
    # target; the round-1 previousCount is the evidence check that the budget
    # bound (and did not exclude the target region).
    [long]$SnapshotMaxBytes = 0,
    [string]$ResultPath = '',
    # Local (untracked) file to receive survivor candidate absolute addresses
    # from the final compare, for interactive Find-what-writes. Aggregate counts
    # remain the only output on stdout; addresses never enter the repo.
    [string]$AddressFile = '',
    # OD-026 steady-state gate. OD-026 probing showed the 66M+ snapshot state
    # is STABLE for this game session (three snapshots within 0.05%, ~535MB,
    # ~1880 regions) - not a transient load spike - and rolling converges from
    # it (OD-025 attempt 1: 66M->679 in 6 rounds; it failed on lease, not
    # baseline size). So the threshold only rejects genuinely absurd states
    # (e.g. a growing footprint during an actual transition); a stable large
    # baseline is accepted and rolled with shorter transitions to fit the 120s
    # lease. Initial count is read from round-1's compare previousCount, which
    # equals the snapshot's candidate count (probe folded into round 1,
    # OD-RECOVERY-030).
    [long]$MaxInitialCandidates = 100000000,
    [int]$SnapshotRetrySeconds = 20,
    [int]$MaxSnapshotRetries = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Script-scoped API context so a rendezvous capability rotation (~5 min) can be
# refreshed and retried on 401 mid-rolling. OD-RECOVERY-030 finding: the first
# 66M-candidate sanity probe + round-1 compare outlive the token captured at
# startup, so round 2 returned 401 and aborted the campaign. Refresh + retry
# keeps the roll alive (the scanner session itself remains valid server-side).
$script:Api = $null

function Write-Roll([string]$Message) {
    Write-Host ("roll_rt: " + $Message)
}

function Get-Rendezvous {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem $dir -File -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $file) { return $null }
        return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Get-ApiContext {
    $rv = Get-Rendezvous
    if (-not $rv) { return $null }
    return @{
        Base    = [string]$rv.baseUri
        Headers = @{
            'X-WotBTreader-Capability' = "$($rv.capability)"
            'Content-Type'             = 'application/json'
        }
    }
}

function Get-GateState {
    try {
        return Invoke-OdApi -Path '/api/v1/game/state'
    }
    catch {
        return $null
    }
}

# Invoke an OD API endpoint with rendezvous capability rotation recovery: a 401
# (token rotated ~5 min) refreshes the API context and retries the same request
# with the fresh capability, up to Retries times. Keeps a long rolling campaign
# from dying mid-roll to a rotated token.
function Invoke-OdApi {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Method = 'Get',
        [string]$Body = '',
        [string]$ContentType = 'application/json',
        [int]$Retries = 2
    )
    for ($attempt = 0; ; $attempt++) {
        try {
            $params = @{
                Uri     = "$($script:Api.Base)$Path"
                Method  = $Method
                Headers = $script:Api.Headers
            }
            if ($Body) {
                $params.Body = $Body
                $params.ContentType = $ContentType
            }
            return Invoke-RestMethod @params
        }
        catch {
            if ($_.Exception.Message -match '401' -and $attempt -lt $Retries) {
                Write-Roll ("capability_401_refresh_retry=" + ($attempt + 1))
                $script:Api = Get-ApiContext
                if (-not $script:Api) { throw }
                continue
            }
            throw
        }
    }
}

function Send-SpacePulse {
    try {
        $wshell = New-Object -ComObject WScript.Shell
        $wshell.SendKeys(' ')
    }
    catch {
        Write-Roll 'space_pulse_error'
    }
}

try {
    $script:Api = Get-ApiContext
    if (-not $script:Api) {
        Write-Roll 'FAILED_rendezvous_missing'
        exit 2
    }
    $state = Get-GateState
    if (-not $state) {
        Write-Roll 'FAILED_host_unreachable'
        exit 2
    }
    if ($state.verificationState -ne 'OfflineReplayVerified') {
        Write-Roll ("FAILED_gate=" + $state.verificationState + " reason=" + $state.reasonCode)
        exit 3
    }
    Write-Roll 'gate=OfflineReplayVerified'

    $snapBody = @{ valueKind = 'Double'; valueSize = 8; alignment = $Alignment; maxBytes = $SnapshotMaxBytes } | ConvertTo-Json

    # OD-026/030 steady-state gate: the snapshot response carries only the
    # session id, so the snapshot's initial candidate count is learned from the
    # FIRST round's compare previousCount (with rollingBaseline=true, round 1
    # compares against the original snapshot, so previousCount == snapshot
    # candidate count). A separate non-advancing sanity probe is redundant -
    # OD-RECOVERY-030 showed round-1 previous always equals the probe's
    # initial, and the probe's full 66M-candidate walk was consuming the 120s
    # lease before convergence. Trade-off (documented, not a bug): the fold
    # saves lease on the SANE path only - an insane snapshot now pays for a
    # full round-1 walk before being discarded, where the old probe rejected
    # it pre-round. Insane snapshots are rare (absurd counts only), so the
    # sane-path savings win. An absurd round-1 count means the game is still
    # loading: discard and re-snapshot after a gate-aware wait.
    $seq = @()
    $survivors = -1
    # Larger stable baselines (OD-026: ~66M) need more rounds than the
    # OD-018..024 dialog-state baselines (0.7-3M). 15 is the proven floor for
    # small baselines; bump to 22 so a large baseline can reach <=10 inside the
    # lease at short transitions without silently maxing out.
    $roundLimit = [Math]::Max($MaxRounds, 22)
    $lastCmp = $null
    $sessionId = ''
    $snapshotAttempts = 0
    while ($true) {
        $snapshotAttempts++
        $snap = Invoke-OdApi -Path '/api/v1/game/discover/snapshot' -Method Post -Body $snapBody
        if ($snap.PSObject.Properties['error']) {
            Write-Roll ("FAILED_snapshot=" + $snap.error)
            exit 4
        }
        $sessionId = [string]$snap.sessionId
        if ([string]::IsNullOrWhiteSpace($sessionId)) {
            Write-Roll 'FAILED_snapshot_no_session'
            exit 4
        }
        $short = if ($sessionId.Length -gt 8) { $sessionId.Substring(0, 8) } else { $sessionId }
        Write-Roll ("snapshot session=" + $short)

        $seq = @()
        $lastCmp = $null
        $survivors = -1
        $insaneSnapshot = $false
        for ($round = 1; $round -le $roundLimit; $round++) {
            if ($AutoSpace) { Send-SpacePulse }
            # Two-phase pulse (OD-034): full TransitionSeconds for the
            # expensive early rounds, TailTransitionSeconds once the survivor
            # set is small (survivors holds the previous round's count; -1 on
            # round 1 means the full pulse is always used there).
            $pulseSeconds = $TransitionSeconds
            if ($survivors -ge 0 -and $survivors -le $TailThreshold) {
                $pulseSeconds = $TailTransitionSeconds
            }
            Write-Roll ("round={0} pulse_window={1}s" -f $round, $pulseSeconds)
            Start-Sleep -Seconds $pulseSeconds

            # Rolling rounds request a single candidate: only the FINAL round's
            # candidates are ever written to -AddressFile, so requesting 500
            # every round (esp. the expensive 66M-baseline round 1) adds
            # candidate serialization for nothing (OD-RECOVERY-031 lease wall).
            # The address list is harvested separately on the target round.
            $cmpBody = @{
                compareMode     = 'increased'
                maxCandidates   = $MaxCandidates
                rollingBaseline = $true
            } | ConvertTo-Json
            $cmp = Invoke-OdApi -Path "/api/v1/game/discover/compare/$sessionId" -Method Post -Body $cmpBody
            if ($cmp.PSObject.Properties['error']) {
                Write-Roll ("FAILED_compare=" + $cmp.error)
                exit 4
            }
            $lastCmp = $cmp
            # Survivor set = IncreasedCount for the round. RetainedCount only
            # reports unreadable chunks carried forward, not survivors.
            $survivors = [int]$cmp.increasedCount
            $seq += $survivors
            Write-Roll ("round={0} previous={1} increased={2} retained={3} truncated={4} rolling={5}" -f `
                $round, $cmp.previousCount, $survivors, $cmp.retainedCount, $cmp.truncated, $cmp.comparedAgainstRollingBaseline)

            # Steady-state gate on round 1 (previousCount == snapshot count).
            if ($round -eq 1 -and [long]$cmp.previousCount -gt $MaxInitialCandidates) {
                if ($snapshotAttempts -gt $MaxSnapshotRetries) {
                    Write-Roll 'FAILED_snapshot_not_sane'
                    exit 4
                }
                Write-Roll ("snapshot_insane initial=" + $cmp.previousCount + " gt " + $MaxInitialCandidates + " attempt=" + $snapshotAttempts)
                try {
                    $null = Invoke-OdApi -Path "/api/v1/game/discover/session/$sessionId" -Method Delete
                    Write-Roll 'snapshot_insane_discarded'
                }
                catch {
                    Write-Roll 'snapshot_insane_discard_failed'
                }
                # Gate-aware retry wait: abort fast if the lease or monitor
                # dies while we wait for steady state (OD-025 attempt 2).
                $waitDeadline = (Get-Date).AddSeconds($SnapshotRetrySeconds)
                while ((Get-Date) -lt $waitDeadline) {
                    $g = Get-GateState
                    if (-not $g -or $g.verificationState -ne 'OfflineReplayVerified') {
                        $vs = if ($g) { $g.verificationState } else { 'unreachable' }
                        Write-Roll ("STOP_snapshot_retry gate=" + $vs)
                        exit 3
                    }
                    Start-Sleep -Seconds 2
                }
                $insaneSnapshot = $true
                break
            }

            if ($survivors -le $TargetSurvivors) {
                Write-Roll ("TARGET survivors=" + $survivors + " le " + $TargetSurvivors)
                # Harvest the survivor addresses for -AddressFile: one more
                # increased compare requesting up to 500 candidates. Cheap here
                # (the survivor set is tiny), and keeps the big early rounds
                # free of candidate serialization (OD-RECOVERY-031).
                if ($AddressFile) {
                    $harvestBody = @{
                        compareMode     = 'increased'
                        maxCandidates   = 500
                        rollingBaseline = $true
                    } | ConvertTo-Json
                    $harvest = Invoke-OdApi -Path "/api/v1/game/discover/compare/$sessionId" -Method Post -Body $harvestBody
                    if (-not $harvest.PSObject.Properties['error']) {
                        $lastCmp = $harvest
                        $survivors = [int]$harvest.increasedCount
                        Write-Roll ("harvest increased=" + $survivors + " candidates=" + @($harvest.candidates).Count)
                    }
                    else {
                        Write-Roll ("harvest_failed=" + $harvest.error)
                    }
                }
                break
            }

            $g = Get-GateState
            if (-not $g -or $g.verificationState -ne 'OfflineReplayVerified') {
                $vs = if ($g) { $g.verificationState } else { 'unreachable' }
                Write-Roll ("STOP_gate=" + $vs)
                break
            }
        }
        if (-not $insaneSnapshot) { break }
    }

    try {
        $null = Invoke-OdApi -Path "/api/v1/game/discover/session/$sessionId" -Method Delete
        Write-Roll 'discarded'
    }
    catch {
        Write-Roll 'discard_failed'
    }

    Write-Roll ("sequence=" + ($seq -join '->'))
    if ($ResultPath) {
        Set-Content -LiteralPath $ResultPath -Value "$survivors" -Encoding ascii -NoNewline
    }
    if ($AddressFile -and $lastCmp -and $lastCmp.PSObject.Properties['candidates']) {
        $lines = @($lastCmp.candidates | ForEach-Object { [string]$_.absoluteAddress })
        Set-Content -LiteralPath $AddressFile -Value $lines -Encoding ascii
        # The compare candidate list is not contractually guaranteed to equal
        # the survivor set; flag a mismatch so the operator does not trust
        # non-survivor addresses in the debugger.
        if ($survivors -ge 0 -and $lines.Count -ne $survivors) {
            Write-Roll ("WARN_address_count_mismatch candidates=" + $lines.Count + " survivors=" + $survivors)
        }
        Write-Roll ("addresses_written=" + $AddressFile + " count=" + $lines.Count + " survivors=" + $survivors)
    }
    exit 0
}
catch {
    Write-Roll ("FAILED_unexpected=" + $_.Exception.Message)
    exit 5
}
