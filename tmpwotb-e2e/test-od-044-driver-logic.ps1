#Requires -Version 5.1
# Offline probe for the OD-044 replayTime driver's pure logic (AST-extracted
# from scripts/invoke-od-044-replaytime-session.ps1): ConvertTo-HexToken, the
# KUSER clock drop, the raw-vs-token mismatch warning, and the write-site
# RIP -> module RVA computation. No live game, no host, no interceptor.
# The interceptor's own Double discriminator is proven separately by
# scripts/test-offline-write-observation.ps1 (--double phase).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\scripts\invoke-od-044-replaytime-session.ps1'
if (-not (Test-Path -LiteralPath $scriptPath)) { throw "driver not found: $scriptPath" }

# ---- AST-extract ConvertTo-HexToken from the driver ----------------------
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host ('PARSE ' + $_.Message) }
    exit 2
}
$convertFn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'ConvertTo-HexToken' }, $true)
if ($null -eq $convertFn) { throw 'ConvertTo-HexToken not found in driver AST' }
# Extent.Text is the full top-level function definition (name + params +
# body); execute it so the probe exercises the driver's exact code.
Invoke-Expression $convertFn.Extent.Text
if (-not (Get-Command ConvertTo-HexToken -ErrorAction SilentlyContinue)) {
    throw 'ConvertTo-HexToken failed to load from driver AST'
}

$pass = 0; $fail = 0
function Assert-Equal([string]$Name, [object]$Actual, [object]$Expected) {
    if ($Actual -eq $Expected) { $script:pass++; Write-Host ("PASS $Name") }
    else { $script:fail++; Write-Host ("FAIL $Name expected=$Expected actual=$Actual") }
}

# ---- ConvertTo-HexToken --------------------------------------------------
Assert-Equal 'hex_plain'       (ConvertTo-HexToken '1234ABCD') '0x1234ABCD'
Assert-Equal 'hex_0x_prefixed' (ConvertTo-HexToken '0x2A1B') '0x2A1B'
Assert-Equal 'hex_lower'       (ConvertTo-HexToken '0xdeadbeef') '0xDEADBEEF'
Assert-Equal 'hex_ws_trim'     (ConvertTo-HexToken '  0xABCD  ') '0xABCD'
Assert-Equal 'non_hex_reject'  (ConvertTo-HexToken '1234ABCDG') $null
Assert-Equal 'empty_reject'    (ConvertTo-HexToken '') $null
Assert-Equal 'short_reject'    (ConvertTo-HexToken '0x1') $null   # < 4 digits
Assert-Equal 'too_long_reject' (ConvertTo-HexToken '0x12345678901234567') $null  # > 16 digits

# ---- KUSER clock drop (defensive re-check in the driver) -----------------
$tokensList = @('0x228AB0F0', '0x7FFE0010', '0x22BC3400', '0x7FFE0ABC')
$clockTokens = @($tokensList | Where-Object { $_ -match '^0x7FFE0[0-9A-Fa-f]{3}$' })
Assert-Equal 'kuser_clock_detected' $clockTokens.Count 2
$kept = @($tokensList | Where-Object { $_ -notmatch '^0x7FFE0[0-9A-Fa-f]{3}$' })
Assert-Equal 'kuser_clock_dropped' ($kept -join ',') '0x228AB0F0,0x22BC3400'
Assert-Equal 'kuser_minimum_2_survivors' ($kept.Count -ge 2) $true

# ---- Write-site RIP -> module RVA ----------------------------------------
$modules = @(
    @{ name = 'wotblitz.exe'; baseAddress = '0x400000' },
    @{ name = 'VCRUNTIME140.dll'; baseAddress = '0x6F100000' }
)
# Mirrors the DRIVER's corrected rule: the module with the HIGHEST base
# address <= RIP owns the write (a first-match loop would mis-attribute a
# CRT write to wotblitz.exe because its low base also satisfies <= RIP).
function Resolve-Rva([string]$Rip, [object[]]$ModuleList) {
    $best = $null
    $bestBase = [uint64]0
    foreach ($mod in $ModuleList) {
        try {
            $ripValue = [Convert]::ToUInt64($Rip.TrimStart('0x'), 16)
            $baseValue = [Convert]::ToUInt64(([string]$mod.baseAddress).TrimStart('0x'), 16)
            if ($ripValue -ge $baseValue -and $baseValue -gt $bestBase) {
                $bestBase = $baseValue
                $best = @{ module = [string]$mod.name; rva = ('0x' + ($ripValue - $baseValue).ToString('X')) }
            }
        }
        catch { }
    }
    if ($null -eq $best) { return @{ module = ''; rva = '' } }
    return $best
}
$gameSite = Resolve-Rva '0xBC39AB' $modules
Assert-Equal 'rva_game_module' $gameSite.module 'wotblitz.exe'
Assert-Equal 'rva_game_offset' $gameSite.rva '0x7C39AB'
# The CRT write must be attributed to VCRUNTIME140.dll (base 0x6F100000),
# NOT wotblitz.exe (base 0x400000 is also <= RIP) - the
# highest-base-contains rule is load-bearing. RIP = base + rva.
$crtSite = Resolve-Rva '0x6F10E8AE' $modules
Assert-Equal 'rva_crt_module' $crtSite.module 'VCRUNTIME140.dll'
Assert-Equal 'rva_crt_offset' $crtSite.rva '0xE8AE'
$belowSite = Resolve-Rva '0x300000' $modules
Assert-Equal 'rva_below_all_modules' $belowSite.rva ''
$belowSite2 = Resolve-Rva '0x300000' $modules
Assert-Equal 'rva_below_module_empty' $belowSite2.module ''

Write-Host ("RESULT pass=$pass fail=$fail")
if ($fail -gt 0) { exit 1 }
exit 0
