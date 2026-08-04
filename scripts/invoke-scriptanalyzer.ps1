[CmdletBinding()]
param(
    [string] $ReportPath = '',
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$settingsPath = Join-Path $repoRoot 'tools\psscriptanalyzer-settings.psd1'
$customRulesPath = Join-Path $repoRoot 'tools\psscriptanalyzer-custom-rules.psm1'

# Exit codes: 0 clean, 1 gate violations, 2 module not installed, 3 other failure.
function Exit-With {
    param([int] $Code, [string] $Message = '')
    if ($Message) { Write-Host $Message }
    exit $Code
}

# ---- 1. Locate the installed module (highest pinned version wins) ----
$installedRoot = Join-Path $repoRoot 'tools\external\installed\psscriptanalyzer'
$manifests = @(Get-ChildItem -Path $installedRoot -Filter 'PSScriptAnalyzer.psd1' -Recurse -ErrorAction SilentlyContinue)
if ($manifests.Count -eq 0) {
    Exit-With 2 @"

PSScriptAnalyzer is not installed. Run the pinned installer first:

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install-psscriptanalyzer.ps1

The gate is deliberately hard: without the analyzer, scripts cannot land.
"@
}
$manifest = $manifests | Sort-Object { [version] (($_.Directory.Name -split '-')[0]) } -Descending | Select-Object -First 1
Import-Module -Name $manifest.FullName -Force

# ---- 2. Self-test: prove the custom rules actually fire ----
if ($SelfTest) {
    $tmpDir = Join-Path $env:TEMP "pssa-selftest-$PID"
    New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
    try {
        $badA = Join-Path $tmpDir 'bad-isfinite.ps1'
        $badB = Join-Path $tmpDir 'bad-ps7op.ps1'
        Set-Content -LiteralPath $badA -Value '$x = [double]::IsFinite(1.0)' -Encoding UTF8
        Set-Content -LiteralPath $badB -Value '$a = $null; $x = $a ?? 5' -Encoding UTF8

        # NOTE: do NOT pass -Severity here. PSScriptAnalyzer 1.25 declares
        # external (script) rules at Warning rule-level (ExternalRule.GetSeverity),
        # so a -Severity Error filter silently drops our custom rules from the run.
        # The records themselves carry Error severity; the gate filters records,
        # not rules. -CustomRulePath alone runs ONLY the custom rules (built-ins
        # would need -IncludeDefaultRules), which is what the self-test wants.
        $resA = @(Invoke-ScriptAnalyzer -Path $badA -CustomRulePath $customRulesPath)
        $isfiniteHit = @($resA | Where-Object { $_.RuleName -eq 'PSBanNetCoreOnlyStaticMembers' -and $_.Severity -eq 'Error' })
        if ($isfiniteHit.Count -lt 1) {
            Exit-With 3 "SELF-TEST FAIL: PSBanNetCoreOnlyStaticMembers did not fire on '[double]::IsFinite'."
        }

        # `??` is a parse error on 5.1 (caught by the ParseError gate) and a
        # custom-rule hit on pwsh 7. Either way the gate must fail the file.
        $resB = @(Invoke-ScriptAnalyzer -Path $badB -CustomRulePath $customRulesPath)
        $ps7OpHit = @($resB | Where-Object { $_.RuleName -eq 'PSBanPowerShell7OnlyOperators' -and $_.Severity -eq 'Error' })
        $parseErrorHit = @($resB | Where-Object { $_.Severity -eq 'ParseError' })
        if ($ps7OpHit.Count -lt 1 -and $parseErrorHit.Count -lt 1) {
            Exit-With 3 "SELF-TEST FAIL: PS7-only operator '??' was not flagged as Error or ParseError."
        }

        # Negative control: a clean file must produce zero custom-rule findings.
        $good = Join-Path $tmpDir 'good.ps1'
        Set-Content -LiteralPath $good -Value '$v = 1.0; if ($v -gt 0.5) { Write-Host "ok" }' -Encoding UTF8
        $resGood = @(Invoke-ScriptAnalyzer -Path $good -CustomRulePath $customRulesPath | Where-Object { $_.RuleName -like 'PSBan*' })
        if ($resGood.Count -ne 0) {
            Exit-With 3 "SELF-TEST FAIL: clean file produced custom-rule findings: $($resGood.Count)"
        }

        Write-Host 'SELF-TEST PASS: custom rules fire and clean files pass.'
        Exit-With 0
    }
    finally {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---- 3. Scope: every tracked .ps1 (auto-excludes node_modules/.venv/untracked) ----
$tracked = @(git -C $repoRoot ls-files '*.ps1')
if ($LASTEXITCODE -ne 0) {
    Exit-With 3 'git ls-files failed.'
}
if ($tracked.Count -eq 0) {
    Exit-With 3 'No tracked .ps1 files found; nothing to analyze.'
}
$scriptPaths = @($tracked | ForEach-Object { Join-Path $repoRoot ($_ -replace '/', '\') })

# ---- 4. Analyze ----
Write-Host "Analyzing $($scriptPaths.Count) tracked .ps1 files with PSScriptAnalyzer $((Get-Module PSScriptAnalyzer).Version)"
# -IncludeDefaultRules is REQUIRED: -CustomRulePath alone replaces the rule set
# with only the custom rules (1.25 help: 'only the custom rules found in the
# specified paths are used for the analysis'). -Path is a single string (not an
# array), so analyze per file and accumulate.
$records = @()
foreach ($scriptPath in $scriptPaths) {
    $records += @(
        Invoke-ScriptAnalyzer -Path $scriptPath -Settings $settingsPath -CustomRulePath $customRulesPath -IncludeDefaultRules -ErrorAction SilentlyContinue |
            Where-Object { $_ -is [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord] }
    )
}

$gateViolations = @($records | Where-Object { $_.Severity -in @('Error', 'ParseError') })
$warnings = @($records | Where-Object { $_.Severity -eq 'Warning' })

# ---- 5. Report ----
$report = @{
    generated_at_utc   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    analyzer_version   = (Get-Module PSScriptAnalyzer).Version.ToString()
    files_scanned      = $scriptPaths.Count
    gate_violations    = $gateViolations.Count
    warnings           = $warnings.Count
    findings           = @($records | ForEach-Object {
            $line = 0; $column = 0
            if ($null -ne $_.Extent) {
                $line = $_.Extent.StartLineNumber
                $column = $_.Extent.StartColumnNumber
            }
            [ordered]@{
                RuleName   = $_.RuleName
                Severity   = $_.Severity.ToString()
                ScriptName = $_.ScriptName
                Line       = $line
                Column     = $column
                Message    = $_.Message
            }
        })
}

if (-not $ReportPath) {
    $reportDir = Join-Path $repoRoot '.data'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $ReportPath = Join-Path $reportDir 'scriptanalyzer-report.json'
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
Write-Host "Report: $ReportPath"

if ($gateViolations.Count -gt 0) {
    Write-Host ''
    Write-Host "SCRIPT HYGIENE GATE FAILED: $($gateViolations.Count) Error/ParseError finding(s)."
    $gateViolations | Group-Object RuleName | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0,-45} {1}" -f $_.Name, $_.Count)
    }
    $gateViolations | Select-Object -First 10 | ForEach-Object {
        $line = if ($null -ne $_.Extent) { $_.Extent.StartLineNumber } else { 0 }
        Write-Host ("    {0}:{1} {2} -- {3}" -f $_.ScriptName, $line, $_.RuleName, $_.Message)
    }
    Exit-With 1
}

Write-Host "SCRIPT HYGIENE GATE PASSED ($($warnings.Count) warnings reported; see JSON for details)."
Exit-With 0
