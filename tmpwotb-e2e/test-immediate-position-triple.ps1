# Synthetic harness for the FRESH45 immediate position-triple helpers in
# scripts/od-048-monitor-correlate-session.ps1. The harness loads only function
# definitions through the PowerShell AST; it never runs the live driver body.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $repo 'scripts\od-048-monitor-correlate-session.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'source parse failed: ' + $errors[0].Message }

foreach ($functionAst in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($functionAst.Extent.Text))
}

# Load only the outer runner's result validator so instrumentation failure
# semantics are exercised without launching the runner body.
$runnerSource = Join-Path $repo 'scripts\invoke-fresh44-crossbattle.ps1'
$runnerTokens = $null
$runnerErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerSource, [ref]$runnerTokens, [ref]$runnerErrors)
if ($runnerErrors.Count -gt 0) { throw 'runner parse failed: ' + $runnerErrors[0].Message }
$validatorAst = $runnerAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ImmediateEvidenceResult' }, $true) | Select-Object -First 1
if ($null -eq $validatorAst) { throw 'runner immediate-result validator not found' }
. ([scriptblock]::Create($validatorAst.Extent.Text))

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw ('FAIL: ' + $Message + ' expected=' + $Expected + ' actual=' + $Actual) }
}

# Case 1: only positive-sign viewpoint-x results are eligible; address math is
# exact and the derived base remains explicitly unproven.
$results = @(
    [pscustomobject]@{ address = '0x101C'; entityId = 7; axis = 'x'; sign = 1; score = 0.95; span = 50.0; shiftSeconds = 0.0; shiftMinSeconds = -1.0; shiftMaxSeconds = 1.0 }
    [pscustomobject]@{ address = '0x201C'; entityId = 7; axis = 'x'; sign = 1; score = 0.99; span = 20.0; shiftSeconds = 0.0; shiftMinSeconds = -1.0; shiftMaxSeconds = 1.0 }
    [pscustomobject]@{ address = '0x301C'; entityId = 8; axis = 'x'; sign = 1; score = 1.00; span = 100.0; shiftSeconds = 0.0; shiftMinSeconds = -1.0; shiftMaxSeconds = 1.0 }
    [pscustomobject]@{ address = '0x401C'; entityId = 7; axis = 'x'; sign = -1; score = 1.00; span = 100.0; shiftSeconds = 0.0; shiftMinSeconds = -1.0; shiftMaxSeconds = 1.0 }
)
$plan = @(New-ImmediatePositionReadPlan -Results $results -ViewpointEntityId 7 -CandidateCap 1 -EdgeThresholdSeconds 8.0)
Assert-Equal 1 $plan.Count 'candidate cap/filter'
Assert-Equal '0x1000' $plan[0].objectBaseHypothesis 'candidate-derived base'
Assert-Equal $false $plan[0].objectBaseProven 'base must remain unproven'
Assert-Equal '0x101C' $plan[0].addresses[0].absoluteAddress 'x address'
Assert-Equal '0x1020' $plan[0].addresses[1].absoluteAddress 'y address'
Assert-Equal '0x1024' $plan[0].addresses[2].absoluteAddress 'z address'
Write-Host 'PASS case1: viewpoint/sign filters + exact 0x1C/0x20/0x24 plan'

# Case 2: a three-float read completed 60ms after correlation is interpolated
# against decoded ground truth and reported only as a hypothesis match.
$battleStart = [datetime]::SpecifyKind([datetime]'2026-08-08T12:00:00', [DateTimeKind]::Utc)
$samples = @(
    [pscustomobject]@{ replayTimeTicks = 0L; x = 0.0; y = 20.0; z = 40.0 }
    [pscustomobject]@{ replayTimeTicks = 10000000L; x = 10.0; y = 30.0; z = 50.0 }
)
$readResponse = [pscustomobject]@{
    readCount = 3
    reads = @(
        [pscustomobject]@{ absoluteAddress = '0x101C'; readOk = $true; valueSummary = '5' }
        [pscustomobject]@{ absoluteAddress = '0x1020'; readOk = $true; valueSummary = '25' }
        [pscustomobject]@{ absoluteAddress = '0x1024'; readOk = $true; valueSummary = '45' }
    )
}
$evidence = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $readResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(440) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(450) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(460) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 50
Assert-Equal 'hypothesis-match-within-gap' $evidence.verdict 'within-gap verdict'
Assert-Equal $true $evidence.withinTargetGap 'within target'
Assert-Equal 1 $evidence.matchingCandidateCount 'matching candidate count'
Assert-Equal $false $evidence.objectBaseProven 'evidence must not upgrade base provenance'
Assert-Equal $false $evidence.atomicReadProven 'batch read must not claim atomicity'
Assert-Equal $false $evidence.sameClockProven 'completion-time alignment is not same-clock proof'
Assert-Equal 5000000L $evidence.candidates[0].targetReplayTick 'target replay tick'
Assert-Equal ([double]-1.0) $evidence.candidates[0].shiftMinSeconds 'shift minimum persisted'
Assert-Equal 1.0 $evidence.candidates[0].shiftMaxSeconds 'shift maximum persisted'
Assert-Equal 50.0 $evidence.candidates[0].span 'span persisted'
Write-Host 'PASS case2: 60ms completion gap + interpolated XYZ hypothesis match'

# Case 3: identical values after the latency target remain a match, but the
# verdict records that timing missed the changed hypothesis.
$late = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $readResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(340) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(350) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(360) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 150
Assert-Equal 'hypothesis-match-gap-exceeded' $late.verdict 'late verdict'
Assert-Equal $false $late.withinTargetGap 'late target flag'
Write-Host 'PASS case3: value match cannot hide a missed timing target'

# Case 4: partial/unreadable triples fail closed and never claim a layout match.
$partialResponse = [pscustomobject]@{
    readCount = 2
    reads = @(
        [pscustomobject]@{ absoluteAddress = '0x101C'; readOk = $true; valueSummary = '5' }
        [pscustomobject]@{ absoluteAddress = '0x1020'; readOk = $false; valueSummary = '' }
        [pscustomobject]@{ absoluteAddress = '0x1024'; readOk = $true; valueSummary = '45' }
    )
}
$partial = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $partialResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(440) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(450) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(460) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 50
Assert-Equal 'no-hypothesis-match' $partial.verdict 'partial read verdict'
Assert-Equal $false $partial.candidates[0].allAxesRead 'partial read gate'
Write-Host 'PASS case4: partial triple fails closed'

# Case 5: an edge-aligned result is excluded before a live read is attempted.
$edge = @([pscustomobject]@{ address = '0x501C'; entityId = 7; axis = 'x'; sign = 1; score = 1.0; span = 50.0; shiftSeconds = 0.0; shiftMinSeconds = -8.0; shiftMaxSeconds = 1.0 })
$edgePlan = @(New-ImmediatePositionReadPlan -Results $edge -ViewpointEntityId 7 -CandidateCap 4 -EdgeThresholdSeconds 8.0)
Assert-Equal 0 $edgePlan.Count 'edge-aligned exclusion'
Write-Host 'PASS case5: edge-aligned result excluded'

# Case 6: positive nonzero shifts move the target replay tick forward using
# the same sign convention as TrajectoryCorrelationScorer.
$shiftedPlan = @($plan)
$shiftedPlan[0].shiftSeconds = 0.25
$shiftedResponse = [pscustomobject]@{
    readCount = 3
    reads = @(
        [pscustomobject]@{ absoluteAddress = '0x101C'; readOk = $true; valueSummary = '7.5' }
        [pscustomobject]@{ absoluteAddress = '0x1020'; readOk = $true; valueSummary = '27.5' }
        [pscustomobject]@{ absoluteAddress = '0x1024'; readOk = $true; valueSummary = '47.5' }
    )
}
$shifted = New-ImmediatePositionReadEvidence -Plan $shiftedPlan -ReadResponse $shiftedResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(440) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(450) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(460) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 50
Assert-Equal 7500000L $shifted.candidates[0].targetReplayTick 'positive shift direction'
Assert-Equal 'hypothesis-match-within-gap' $shifted.verdict 'shifted match verdict'
Write-Host 'PASS case6: nonzero positive shift advances the target tick'

# Restore the base plan for the remaining cases.
$plan[0].shiftSeconds = 0.0

# Case 7: non-finite or fully mismatched values never produce a match.
$nonFiniteResponse = [pscustomobject]@{
    readCount = 3
    reads = @(
        [pscustomobject]@{ absoluteAddress = '0x101C'; readOk = $true; valueSummary = 'NaN' }
        [pscustomobject]@{ absoluteAddress = '0x1020'; readOk = $true; valueSummary = '999' }
        [pscustomobject]@{ absoluteAddress = '0x1024'; readOk = $true; valueSummary = 'Infinity' }
    )
}
$nonFinite = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $nonFiniteResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(440) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(450) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(460) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 50
Assert-Equal 'no-hypothesis-match' $nonFinite.verdict 'non-finite mismatch verdict'
Assert-Equal $false $nonFinite.candidates[0].allAxesRead 'non-finite values fail read evidence'
Write-Host 'PASS case7: NaN/Infinity/full mismatch fail closed'

# Case 8: the exact latency boundary is accepted; any value over it is late.
$boundary = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $readResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(400) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(410) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(420) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 90
Assert-Equal $true $boundary.withinTargetGap 'exact gap boundary'
$overBoundary = New-ImmediatePositionReadEvidence -Plan $plan -ReadResponse $readResponse `
    -CorrelateCompletedUtc $battleStart.AddMilliseconds(399) `
    -CorrelateResponseReceivedUtc $battleStart.AddMilliseconds(409) `
    -ReadRequestStartedUtc $battleStart.AddMilliseconds(419) `
    -ReadCompletedUtc $battleStart.AddMilliseconds(500) `
    -ReadResponseReceivedUtc $battleStart.AddMilliseconds(510) `
    -BattleStartUtc $battleStart -ViewpointSamples $samples -Tolerance 0.001 `
    -MaxGapMilliseconds 100 -RoundTripMilliseconds 91
Assert-Equal $false $overBoundary.withinTargetGap 'over gap boundary'
Write-Host 'PASS case8: completion-gap boundary is enforced'

# Case 9: the outer runner rejects missing/disabled/instrumentation-failure
# evidence but accepts valid negative scientific outcomes.
$missingCheck = Test-ImmediateEvidenceResult -Result ([pscustomobject]@{}) -Required $true
Assert-Equal $false $missingCheck.Ok 'missing immediate field'
$failedCheck = Test-ImmediateEvidenceResult -Result ([pscustomobject]@{ immediatePositionTripleRead = [pscustomobject]@{ enabled = $true; status = 'read-failed' } }) -Required $true
Assert-Equal $false $failedCheck.Ok 'read-failed instrumentation status'
$negativeCheck = Test-ImmediateEvidenceResult -Result ([pscustomobject]@{ immediatePositionTripleRead = [pscustomobject]@{ enabled = $true; status = 'complete'; verdict = 'no-hypothesis-match' } }) -Required $true
Assert-Equal $true $negativeCheck.Ok 'valid negative outcome'
$noCandidateCheck = Test-ImmediateEvidenceResult -Result ([pscustomobject]@{ immediatePositionTripleRead = [pscustomobject]@{ enabled = $true; status = 'no-eligible-viewpoint-x-candidate' } }) -Required $true
Assert-Equal $true $noCandidateCheck.Ok 'valid no-candidate outcome'
Write-Host 'PASS case9: outer runner rejects instrumentation failure, accepts honest negatives'

# Case 10: assert the switch plumbing and stale-result checks exist in all
# three live layers. This guards the source route without launching a game.
$runnerText = Get-Content -LiteralPath $runnerSource -Raw
$autoloopText = Get-Content -LiteralPath (Join-Path $repo 'tmpwotb-e2e\od-049-autoloop.ps1') -Raw
if ($runnerText -notmatch 'driverArgs\.ImmediatePositionTripleRead\s*=\s*\$true') { throw 'FAIL: outer immediate-read switch not plumbed' }
if ($runnerText -notmatch 'driverArgs\.SkipAutoWriteTrace\s*=\s*\$true') { throw 'FAIL: outer trace-skip switch not plumbed' }
if ($autoloopText -notmatch 'm1Args\.ImmediatePositionTripleRead\s*=\s*\$true') { throw 'FAIL: autoloop immediate-read switch not plumbed' }
if ($autoloopText -notmatch '-not \$SkipAutoWriteTrace\) \{ \$m1Args\.AutoWriteTraceOnVerdict') { throw 'FAIL: autoloop trace skip not enforced' }
if ($runnerText -notmatch 'output already exists; choose a fresh name' -or $runnerText -notmatch 'LastWriteTimeUtc -lt \$startedUtc') { throw 'FAIL: stale-result protections missing' }
Write-Host 'PASS case10: switch plumbing + stale-result protections present'

Write-Host 'ALL IMMEDIATE POSITION-TRIPLE HARNESS CHECKS PASSED'
