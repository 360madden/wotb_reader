# Harness: prove the od-048 FRESH14 solo-family emission block (extracted
# VERBATIM from scripts/od-048-monitor-correlate-session.ps1) turns FRESH10's
# lone tight-band survivor 0x1FC57238 into an armable solo family, while a
# degenerate y@1.0 (40s band) is NOT emitted. Uses the REAL helper functions
# (Get-SurvivorBandWidth etc.) extracted from the script via AST, so a future
# edit to the script cannot silently drift the test's copy.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $repo 'scripts\od-048-monitor-correlate-session.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'source parse failed: ' + $errors[0].Message }

# Extract the emission block verbatim: from the FRESH14 comment marker to the
# line after the final closing brace of the emission if-block.
$text = Get-Content -LiteralPath $src -Raw
$startMarker = '# FRESH14 solo-survivor arming path: the strongest artifact'
$endMarker = '$soloFamilyEmitted = $false'
$startIdx = $text.IndexOf($startMarker)
$endIdx = $text.IndexOf($endMarker)
if ($startIdx -lt 0 -or $endIdx -lt 0) { throw 'emission block markers not found' }
# The block begins at the comment line start and runs through the closing
# brace of the `if ($strongSurvivors.Count -gt 0)` block, which is the `}`
# on the line right after the final Write-Od048 inside it. Find it by
# scanning for the first standalone `}` after $endIdx.
$blockStart = $text.LastIndexOf("`n", $startIdx) + 1
$cursor = $endIdx
$depth = 0
$capture = $null
while ($cursor -lt $text.Length) {
    $nl = $text.IndexOf("`n", $cursor)
    if ($nl -lt 0) { $nl = $text.Length }
    $line = $text.Substring($cursor, $nl - $cursor)
    $depth += ($line.ToCharArray() | Where-Object { $_ -eq '{' }).Count
    $depth -= ($line.ToCharArray() | Where-Object { $_ -eq '}' }).Count
    if ($depth -eq 0 -and $line.Trim() -eq '}') {
        $capture = $text.Substring($blockStart, $nl - $blockStart)
        break
    }
    $cursor = $nl + 1
}
if (-not $capture) { throw 'could not bracket emission block' }

# Define the REAL helper functions from the script (AST-scoped, so only the
# function definitions execute - never the driver body).
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    $sb = [scriptblock]::Create($fn.Extent.Text)
    . $sb   # dot-source: function definitions must persist into this scope
}

# Stub the driver's logging + params the block touches. Each case re-stubs
# the floors it is testing (the FRESH10 fixture's 0x1FC57238 carries span 2.76
# but case 2's degenerate must be refused by the SPAN floor, so the span gate
# cannot be a single global stub).
function Write-Od048 { param([string]$Message) Write-Host ('od048: ' + $Message) }
$AutoTraceMinMemberScore = 0.9
$AutoTraceMaxMemberBandSeconds = 60.0
$AutoTraceMinMemberSpan = 10.0
$AutoTraceMaxSoloMembers = 4
# FRESH42 band-weighted floor: tight-band survivors (band <= 10s) clear at
# 0.85; wide-band survivors need the strict 0.9.
$AutoTraceTightBandMinScore = 0.85
$AutoTraceTightBandMaxSeconds = 10.0

# --- Case 1: FRESH10 report. Expect 0x1FC57238 emitted as solo family. ----
# The fixture artifact carries span 2.76 (below the live 10.0 span floor) but
# the historical emission predates that floor; this case tests the emission
# SHAPE (score/band/selection) with the span gate disabled.
$AutoTraceMinMemberSpan = 0.0
$report = Get-Content (Join-Path $repo '.data\od-049-fresh10-result.json') -Raw | ConvertFrom-Json
$results = @($report.results)
# Replicate the audit block's edge-aligned demotion minimally (the report
# already carries edgeAligned + shiftBandMin/MaxSeconds on results).
$strongSurvivors = @($results | Where-Object { $_.score -ge 0.7 -and -not $_.edgeAligned })
$families = @($report.families)
$correlated = $null   # not used by the emission block itself
$completeFamilies = @($families | Where-Object { $_.complete })
$edgeAlignedSurvivors = @()
$verdict = 'evidence-strong'
. ([scriptblock]::Create($capture))   # dot-source: assignments persist

if (-not $soloFamilyEmitted) { throw 'FAIL: 0x1FC57238 was NOT emitted as a solo family' }
# @() wrap: a single matching family is a scalar PSCustomObject whose .Count
# is $null (the FRESH23 block appends exactly one solo family) - the unwrapped
# form false-negatives. Array-wrap so the count and index reads are reliable.
$emitted = @($families | Where-Object { $_.solo })
if ($emitted.Count -ne 1) { throw 'FAIL: expected exactly 1 solo family, got ' + $emitted.Count }
$m = $emitted[0].members[0]
if ([string]$m.address -ne '0x1FC57238') { throw 'FAIL: solo member address ' + $m.address }
if ([string]$m.axis -ne 'y') { throw 'FAIL: axis ' + $m.axis }
if ([double]$m.score -lt 0.999) { throw 'FAIL: score ' + $m.score }
if ($m.edgeAligned) { throw 'FAIL: solo member must not be edge-aligned' }
Write-Host ("PASS case1: 0x1FC57238 emitted solo axis=" + $m.axis + " score=" + $m.score)

# --- Case 2: re-run the emission with ONLY a degenerate survivor -> must ---
# NOT be emitted. With the live 60s band floor, the static y@1.0 (40s band) is
# refused by the SPAN floor (span 4.0 < 10.0) - the movement-proof gate that
# FRESH22 added for exactly this class. Restore the live span floor here.
$AutoTraceMinMemberSpan = 10.0
$results = @([pscustomobject]@{
    address = '0x22CF8198'; participantId = 'x'; entityId = 1; axis = 'y'; sign = 1
    shiftSeconds = 0.0; shiftBandMinSeconds = -10.0; shiftBandMaxSeconds = 30.0
    edgeAligned = $false; matchCount = 69; totalSamples = 69; span = 4.0; score = 1.0
})
$strongSurvivors = @($results)
$families = @()
$completeFamilies = @()
$edgeAlignedSurvivors = @()
$soloFamilyEmitted = $false
. ([scriptblock]::Create($capture))   # dot-source: assignments persist
if ($soloFamilyEmitted) { throw 'FAIL: degenerate static y@1.0 (span 4.0 < 10.0 floor) was emitted (span floor broken)' }
Write-Host 'PASS case2: degenerate static y@1.0 (span 4.0) NOT emitted'

# --- Case 3 (FRESH42): TIGHT-band x@0.857 (FRESH40 class: 3.0s band, the
# same class as both hits) MUST now be emitted via the band-weighted floor ---
# (score 0.857 >= 0.85 tight floor; band 3.0s <= 10s; span 40 >= 10). This is
# the exact candidate the flat 0.9 floor refused on FRESH40.
$AutoTraceMinMemberSpan = 10.0
$results = @([pscustomobject]@{
    address = '0x23CCC2D0'; participantId = 'x'; entityId = 2549401; axis = 'x'; sign = 1
    shiftSeconds = 68.0; shiftBandMinSeconds = 66.5; shiftBandMaxSeconds = 69.5
    edgeAligned = $false; matchCount = 12; totalSamples = 14; span = 40.0; score = 0.8571428571428571
})
$strongSurvivors = @($results)
$families = @()
$completeFamilies = @()
$edgeAlignedSurvivors = @()
$soloFamilyEmitted = $false
. ([scriptblock]::Create($capture))
if (-not $soloFamilyEmitted) { throw 'FAIL: tight-band x@0.857 (3s band) was NOT emitted (band-weighted floor broken)' }
$emitted3 = @($families | Where-Object { $_.solo })
if ($emitted3.Count -ne 1) { throw 'FAIL: case3 expected exactly 1 solo family, got ' + $emitted3.Count }
if ([string]$emitted3[0].members[0].address -ne '0x23CCC2D0') { throw 'FAIL: case3 member address ' + $emitted3[0].members[0].address }
Write-Host ("PASS case3: tight-band x@0.857 emitted solo axis=" + $emitted3[0].members[0].axis + " score=" + $emitted3[0].members[0].score + " band=" + $emitted3[0].members[0].shiftMaxSeconds + '-' + $emitted3[0].members[0].shiftMinSeconds)

# --- Case 4 (FRESH42): WIDE-band z@0.846 (FRESH41 class: 45.5s band) must ---
# STILL be refused - the band-weighted floor lowers the bar for TIGHT bands
# only; a wide-band survivor (45.5s > 10s tight max, under the 60s band floor)
# still needs the strict 0.9 and 0.846 < 0.9.
$AutoTraceMinMemberSpan = 10.0
$results = @([pscustomobject]@{
    address = '0x23A56AD0'; participantId = 'x'; entityId = 2549401; axis = 'z'; sign = 1
    shiftSeconds = 30.0; shiftBandMinSeconds = 6.0; shiftBandMaxSeconds = 51.5
    edgeAligned = $false; matchCount = 11; totalSamples = 13; span = 55.0; score = 0.8461538461538461
})
$strongSurvivors = @($results)
$families = @()
$completeFamilies = @()
$edgeAlignedSurvivors = @()
$soloFamilyEmitted = $false
. ([scriptblock]::Create($capture))
if ($soloFamilyEmitted) { throw 'FAIL: wide-band z@0.846 (45.5s band) was emitted (band-weighted floor must NOT admit wide bands)' }
Write-Host 'PASS case4: wide-band z@0.846 (45.5s band) NOT emitted'

Write-Host 'ALL SOLO-EMISSION HARNESS CHECKS PASSED'
