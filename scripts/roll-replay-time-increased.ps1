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
  Stops at TargetRetained (default 10) or gate loss, then discards the
  retained scanner session.

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
    [int]$TargetRetained = 10,
    [int]$MaxRounds = 15,
    [int]$TransitionSeconds = 4,
    [switch]$AutoSpace,
    [int]$MaxCandidates = 1,
    [int]$Alignment = 8,
    [string]$ResultPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Get-GateState($api) {
    try {
        return Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/state" -Headers $api.Headers
    }
    catch {
        return $null
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
    $api = Get-ApiContext
    if (-not $api) {
        Write-Roll 'FAILED_rendezvous_missing'
        exit 2
    }
    $state = Get-GateState $api
    if (-not $state) {
        Write-Roll 'FAILED_host_unreachable'
        exit 2
    }
    if ($state.verificationState -ne 'OfflineReplayVerified') {
        Write-Roll ("FAILED_gate=" + $state.verificationState + " reason=" + $state.reasonCode)
        exit 3
    }
    Write-Roll 'gate=OfflineReplayVerified'

    $snapBody = @{ valueKind = 'Double'; valueSize = 8; alignment = $Alignment } | ConvertTo-Json
    $snap = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/discover/snapshot" -Method Post `
        -Headers $api.Headers -ContentType 'application/json' -Body $snapBody
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
    $retained = -1
    for ($round = 1; $round -le $MaxRounds; $round++) {
        if ($AutoSpace) { Send-SpacePulse }
        Write-Roll ("round={0} pulse_window={1}s" -f $round, $TransitionSeconds)
        Start-Sleep -Seconds $TransitionSeconds

        $cmpBody = @{
            compareMode     = 'increased'
            maxCandidates   = $MaxCandidates
            rollingBaseline = $true
        } | ConvertTo-Json
        $cmp = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/discover/compare/$sessionId" `
            -Method Post -Headers $api.Headers -ContentType 'application/json' -Body $cmpBody
        if ($cmp.PSObject.Properties['error']) {
            Write-Roll ("FAILED_compare=" + $cmp.error)
            exit 4
        }
        $retained = [int]$cmp.retainedCount
        $increased = [int]$cmp.increasedCount
        $seq += $retained
        Write-Roll ("round={0} previous={1} increased={2} retained={3} truncated={4} rolling={5}" -f `
            $round, $cmp.previousCount, $increased, $retained, $cmp.truncated, $cmp.comparedAgainstRollingBaseline)

        if ($retained -le $TargetRetained) {
            Write-Roll ("TARGET retained=" + $retained + " le " + $TargetRetained)
            break
        }

        $g = Get-GateState $api
        if (-not $g -or $g.verificationState -ne 'OfflineReplayVerified') {
            $vs = if ($g) { $g.verificationState } else { 'unreachable' }
            Write-Roll ("STOP_gate=" + $vs)
            break
        }
    }

    try {
        $null = Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/discover/session/$sessionId" `
            -Method Delete -Headers $api.Headers
        Write-Roll 'discarded'
    }
    catch {
        Write-Roll 'discard_failed'
    }

    Write-Roll ("sequence=" + ($seq -join '->'))
    if ($ResultPath) {
        Set-Content -LiteralPath $ResultPath -Value "$retained" -Encoding ascii -NoNewline
    }
    exit 0
}
catch {
    Write-Roll ("FAILED_unexpected=" + $_.Exception.Message)
    exit 5
}
