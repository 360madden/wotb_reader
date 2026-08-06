# Harness: prove the viewpoint-first pivot (-StageViewpointOnly) in
# scripts/od-048-monitor-correlate-session.ps1. Uses the REAL functions
# (Select-ViewpointResults, Test-FamilyAllViewpoint) and the VERBATIM staging
# block extracted from the script via AST, so a future edit to the script
# cannot silently drift the test's copy.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $repo 'scripts\od-048-monitor-correlate-session.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'source parse failed: ' + $errors[0].Message }
$text = Get-Content -LiteralPath $src -Raw

# Define the REAL helper functions from the script (AST-scoped, so only the
# function definitions execute - never the driver body).
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    $sb = [scriptblock]::Create($fn.Extent.Text)
    . $sb   # dot-source: function definitions must persist into this scope
}

# Stub the driver's logging (defined AFTER extraction so it overrides).
function Write-Od048 { param([string]$Message) Write-Host ('od048: ' + $Message) }

# --- Extract the staging-selection block verbatim ---------------------------
# From the pivot comment through the $viewpointEntityId assignment line.
$startMarker = '# Viewpoint-first pivot: -StageViewpointOnly stages ONLY'
$endMarker = '$viewpointEntityId = if ($null -ne $viewpointEntity)'
$startIdx = $text.IndexOf($startMarker)
$endIdx = $text.IndexOf($endMarker)
if ($startIdx -lt 0 -or $endIdx -lt 0) { throw 'staging block markers not found' }
$blockStart = $text.LastIndexOf("`n", $startIdx) + 1
$lineEnd = $text.IndexOf("`n", $endIdx)
if ($lineEnd -lt 0) { $lineEnd = $text.Length }
$stagingBlock = $text.Substring($blockStart, $lineEnd - $blockStart)

function New-ScoredEntity {
    param([string]$EntityId, [string]$TankName, [bool]$IsViewpoint, [double]$Movement)
    [pscustomobject]@{ EntityId = $EntityId; TankName = $TankName; IsViewpoint = $IsViewpoint; Movement = $Movement }
}

# --- Case 1: Select-ViewpointResults keeps the viewpoint entity only. -------
$results = @(
    [pscustomobject]@{ address = '0x1000'; entityId = 7; axis = 'x'; score = 0.95 }
    [pscustomobject]@{ address = '0x2000'; entityId = 3; axis = 'x'; score = 0.99 }  # decoy: higher score, wrong entity
    [pscustomobject]@{ address = '0x3000'; entityId = 7; axis = 'z'; score = 0.88 }
)
$vp = Select-ViewpointResults -Results $results -ViewpointEntityId '7'
if ($vp.Count -ne 2) { throw 'FAIL: expected 2 viewpoint results, got ' + $vp.Count }
foreach ($r in $vp) { if ([string]$r.entityId -ne '7') { throw 'FAIL: non-viewpoint entity leaked: ' + $r.entityId } }
Write-Host 'PASS case1: Select-ViewpointResults keeps entity 7 only (decoy 0x2000 score 0.99 excluded)'

# --- Case 2: Test-FamilyAllViewpoint excludes foreign-address families. -----
$vpAddr = @{ '0x1000' = $true; '0x3000' = $true }
$famGood = [pscustomobject]@{ members = @(
    [pscustomobject]@{ address = '0x1000' }
    [pscustomobject]@{ address = '0x3000' }
) }
$famForeign = [pscustomobject]@{ members = @(
    [pscustomobject]@{ address = '0x1000' }
    [pscustomobject]@{ address = '0x2000' }   # decoy address
) }
$famEmpty = [pscustomobject]@{ members = @() }
if (-not (Test-FamilyAllViewpoint -Family $famGood -ViewpointAddresses $vpAddr)) { throw 'FAIL: all-viewpoint family rejected' }
if (Test-FamilyAllViewpoint -Family $famForeign -ViewpointAddresses $vpAddr) { throw 'FAIL: foreign-address family accepted' }
if (Test-FamilyAllViewpoint -Family $famEmpty -ViewpointAddresses $vpAddr) { throw 'FAIL: empty family accepted' }
Write-Host 'PASS case2: family filter keeps all-viewpoint, rejects foreign + empty'

# --- Case 3: empty results -> empty filter output. ---------------------------
$vpEmpty = Select-ViewpointResults -Results @() -ViewpointEntityId '7'
if ($vpEmpty.Count -ne 0) { throw 'FAIL: empty results produced non-empty filter' }
Write-Host 'PASS case3: empty results -> empty output'

# --- Case 4: staging block with -StageViewpointOnly -> ONLY the viewpoint. ---
$StageViewpointOnly = $true
$StageTopN = 3
$scored = @(
    (New-ScoredEntity '3' 'TeammateA' $false 900.0)
    (New-ScoredEntity '7' 'Self' $true 500.0)
    (New-ScoredEntity '4' 'TeammateB' $false 800.0)
)
. ([scriptblock]::Create($stagingBlock))
if ($stagingEntities.Count -ne 1) { throw 'FAIL: viewpoint-only staged ' + $stagingEntities.Count + ' entities (expected 1)' }
if ([string]$stagingEntities[0].EntityId -ne '7') { throw 'FAIL: staged entity ' + $stagingEntities[0].EntityId }
if ([string]$viewpointEntityId -ne '7') { throw 'FAIL: viewpointEntityId ' + $viewpointEntityId }
Write-Host 'PASS case4: viewpoint-only staging picks entity 7 alone'

# --- Case 5: switch off -> viewpoint + top movers (existing behavior). -------
$StageViewpointOnly = $false
. ([scriptblock]::Create($stagingBlock))
if ($stagingEntities.Count -ne 3) { throw 'FAIL: default staging expected 3 entities, got ' + $stagingEntities.Count }
if ([string]$stagingEntities[0].EntityId -ne '7') { throw 'FAIL: default staging must lead with viewpoint' }
if ([string]$viewpointEntityId -ne '7') { throw 'FAIL: viewpointEntityId ' + $viewpointEntityId }
Write-Host 'PASS case5: default staging keeps viewpoint-first + top movers'

# --- Case 6 (subprocess): no viewpoint + switch on -> exit 2 fail-closed. ---
# `exit 2` inside the block would terminate this harness, so the block text is
# embedded into a SEPARATE pwsh process and its exit code asserted. The block
# is plain statements (comments + assignments + if/else) so it runs inline;
# 'UNREACHABLE' proves the exit really fired.
$subPreamble = @'
$ErrorActionPreference = "Stop"
function Write-Od048 { param([string]$Message) Write-Host ("od048: " + $Message) }
$StageViewpointOnly = $true
$StageTopN = 3
$scored = @(
    [pscustomobject]@{ EntityId = "3"; TankName = "A"; IsViewpoint = $false; Movement = 900.0 }
    [pscustomobject]@{ EntityId = "4"; TankName = "B"; IsViewpoint = $false; Movement = 800.0 }
)
'@
$tmp = Join-Path $env:TEMP ('od048-vp-t6-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$subScript = $subPreamble + "`n" + $stagingBlock + "`nWrite-Host 'UNREACHABLE'`n"
[System.IO.File]::WriteAllText($tmp, $subScript, (New-Object System.Text.UTF8Encoding($false)))
$out = & pwsh -NoProfile -File $tmp 2>&1
$exit = $LASTEXITCODE
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
if ($exit -ne 2) { throw 'FAIL: no-viewpoint exit was ' + $exit + ' (expected 2): ' + ($out -join '; ') }
if (($out -join ' ') -notmatch 'FAILED_no_viewpoint_entity') { throw 'FAIL: expected FAILED_no_viewpoint_entity, got: ' + ($out -join '; ') }
Write-Host 'PASS case6: no-viewpoint + switch on fails closed with exit 2'

Write-Host 'ALL VIEWPOINT-FILTER HARNESS CHECKS PASSED'
