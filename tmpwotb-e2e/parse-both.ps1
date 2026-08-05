$ErrorActionPreference = 'Stop'
$files = @('C:\work\wotb_reader\scripts\od-048-monitor-correlate-session.ps1', 'C:\work\wotb_reader\tmpwotb-e2e\fresh-launch-m1.ps1')
$fail = $false
foreach ($f in $files) {
    $tokens = $null; $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$tokens, [ref]$errors) > $null
    if ($errors -and $errors.Count -gt 0) {
        $fail = $true
        Write-Host ('PARSE_ERR ' + [IO.Path]::GetFileName($f))
        foreach ($e in $errors) { Write-Host ('  ' + $e.Message) }
    } else { Write-Host ('PARSE_OK ' + [IO.Path]::GetFileName($f)) }
}
exit $(if ($fail) { 1 } else { 0 })
