# Offline end-to-end validation of the C# write-trace wiring: builds a
# synthetic od-048 M1 family report whose member is the counter's live
# static float, runs scripts/invoke-csharp-write-trace.ps1 -TargetPid
# against the counter, and asserts the odwt-* evidence shapes:
#   - exit 0 (clean window)
#   - <ResultPath>.family.json present with verdict family-hit
#   - member entries carry hit counts and the rip list
#   - windowValuesChanged=true (the counter's writes were captured)
#
# Usage: pwsh -NoProfile -File tmpwotb-e2e/test-csharp-write-trace.ps1
# Requires the x86 publish first:
#   dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor
param(
    [string]$Exe = 'C:\work\wotb_reader\.build\publish\write-interceptor\WotBTreader.WriteInterceptor.exe',
    [string]$Wrapper = 'C:\work\wotb_reader\scripts\invoke-csharp-write-trace.ps1'
)
$ErrorActionPreference = 'Stop'

function Cleanup([int]$code) {
    Stop-Process -Id $script:counterId -Force -ErrorAction SilentlyContinue
    exit $code
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host 'MISSING_EXE_build_first: dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor'
    exit 1
}
if (-not (Test-Path -LiteralPath $Wrapper)) {
    Write-Host 'MISSING_WRAPPER:' $Wrapper
    exit 1
}

$work = Join-Path $env:TEMP 'od-wt-cswt-test'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Get-ChildItem -LiteralPath $work -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$addrFile = Join-Path $work 'counter-addr.txt'
$progressFile = Join-Path $work 'counter-progress.txt'
$familyFile = Join-Path $work 'synthetic-family.json'
$resultTxt = Join-Path $work 'hits.txt'
$familyReport = $resultTxt + '.family.json'

# 1. Start the counter and read its published address.
$counter = Start-Process -FilePath $Exe -ArgumentList @('--counter', '-AddrFile', $addrFile, '-ProgressFile', $progressFile) -PassThru -WindowStyle Hidden
$script:counterId = $counter.Id
$addr = $null
for ($i = 0; $i -lt 30 -and -not $addr; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim() }
    if (-not $addr) { Start-Sleep -Milliseconds 300 }
}
if (-not $addr) { Write-Host 'FAIL_no_address'; Cleanup 1 }
Start-Sleep -Milliseconds 800

# 2. Build the synthetic od-048 M1 family report shape: families[] with a
#    single-member family whose address is the counter's live float, score
#    and band inside the floors, not edge-aligned, span above the floor.
$family = [ordered]@{
    baseAddress = ('0x' + $addr)
    spanBytes   = 0
    axesCovered = @('z')
    complete    = $false
    solo        = $true
    members     = @(@{
        address         = ('0x' + $addr)
        offsetBytes     = 0
        axis            = 'z'
        sign            = 1
        shiftSeconds    = 0
        shiftMinSeconds = -2
        shiftMaxSeconds = 2
        score           = 1.0
        edgeAligned     = $false
        span            = 1000.0
    })
}
$familyDoc = [ordered]@{ families = @($family) }
$familyJson = $familyDoc | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($familyFile, $familyJson, (New-Object System.Text.UTF8Encoding($false)))

# 3. Run the write-trace wrapper against the counter. Splat the EXACT
#    wtArgs hashtable od-048 builds for the auto-trace (including
#    AutoWriteTrace=$true) so any param-contract drift is caught offline,
#    not on a live run. TargetPid is the offline-only override.
Write-Host ('running wrapper pid=' + $counter.Id)
$wtArgs = @{
    FamilyFile            = $familyFile
    AutoWriteTrace        = $true
    TraceSeconds          = 4
    ResultPath            = $resultTxt
    MinMemberScore        = 0.9
    MaxMemberBandSeconds  = 60.0
    MinMemberSpan         = 10.0
    TargetPid             = $counter.Id
}
& $Wrapper @wtArgs
$wcode = $LASTEXITCODE
Write-Host ('wrapper_exit=' + $wcode)
if ($wcode -ne 0) { Write-Host 'FAIL_wrapper_exit'; Cleanup 1 }
if (-not (Test-Path -LiteralPath $familyReport)) { Write-Host 'FAIL_missing_family_report'; Cleanup 1 }

$fr = Get-Content -LiteralPath $familyReport -Raw | ConvertFrom-Json
Write-Host ('verdict=' + $fr.verdict + ' hits_total=' + $fr.hitsTotal + ' hit_members=' + $fr.hitMembers + ' armed=' + $fr.armedCount)
if ($fr.verdict -ne 'family-hit') { Write-Host 'FAIL_no_family_hit'; Cleanup 1 }
if ($fr.hitsTotal -lt 1) { Write-Host 'FAIL_no_hits'; Cleanup 1 }
if ($fr.hitMembers -lt 1) { Write-Host 'FAIL_no_hit_members'; Cleanup 1 }
if ($fr.windowValuesChanged -ne 'true') { Write-Host 'FAIL_values_not_changed'; Cleanup 1 }

$member = @($fr.members)[0]
Write-Host ('member_hits=' + $member.hits + ' first_rip=' + (@($member.rips)[0]))
if ($member.hits -lt 1) { Write-Host 'FAIL_member_zero_hits'; Cleanup 1 }
if (-not (@($member.rips)[0] -match '^0x[0-9a-fA-F]{4,16}$')) { Write-Host 'FAIL_bad_rip_format'; Cleanup 1 }

# 4. Hits text file (odwt-* shape) must carry 'addr rip' lines.
if (-not (Test-Path -LiteralPath $resultTxt)) { Write-Host 'FAIL_missing_hits_txt'; Cleanup 1 }
$lines = @(Get-Content -LiteralPath $resultTxt)
if ($lines.Count -lt 1) { Write-Host 'FAIL_empty_hits_txt'; Cleanup 1 }
if ($lines[0] -notmatch '^0x[0-9a-fA-F]+ 0x[0-9a-fA-F]{4,16}$') { Write-Host 'FAIL_hits_txt_shape: ' + $lines[0]; Cleanup 1 }

Write-Host 'PASS_csharp_write_trace'
Cleanup 0
