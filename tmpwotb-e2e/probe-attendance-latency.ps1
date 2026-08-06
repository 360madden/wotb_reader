# Smoke: prove the FRESH18 attendance-latency tick math. The decoded
# trajectory's tick 0 is MATCH-BEGIN, which lags the Start marker by the
# loading+attendance phase (~50s). The staging scan must target ground-truth
# tick (markerElapsed - attendance) and the correlate must anchor wall->tick
# at (marker + attendance) so the needed shift is ~0, not -50s (unreachable
# by the +/-30s sweep - the FRESH15j edge-aligned-all-results signature).
# Run under BOTH engines:
#   powershell -File tmpwotb-e2e/probe-attendance-latency.ps1
#   pwsh        -File tmpwotb-e2e/probe-attendance-latency.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Scenario: FRESH15j - the staging gate fired at marker-elapsed 56s.
$markerElapsedStaging = 56.0
$attendance = 50.0
$markerUtc = [datetime]::Parse('2026-08-06T13:30:00Z', [Globalization.CultureInfo]::InvariantCulture,
    ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))

# 1. Match-begin instant (what od-048 computes after the FRESH18 change).
$battleStartUtc = $markerUtc.AddSeconds($attendance)
$elapsedSinceBattleStart = ([datetime]::UtcNow - $battleStartUtc).TotalSeconds
# The STAGING tick must be relative to battleStartUtc; simulate a probe time
# 56s after the marker.
$probeWall = $markerUtc.AddSeconds($markerElapsedStaging)
$stagingTickSeconds = [Math]::Max(0.0, ($probeWall - $battleStartUtc).TotalSeconds)
$stageTickEstimate = [long]($stagingTickSeconds * 10000000.0)
Write-Host ("STAGING markerElapsed=56 attendance=50 -> battleTickSeconds=" + [Math]::Round($stagingTickSeconds, 2) + " tick_est=" + $stageTickEstimate)
if ([Math]::Abs($stagingTickSeconds - 6.0) -gt 0.01) { throw 'FAIL: staging tick must be ~6s (56 - 50), not 56s' }

# 2. Correlate anchor: must be battleStartUtc, so baseTicks = wall - matchBegin.
$correlateAnchor = $battleStartUtc.ToString('o')
$parsedAnchor = [datetime]::Parse($correlateAnchor, [Globalization.CultureInfo]::InvariantCulture,
    ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
if ($parsedAnchor -ne $battleStartUtc) { throw 'FAIL: correlate anchor must equal marker + attendance' }
$offsetFromMarker = ($parsedAnchor - $markerUtc).TotalSeconds
Write-Host ("CORRELATE anchor_offset_from_marker_s=" + $offsetFromMarker)
if ([Math]::Abs($offsetFromMarker - $attendance) -gt 0.01) { throw 'FAIL: anchor offset must equal attendance' }

# 3. The needed shift at a sample 100s after the marker: with the corrected
#    anchor, baseTicks = wall - matchBegin = 50s, and the TRUE battle tick at
#    that wall instant is also 50s (battle started at marker+50) -> shift ~0,
#    well inside the +/-30s sweep. The OLD marker anchor would need -50s.
$sampleWall = $markerUtc.AddSeconds(100.0)
$baseTicksSec = ($sampleWall - $parsedAnchor).TotalSeconds
$trueBattleTickSec = ($sampleWall - $battleStartUtc).TotalSeconds
$neededShift = $trueBattleTickSec - $baseTicksSec
Write-Host ("SHIFT sample_at_marker_elapsed_100s -> baseTick=" + [Math]::Round($baseTicksSec, 2) + "s trueTick=" + [Math]::Round($trueBattleTickSec, 2) + "s neededShift=" + [Math]::Round($neededShift, 2) + "s (0 = aligned, within +/-30 sweep)")
if ([Math]::Abs($neededShift) -gt 0.01) { throw 'FAIL: corrected anchor must make the needed shift ~0' }

Write-Host ('PROBE_ATTENDANCE_OK engine=' + $PSVersionTable.PSEdition + ' ps=' + $PSVersionTable.PSVersion.ToString())
