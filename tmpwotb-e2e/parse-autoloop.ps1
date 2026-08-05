$errors = $null
$tokens = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    [ref]$tokens,
    [ref]$errors) > $null
if ($errors -and $errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Host ('PARSE_ERR: ' + $e.Message) }
    exit 1
}
Write-Host 'PARSE_OK'
