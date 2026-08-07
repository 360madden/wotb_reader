# Offline mechanism test for the C# guard-page write interceptor (M2
# successor). Proves the capture loop that x64dbg could never deliver here:
# the x86 helper arms PAGE_GUARD on the counter's shared float, attaches as
# the only debugger, and must capture the per-write RIP + value while the
# counter keeps advancing (no freeze).
#
# Usage: pwsh -NoProfile -File tmpwotb-e2e/test-guard-interceptor.ps1
# The x86 self-contained publish bundles the x86 .NET runtime (the machine
# has only the x64 runtime installed). Build it with:
#   dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor
param(
    [string]$Exe = 'C:\work\wotb_reader\.build\publish\write-interceptor\WotBTreader.WriteInterceptor.exe'
)
$ErrorActionPreference = 'Stop'

function Cleanup([int]$code) {
    Stop-Process -Id $script:counterId -Force -ErrorAction SilentlyContinue
    exit $code
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host 'MISSING_EXE_build_first: dotnet build tools/WriteInterceptor -c Release'
    exit 1
}

$work = Join-Path $env:TEMP 'od-wt-interceptor-test'
New-Item -ItemType Directory -Force -Path $work | Out-Null
Get-ChildItem -LiteralPath $work -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$addrFile = Join-Path $work 'counter-addr.txt'
$progressFile = Join-Path $work 'counter-progress.txt'
$report = Join-Path $work 'capture-report.json'
$negReport = Join-Path $work 'neg-report.json'

# 1. Start the counter target (same exe, --counter mode).
$counter = Start-Process -FilePath $Exe -ArgumentList @('--counter', '-AddrFile', $addrFile, '-ProgressFile', $progressFile) -PassThru -WindowStyle Hidden
$script:counterId = $counter.Id
$addr = $null
for ($i = 0; $i -lt 30 -and -not $addr; $i++) {
    if (Test-Path -LiteralPath $addrFile) { $addr = (Get-Content -LiteralPath $addrFile -Raw).Trim() }
    if (-not $addr) { Start-Sleep -Milliseconds 300 }
}
if (-not $addr) { Write-Host 'FAIL_no_address'; Cleanup 1 }

Start-Sleep -Milliseconds 800
$p0 = [long](Get-Content -LiteralPath $progressFile -Raw).Trim()

# 2. Run the interceptor for 6 seconds against the live counter.
& $Exe '--interceptor' '-Pid' $counter.Id '-Addresses' ('0x' + $addr) '-Seconds' '6' '-Out' $report | Out-Null
$icode = $LASTEXITCODE
$p1 = [long](Get-Content -LiteralPath $progressFile -Raw).Trim()

# 3. Assertions: exit 0, target advanced (no freeze), hits captured.
$json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$hits = @($json.hits)
Write-Host ('interceptor_exit=' + $icode + ' pages_armed=' + $json.pagesArmed)
Write-Host ('progress ' + $p0 + ' -> ' + $p1 + ' advancing=' + ($p1 -gt $p0))
Write-Host ('hits=' + $hits.Count)
if ($icode -ne 0) { Write-Host 'FAIL_interceptor_exit'; Cleanup 1 }
if (-not ($p1 -gt $p0)) { Write-Host 'FAIL_target_frozen_during_attach'; Cleanup 1 }
if ($hits.Count -lt 1) { Write-Host 'FAIL_no_hits'; Cleanup 1 }
$h0 = $hits[0]
if (-not $h0.rip -or $h0.rip -eq '0x00000000') { Write-Host 'FAIL_bad_rip'; Cleanup 1 }
if ($null -eq $h0.value) { Write-Host 'FAIL_missing_value'; Cleanup 1 }
if (-not $h0.PSObject.Properties['rva'] -or -not $h0.rva) { Write-Host 'FAIL_missing_rva_key'; Cleanup 1 }
# Synthetic counter writes from JIT/private code: rva may be "jit"; module list
# must still be non-empty (at least the interceptor/counter module itself).
$mods = @()
if ($json.PSObject.Properties['modules'] -and $null -ne $json.modules) { $mods = @($json.modules) }
Write-Host ('modules=' + $mods.Count)
if ($mods.Count -lt 1) { Write-Host 'FAIL_modules_missing'; Cleanup 1 }
$instrPresent = ($h0.PSObject.Properties['instructionHex'] -and $null -ne $h0.instructionHex -and [string]$h0.instructionHex)
Write-Host ('first_hit rip=' + $h0.rip + ' value=' + $h0.value + ' rva=' + $h0.rva + ' instr=' + $instrPresent + ' regs_present=' + ($null -ne $h0.registers))
if (-not $instrPresent) { Write-Host 'FAIL_missing_instruction_hex'; Cleanup 1 }
Write-Host 'PASS_capture'

# 4. Negative control: bogus pid must fail closed (no pages armed / no attach).
& $Exe '--interceptor' '-Pid' '999999' '-Addresses' ('0x' + $addr) '-Seconds' '2' '-Out' $negReport | Out-Null
$negCode = $LASTEXITCODE
Write-Host ('negative_exit=' + $negCode)
if ($negCode -eq 0) { Write-Host 'FAIL_negative_should_fail_closed'; Cleanup 1 }
Write-Host 'PASS_negative_fail_closed'

Cleanup 0
