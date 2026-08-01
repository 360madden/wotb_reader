[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$trackedFiles = git -C $repositoryRoot ls-files
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed with exit code $LASTEXITCODE."
}

$findings = [System.Collections.Generic.List[string]]::new()

$storageSentinel = 'src/WotBTreader.Storage.Sqlite/WotBTreader.Storage.Sqlite.csproj'
git -C $repositoryRoot check-ignore -q --no-index $storageSentinel
if ($LASTEXITCODE -gt 1) {
    throw "git check-ignore failed with exit code $LASTEXITCODE."
}

if ($LASTEXITCODE -eq 0) {
    $findings.Add("Ignore policy hides the storage source project: $storageSentinel")
}

# Runtime-data ignore patterns match case-insensitively on Windows worktrees,
# so any ignored file inside a source tree indicates a hidden-source hazard
# (BLK-0005, BLK-0012). tools/external stays ignored by design.
$hiddenSources = git -C $repositoryRoot ls-files --others --ignored --exclude-standard -- src tests tools/src tools/tests scripts docs ultimate-scanner |
    Where-Object {
        $_ -notmatch '(^|/)(bin|obj|TestResults|__pycache__)/' -and
        $_ -notmatch '(?i)(^|/)(appsettings|launchSettings)\.Local\.json$' -and
        $_ -notmatch '\.user$'
    }
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files for ignored source files failed with exit code $LASTEXITCODE."
}

foreach ($hiddenSource in $hiddenSources) {
    $findings.Add("Ignore policy hides a source-tree file: $hiddenSource")
}

$visibleBuildOutputs = git -C $repositoryRoot ls-files --others --exclude-standard |
    Where-Object { $_ -match '(^|/)(bin|obj|TestResults)/' }
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files for build outputs failed with exit code $LASTEXITCODE."
}

foreach ($buildOutput in $visibleBuildOutputs) {
    $findings.Add("Build output is not ignored: $buildOutput")
}

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
