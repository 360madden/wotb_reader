[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$trackedFiles = git -C $repositoryRoot ls-files
$findings = [System.Collections.Generic.List[string]]::new()

$patterns = [ordered]@{
    'OpenAI-style API key' = 'sk-(proj-)?[A-Za-z0-9_-]{20,}'
    'Private key header' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'Connection string password' = '(?i)(Password|Pwd)\s*=\s*[^;\s]+'
    'Private replay absolute path' = '(?i)[A-Z]:\\[^"\r\n]*\\replays\\[^"\r\n]*\.wotbreplay'
}

foreach ($relativePath in $trackedFiles) {
    $absolutePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        continue
    }

    $extension = [System.IO.Path]::GetExtension($absolutePath)
    if ($extension -in @('.png', '.wotbreplay', '.db')) {
        continue
    }

    $content = Get-Content -Raw -LiteralPath $absolutePath -ErrorAction SilentlyContinue
    foreach ($entry in $patterns.GetEnumerator()) {
        if ($content -match $entry.Value) {
            $findings.Add("$($entry.Key): $relativePath")
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Error $_ }
    throw 'Repository scan found potentially sensitive tracked content.'
}

Write-Host "Repository scan passed for $($trackedFiles.Count) tracked files."
