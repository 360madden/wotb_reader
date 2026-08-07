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

# Stub the driver's logging + params the block touches.
function Write-Od048 { param([string]$Message) Write-Host ('od048: ' + $Message) }
$AutoTraceMinMemberScore = 0.9
$AutoTraceMaxMemberBandSeconds = 20.0

# --- Case 1: FRESH10 report. Expect 0x1FC57238 emitted as solo family. ----
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
# NOT be emitted. (The emitted family's round-trip through the write-trace is
# covered separately by tmpwotb-e2e/fixtures/solo-emitted-shape.json.)
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
if ($soloFamilyEmitted) { throw 'FAIL: degenerate 40s-band y@1.0 was emitted (band floor broken)' }
Write-Host 'PASS case2: degenerate y@1.0 (40s band) NOT emitted'

Write-Host 'ALL SOLO-EMISSION HARNESS CHECKS PASSED'
