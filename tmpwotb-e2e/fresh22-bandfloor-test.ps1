# FRESH22 band-floor/span-floor functional probe.
# Builds the exact family JSON shapes od-048 emits, runs them through
# x64dbg-write-trace.ps1 -FamilyFile -DryRun, and asserts the selection.
# FRESH21 class (must be ADMITTED now): z@0.92, band [-19.5,+12] = 31.5s,
# span 275.15, non-edge -- the real survivor FRESH21 refused at the stale
# 20s floor.
# FRESH10 degenerate (must be REFUSED): y@1.0, band [-10,+30] = 40s, span
# 4.0 -- static value; the widened 60s band floor no longer catches it, the
# span floor must.
$ErrorActionPreference = 'Stop'
$wt = Join-Path $PSScriptRoot '..' 'scripts' 'x64dbg-write-trace.ps1'
$tmp = Join-Path $env:TEMP 'fresh22-bandfloor'

function Invoke-Wt($label, [hashtable]$familyDoc) {
    $file = Join-Path $tmp ($label + '.json')
    $familyDoc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $file -Encoding ascii
    $out = & $wt -FamilyFile $file -AutoWriteTrace -DryRun -TraceSeconds 20 2>&1
    $code = $LASTEXITCODE
    $select = @($out | Where-Object { $_ -match 'family_selected|FAILED_family_selection|selected=' })
    return [pscustomobject]@{ Label = $label; Exit = $code; Sel = ($select -join ' | ') }
}

New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# FRESH21 class: single-member solo family exactly as od-048 would emit
# (span field included, FRESH22).
$fresh21 = @{
    baseAddress = '0x23BD2C50'
    spanBytes   = 0
    axesCovered = @('z')
    complete    = $false
    solo        = $true
    members     = @(@{
        address             = '0x23BD2C50'
        offsetBytes         = 0
        axis                = 'z'
        sign                = 1
        shiftSeconds        = 0
        shiftBandMinSeconds = -19.5
        shiftBandMaxSeconds = 12.0
        score               = 0.92
        edgeAligned         = $false
        span                = 275.15
    })
}

# FRESH10 degenerate: static value, wide band that now fits the 60s floor.
$fresh10 = @{
    baseAddress = '0x1FC57238'
    spanBytes   = 0
    axesCovered = @('y')
    complete    = $false
    solo        = $true
    members     = @(@{
        address             = '0x1FC57238'
        offsetBytes         = 0
        axis                = 'y'
        sign                = -1
        shiftSeconds        = -7.5
        shiftBandMinSeconds = -10.0
        shiftBandMaxSeconds = 30.0
        score               = 1.0
        edgeAligned         = $false
        span                = 4.0
    })
}

$r1 = Invoke-Wt 'fresh21-class' $fresh21
$r2 = Invoke-Wt 'fresh10-degenerate' $fresh10

Write-Host ('RESULT fresh21_class exit=' + $r1.Exit + ' :: ' + $r1.Sel)
Write-Host ('RESULT fresh10_degenerate exit=' + $r2.Exit + ' :: ' + $r2.Sel)

$pass = $true
# FRESH21 class: admitted and ARMED (the DRYRUN line family_members_armed=1).
if ($r1.Exit -ne 0) { $pass = $false; Write-Host 'FAIL: FRESH21 class was NOT admitted (regression)' }
# FRESH10 degenerate: static value must be refused by the span floor.
if ($r2.Exit -eq 0) { $pass = $false; Write-Host 'FAIL: FRESH10 degenerate WAS admitted (span floor missed)' }
Write-Host ('PROBE=' + $(if ($pass) { 'PASS' } else { 'FAIL' }))
exit $(if ($pass) { 0 } else { 1 })
