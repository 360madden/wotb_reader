[CmdletBinding()]
param(
    [switch] $SkipDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$registryPath = Join-Path $repoRoot 'tools\external\tools.lock.json'
$downloadsDir = Join-Path $repoRoot 'tools\external\downloads'
$installedRoot = Join-Path $repoRoot 'tools\external\installed\psscriptanalyzer'

# ---- 1. Read the pinned version + hash from the registry (single source of truth) ----
$registry = Get-Content -Raw -LiteralPath $registryPath | ConvertFrom-Json
$entry = $registry.tools | Where-Object { $_.name -eq 'PSScriptAnalyzer' }
if ($null -eq $entry) {
    throw 'PSScriptAnalyzer is not registered in tools/external/tools.lock.json; register it first.'
}
if ($entry.version -ne '1.25.0') {
    Write-Warning "Registry pins PSScriptAnalyzer $($entry.version), not 1.25.0. Proceeding with the pinned version ($($entry.version))."
}

$version = [string] $entry.version
$expectedSha256 = ([string] $entry.sha256).ToLowerInvariant()
$nupkgName = "psscriptanalyzer-$version.nupkg"
$nupkgPath = Join-Path $downloadsDir $nupkgName
$canonicalUrl = "https://www.powershellgallery.com/api/v2/package/PSScriptAnalyzer/$version"
$manifestPath = Join-Path $installedRoot "$version\PSScriptAnalyzer.psd1"

# ---- 2. Download the pinned nupkg (idempotent; never re-download a verified file) ----
if (-not (Test-Path -LiteralPath $nupkgPath)) {
    if ($SkipDownload) {
        throw "Downloads skipped (-SkipDownload) but $nupkgPath is missing."
    }
    Write-Host "Downloading PSScriptAnalyzer $version from $canonicalUrl"
    New-Item -ItemType Directory -Force -Path $downloadsDir | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $canonicalUrl -OutFile $nupkgPath -TimeoutSec 180 -UseBasicParsing
}

# ---- 3. Verify SHA-256 against the registry (fail hard on mismatch) ----
$actualSha256 = (Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "SHA-256 mismatch for $nupkgPath`: expected $expectedSha256, got $actualSha256. Delete the download and re-run."
}
Write-Host "SHA-256 verified: $actualSha256"

# ---- 4. Extract the nupkg (a zip) into the pinned install dir ----
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Write-Host "Extracting to $installedRoot\$version"
    New-Item -ItemType Directory -Force -Path (Join-Path $installedRoot $version) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkgPath, (Join-Path $installedRoot $version))
}
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Extraction produced no PSScriptAnalyzer.psd1 at $manifestPath."
}

# ---- 5. Smoke test: import + rule count on both hosts ----
$smokeScript = Join-Path $env:TEMP "pssa-smoke-$PID.ps1"
@"
`$ErrorActionPreference = 'Stop'
Import-Module '$manifestPath' -Force
`$rules = @(Get-ScriptAnalyzerRule).Count
Write-Output ('RULES=' + `$rules)
if (`$rules -lt 50) { throw 'Suspiciously few rules loaded: ' + `$rules }
"@ | Set-Content -LiteralPath $smokeScript -Encoding UTF8

$hosts = @(
    @{ Label = 'Windows PowerShell 5.1'; Exe = 'powershell.exe'; Field = 'import_smoke_5_1' },
    @{ Label = 'pwsh 7';                 Exe = 'pwsh.exe';       Field = 'import_smoke_pwsh7' }
)
$smokeFailed = $false
foreach ($h in $hosts) {
    $exe = Get-Command $h.Exe -ErrorAction SilentlyContinue
    if ($null -eq $exe) {
        $entry.verification.($h.Field) = 'skipped-host-missing'
        Write-Warning "$($h.Label) not found; skipping its smoke test."
        continue
    }
    Write-Host "Smoke test: $($h.Label)"
    & $exe.Source -NoProfile -ExecutionPolicy Bypass -File $smokeScript 2>&1 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) {
        $entry.verification.($h.Field) = 'fail'
        $smokeFailed = $true
    }
    else {
        $entry.verification.($h.Field) = 'pass'
    }
}
Remove-Item -LiteralPath $smokeScript -Force -ErrorAction SilentlyContinue
if ($smokeFailed) {
    throw 'PSScriptAnalyzer import smoke test failed on at least one host.'
}

# ---- 6. Update the registry verification block (scoped text patch, minimal diff) ----
# A full JSON round-trip reformats the whole registry (ConvertTo-Json emits 4-space
# indent and reflows everything), so patch only the PSScriptAnalyzer entry block.
$raw = [System.IO.File]::ReadAllText($registryPath)
$entryAnchor = '"name": "PSScriptAnalyzer"'
$nextAnchor = '"name": "Grok Build"'
$entryStart = $raw.IndexOf($entryAnchor)
$entryEnd = $raw.IndexOf($nextAnchor, $entryStart)
if ($entryStart -lt 0 -or $entryEnd -lt 0) {
    throw "Could not locate the PSScriptAnalyzer entry block in $registryPath."
}
$entryBlock = $raw.Substring($entryStart, $entryEnd - $entryStart)
$utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$blockUpdates = @(
    @{ Old = '"installation_status": "pending-install"'; New = '"installation_status": "verified-local"' },
    @{ Old = '"verified_at_utc": null';                   New = "`"verified_at_utc`": `"$utc`"" },
    @{ Old = '"module_manifest_present": false';          New = '"module_manifest_present": true' },
    @{ Old = '"sha256_matches": false';                   New = '"sha256_matches": true' },
    @{ Old = '"import_smoke_5_1": "pending"';            New = "`"import_smoke_5_1`": `"$($entry.verification.import_smoke_5_1)`"" },
    @{ Old = '"import_smoke_pwsh7": "pending"';          New = "`"import_smoke_pwsh7`": `"$($entry.verification.import_smoke_pwsh7)`"" }
)
foreach ($u in $blockUpdates) {
    # Idempotent: replace the pending form when present; when it is already
    # gone (e.g. a timestamp from a previous run), the field is already
    # patched and no throw is warranted.
    if ($entryBlock.Contains($u.Old)) {
        $entryBlock = $entryBlock.Replace($u.Old, $u.New)
    }
}

$updated = $raw.Substring(0, $entryStart) + $entryBlock + $raw.Substring($entryEnd)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($registryPath, $updated, $utf8NoBom)
Write-Host "Registered verification in $registryPath"
Write-Host "PSScriptAnalyzer $version installed and verified at $installedRoot\$version"
