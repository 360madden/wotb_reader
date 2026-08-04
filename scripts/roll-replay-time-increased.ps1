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

  -CompareMode 'exact' is the v3 exact-value pause scan: the operator pauses
  the replay at a known decoded value (e.g. replayTime = 60.0s), and the
  driver keeps addresses whose frozen value equals -ExactTarget within
  -ExactTolerance. No Space pulses are sent; each round is a stability
  re-read of the paused frame.

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
    # OD-039 replay-marker compare mode. Default 'increased' preserves the
    # classic replayTime campaign. Set -CompareMode 'delta' to use the engine's
    # delta-compare primitive with a replay-derived marker: -DeltaTarget is the
    # expected value change between baseline and live snapshots (e.g. a known
    # position delta from the decoded replay), -DeltaTolerance the absolute
    # tolerance. Both are required in delta mode; they are rejected if passed
    # with a non-delta mode. Rolling baseline advances each round so the delta
    # is measured against the PREVIOUS round, not the original snapshot.
    #
    # -CompareMode 'exact' (v3) is the exact-value pause scan: the operator
    # pauses the replay at a known decoded value (replayTime at a given frame)
    # and the driver keeps addresses whose CURRENT value equals -ExactTarget
    # within -ExactTolerance (wire fields deltaTarget/deltaTolerance are
    # reused). No Space pulses fire in exact mode - the replay must stay
    # paused so the value freezes at the target.
    [ValidateSet('increased', 'delta', 'exact')]
    [string]$CompareMode = 'increased',
    [double]$DeltaTarget = 0,
    [double]$DeltaTolerance = -1,
    [double]$ExactTarget = 0,
    [double]$ExactTolerance = -1,
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
    # OD-044 delta-compare pilot: scan a Float (position X/Z) or Double
    # (replayTime) snapshot. Default 'Double' preserves the proven campaign;
    # 'Float' pairs with -CompareMode delta and a replay-derived position
    # target from scripts/python/replay-delta-extractor.py. valueSize and the
    # default alignment follow the kind (Float 4/4, Double 8/8); an explicit
    # -Alignment still overrides.
    [ValidateSet('Double', 'Float')]
    [string]$ValueKind = 'Double',
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
# from dying mid-roll to a rotated token. OD-RECOVERY-038 finding: the first
# 401-refresh failure in 13 live validations happened at round 9 (roll 39.2M->65
# in 9 rounds) - the refreshed context re-read the rendezvous file, but the
# retry fired immediately, so a mid-rotation file (old token still present or
# host re-rotating) 401'd again and the 2-retry budget was exhausted. A short
# settle after refresh lets the rotation settle, and 4 retries absorb a
# second-generation stale read.
function Invoke-OdApi {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Method = 'Get',
        [string]$Body = '',
        [string]$ContentType = 'application/json',
        [int]$Retries = 4
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
                Start-Sleep -Milliseconds 750
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

    if ($CompareMode -eq 'exact') {
        # Portable finite check: [double]::IsFinite does not exist on Windows
        # PowerShell 5.1 (.NET Framework), which is the OD workflow host.
        $targetFinite = -not [double]::IsNaN($ExactTarget) -and `
            $ExactTarget -ne [double]::PositiveInfinity -and $ExactTarget -ne [double]::NegativeInfinity
        $tolFinite = -not [double]::IsNaN($ExactTolerance) -and `
            $ExactTolerance -ne [double]::PositiveInfinity -and $ExactTolerance -ne [double]::NegativeInfinity
        if (-not $targetFinite -or -not $tolFinite -or $ExactTolerance -lt 0) {
            Write-Roll 'FAILED_exact_requires_finite_target_and_nonnegative_tolerance'
            exit 5
        }
        Write-Roll ("exact_mode target=" + $ExactTarget + " tolerance=" + $ExactTolerance + " (replay must stay PAUSED; no Space pulses)")
    }

    $valueSize = if ($ValueKind -eq 'Double') { 8 } else { 4 }
    $kindAlignment = if ($ValueKind -eq 'Double') { 8 } else { 4 }
    $snapAlignment = if ($Alignment -eq 8 -and $ValueKind -eq 'Float') { $kindAlignment } else { $Alignment }
    $snapBody = @{ valueKind = $ValueKind; valueSize = $valueSize; alignment = $snapAlignment; maxBytes = $SnapshotMaxBytes } | ConvertTo-Json
    Write-Roll ("snapshot valueKind=$ValueKind valueSize=$valueSize alignment=$snapAlignment")

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
            if ($AutoSpace -and $CompareMode -ne 'exact') { Send-SpacePulse }
            # Two-phase pulse (OD-034): full TransitionSeconds for the
            # expensive early rounds, TailTransitionSeconds once the survivor
            # set is small (survivors holds the previous round's count; -1 on
            # round 1 means the full pulse is always used there).
            #
            # Exact mode keeps the replay PAUSED: the target is the frozen
            # decoded value, so rounds are a 1s stability re-read (a resume
            # pulse would change the field away from the target).
            $pulseSeconds = if ($CompareMode -eq 'exact') { 1 } else { $TransitionSeconds }
            if ($CompareMode -ne 'exact' -and $survivors -ge 0 -and $survivors -le $TailThreshold) {
                $pulseSeconds = $TailTransitionSeconds
            }
            Write-Roll ("round={0} pulse_window={1}s" -f $round, $pulseSeconds)
            Start-Sleep -Seconds $pulseSeconds

            # Rolling rounds request a single candidate: only the FINAL round's
            # candidates are ever written to -AddressFile, so requesting 500
            # every round (esp. the expensive 66M-baseline round 1) adds
            # candidate serialization for nothing (OD-RECOVERY-031 lease wall).
            # OD-044 hardening: once the survivor set is small (<= TailThreshold),
            # request 500 candidates each round so the LAST non-zero round's
            # compare carries the actual survivor addresses -- a later
            # increased=0 plateau round must not lose them.
            $roundMaxCandidates = $MaxCandidates
            if ($survivors -ge 0 -and $survivors -le $TailThreshold) {
                $roundMaxCandidates = 500
            }
            $cmpBody = @{
                compareMode     = $CompareMode
                maxCandidates   = $roundMaxCandidates
                rollingBaseline = $true
            }
            if ($CompareMode -eq 'delta') {
                $cmpBody.deltaTarget = $DeltaTarget
                $cmpBody.deltaTolerance = $DeltaTolerance
            }
            elseif ($CompareMode -eq 'exact') {
                # Exact mode reuses the delta wire fields: target = the frozen
                # absolute value the paused replay clock must match, tolerance
                # = the absolute epsilon.
                $cmpBody.deltaTarget = $ExactTarget
                $cmpBody.deltaTolerance = $ExactTolerance
            }
            $cmpBody = $cmpBody | ConvertTo-Json
            $cmp = Invoke-OdApi -Path "/api/v1/game/discover/compare/$sessionId" -Method Post -Body $cmpBody
            if ($cmp.PSObject.Properties['error']) {
                Write-Roll ("FAILED_compare=" + $cmp.error)
                exit 4
            }
            # Keep the previous round's compare: an increased=0 plateau round
            # returns no candidates, so the last NON-zero round's compare is
            # the one that carries the serialized survivor addresses (small-set
            # bump).
            $prevCmp = $lastCmp
            $lastCmp = $cmp
            # Survivor set = the filter-passing count: IncreasedCount for
            # transition modes, CurrentCount for exact (the previous snapshot
            # is irrelevant to an absolute match). RetainedCount only reports
            # unreadable chunks carried forward, not survivors.
            $survivors = if ($CompareMode -eq 'exact') { [int]$cmp.currentCount } else { [int]$cmp.increasedCount }
            $seq += $survivors
            $survivorLabel = if ($CompareMode -eq 'exact') { 'matched' } else { 'increased' }
            Write-Roll ("round={0} previous={1} {2}={3} retained={4} truncated={5} rolling={6}" -f `
                $round, $cmp.previousCount, $survivorLabel, $survivors, $cmp.retainedCount, $cmp.truncated, $cmp.comparedAgainstRollingBaseline)

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

            if ($survivors -eq 0) {
                # OD-044 live finding: survivors=0 is a value-bound plateau
                # (the tail stopped matching), NOT a 0-survivor target. Stop
                # rolling and restore the last NON-zero round's compare for
                # -AddressFile (its candidates were serialized by the small-set
                # bump; the plateau round's compare has none). previousCount of
                # the plateau round equals the last non-zero survivor count.
                if ($null -eq $prevCmp) {
                    # Degenerate: survivors=0 on round 1 (no previous round).
                    # The gate is almost certainly flipping; stop honestly.
                    Write-Roll ('PLATEAU survivors=0 at round=' + $round + ' - no previous round to restore; stop')
                    break
                }
                $lastNonZero = [long]$cmp.previousCount
                $lastCmp = $prevCmp
                $survivors = $lastNonZero
                Write-Roll ('PLATEAU survivors=0 at round=' + $round + ' - restored last non-zero round previous=' + $lastNonZero)
                break
            }

            if ($survivors -le $TargetSurvivors) {
                Write-Roll ("TARGET survivors=" + $survivors + " le " + $TargetSurvivors)
                # Harvest the survivor addresses for -AddressFile: one more
                # increased compare requesting up to 500 candidates. Cheap here
                # (the survivor set is tiny), and keeps the big early rounds
                # free of candidate serialization (OD-RECOVERY-031).
                if ($AddressFile) {
                    # OD-044 live finding: the first fresh increased-compare
                    # can return 0 candidates even when the target round found
                    # survivors -- the rolling baseline advanced to the target
                    # round's snapshot and the values may not have ticked again
                    # in the harvest window (the tail is value-bound). The
                    # survivors tick every frame during active playback, so
                    # retry a few times with short waits before giving up.
                    # Preserve the target round's count: a 0-candidate harvest
                    # must not clobber it for the final record (OD-044 review).
                    $targetSurvivors = $survivors
                    $harvest = $null
                    for ($h = 1; $h -le 5; $h++) {
                        $harvestBody = @{
                            compareMode     = $CompareMode
                            maxCandidates   = 500
                            rollingBaseline = $true
                        }
                        if ($CompareMode -eq 'delta') {
                            $harvestBody.deltaTarget = $DeltaTarget
                            $harvestBody.deltaTolerance = $DeltaTolerance
                        }
                        elseif ($CompareMode -eq 'exact') {
                            $harvestBody.deltaTarget = $ExactTarget
                            $harvestBody.deltaTolerance = $ExactTolerance
                        }
                        $harvestBody = $harvestBody | ConvertTo-Json
                        $harvest = Invoke-OdApi -Path "/api/v1/game/discover/compare/$sessionId" -Method Post -Body $harvestBody
                        if ($harvest.PSObject.Properties['error']) {
                            Write-Roll ("harvest_failed=" + $harvest.error)
                            $harvest = $null
                            break
                        }
                        $survivors = if ($CompareMode -eq 'exact') { [int]$harvest.currentCount } else { [int]$harvest.increasedCount }
                        $survivorLabel = if ($CompareMode -eq 'exact') { 'matched' } else { 'increased' }
                        Write-Roll ("harvest attempt=" + $h + " " + $survivorLabel + "=" + $survivors + " candidates=" + @($harvest.candidates).Count)
                        if ($survivors -gt 0) {
                            $lastCmp = $harvest
                            break
                        }
                        if ($h -lt 5) {
                            Start-Sleep -Seconds 2
                        }
                    }
                    # Only replace $lastCmp when the harvest actually returned
                    # candidates; a 0-candidate harvest must not discard the
                    # target round's serialized survivors -- and when it stayed
                    # at 0, restore the target round's count so the final
                    # record does not report survivors=0 with valid addresses.
                    if ($harvest -and -not $harvest.PSObject.Properties['error'] -and @($harvest.candidates).Count -gt 0) {
                        $lastCmp = $harvest
                    }
                    else {
                        $survivors = $targetSurvivors
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
        # OD-045 live finding: the rolling scan can converge to the Windows
        # shared kernel clock page KUSER_SHARED_DATA (0x7FFE0000-0x7FFE0FFF).
        # Its SystemTime field (+0x10) is a FILETIME-style 8-byte value that
        # increases every 100ns, so it survives every 'increased' compare even
        # after the game's replayTime stops ticking (replay tail / dying game).
        # Kernel writes to that page never fire user-mode hardware
        # breakpoints, so arming a write-BP there yields 0 hits by
        # construction. Drop the whole page from the address file and WARN so
        # the operator knows the game field had stopped ticking.
        $clockLines = @($lines | Where-Object { $_ -match '^0x7FFE0[0-9A-Fa-f]{3}$' })
        if ($clockLines.Count -gt 0) {
            $lines = @($lines | Where-Object { $_ -notmatch '^0x7FFE0[0-9A-Fa-f]{3}$' })
            Write-Roll ("WARN_kuser_clock_dropped count=" + $clockLines.Count + " addresses=" + ($clockLines -join ','))
        }
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
