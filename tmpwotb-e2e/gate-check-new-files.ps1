[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$settings = Join-Path $repoRoot 'tools\psscriptanalyzer-settings.psd1'
$custom = Join-Path $repoRoot 'tools\psscriptanalyzer-custom-rules.psm1'
$installed = Get-ChildItem (Join-Path $repoRoot 'tools\external\installed') -Filter 'PSScriptAnalyzer.psd1' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $installed) { Write-Host 'NO_ANALYZER'; exit 2 }
Import-Module $installed.FullName -Force

$fatalRules = @('PSReviewUnusedParameter', 'PSAvoidAssignmentToAutomaticVariable', 'PSUseCompatibleSyntax')
$files = @(Get-ChildItem (Join-Path $PSScriptRoot '*.ps1') | Where-Object { $_.Name -notlike 'gate-check-*' } | ForEach-Object { $_.FullName })
$violating = @()
foreach ($f in $files) {
    $recs = @(Invoke-ScriptAnalyzer -Path $f -Settings $settings -CustomRulePath $custom -IncludeDefaultRules -ErrorAction SilentlyContinue |
        Where-Object { $_ -is [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord] })
    $bad = @($recs | Where-Object { $_.Severity -in @('Error', 'ParseError') -or $_.RuleName -in $fatalRules })
    if ($bad.Count -gt 0) {
        $violating += [pscustomobject]@{
            File = (Split-Path $f -Leaf)
            Count = $bad.Count
            Rules = (($bad | ForEach-Object { $_.RuleName } | Sort-Object -Unique) -join ',')
        }
    }
}
Write-Host ("checked=" + $files.Count + " files, violating=" + $violating.Count)
$violating | Format-Table -AutoSize | Out-String | Write-Host
exit 0
