# Dual-engine parse check for a PowerShell script (PS 5.1 + PS 7).
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tmpwotb-e2e/check-parse.ps1 <path>
#         pwsh     -NoProfile -File tmpwotb-e2e/check-parse.ps1 <path>
# Prints PARSE_OK or PARSE_FAIL; exit 0/1. Used by the offline gate so the
# bash-inline `$` escaping trap never mangles the check again.
param([Parameter(Mandatory = $true)][string]$Path)
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $Path, [ref]$null, [ref]$errors)
if ($errors -and $errors.Count -gt 0) {
    Write-Output 'PARSE_FAIL'
    foreach ($e in $errors) { Write-Output ('  ' + $e.Message) }
    exit 1
}
Write-Output 'PARSE_OK'
exit 0
