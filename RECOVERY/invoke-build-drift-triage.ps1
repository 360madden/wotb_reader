<#
.SYNOPSIS
    Read-only build-drift triage: compares the installed wotblitz.exe against
    every versioned offset table in memory-offsets/ and reports drift.

.DESCRIPTION
    Part of the RECOVERY module (see RECOVERY/README.md and
    RECOVERY/build-drift-recovery.md). This script NEVER mutates offset
    tables or evidence. It only reports:

      - the installed executable's SHA-256 (if found);
      - for each memory-offsets/<version>.json table: game version, recorded
        hash, whether it matches the installed exe, published field names,
        and the module-relative anchor RVAs (rootRva / vftableScan hop
        kinds) that must be re-derived after a build change;
      - a drift verdict with a stable exit code:
            0  same build (installed hash matches the newest readable table)
            1  drifted (installed hash matches no table)
            2  executable not found (supply -GameExePath)
            3  failure

    The verdict drives the steps in RECOVERY/build-drift-recovery.md;
    nothing is migrated automatically (evidence-first rule).

.PARAMETER GameExePath
    Explicit path to wotblitz.exe. If omitted, the same install-location
    search list as tools/compute-exe-hash.ps1 is used.

.PARAMETER OffsetDir
    Path to the memory-offsets directory. Defaults to the repo's
    memory-offsets/ folder.

.PARAMETER ReportPath
    Where the JSON report is written. Defaults to .build/reports/
    build-drift-<utc-file-time>.json (outside the committed tree).

.PARAMETER Quiet
    Suppresses the console summary. The JSON report is still written.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File RECOVERY\invoke-build-drift-triage.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File RECOVERY\invoke-build-drift-triage.ps1 -GameExePath 'C:\Games\World_of_Tanks_Blitz\wotblitz.exe' -Quiet
#>
[CmdletBinding()]
param(
    [string] $GameExePath = '',
    [string] $OffsetDir = '',
    [string] $ReportPath = '',
    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$playbook = 'RECOVERY/build-drift-recovery.md'

function Exit-With {
    param([int] $Code, [string] $Message = '')
    if ($Message) { Write-Host $Message }
    exit $Code
}

function Find-WotBlitzExe {
    $candidates = @(
        'C:\Games\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\Steam\steamapps\common\World of Tanks Blitz\wotblitz.exe',
        (Join-Path $env:LOCALAPPDATA 'World_of_Tanks_Blitz\wotblitz.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    $storeRoot = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $storeRoot) {
        $packages = @(Get-ChildItem -LiteralPath $storeRoot -Directory -Filter '*WoTBlitz*' -ErrorAction SilentlyContinue)
        foreach ($pkg in $packages) {
            $exe = Join-Path $pkg.FullName 'wotblitz.exe'
            if (Test-Path -LiteralPath $exe) {
                return $exe
            }
        }
    }
    return $null
}

function Get-SafeVersion {
    param([string] $Value)
    try {
        return [version] $Value
    }
    catch {
        return [version] '0.0.0.0'
    }
}

function Read-OffsetTable {
    param([string] $Path)
    $raw = Get-Content -LiteralPath $Path -Raw
    return ($raw | ConvertFrom-Json)
}

function Get-AnchorHops {
    param($Chains)
    $results = @()
    if ($null -eq $Chains) { return $results }
    foreach ($prop in $Chains.PSObject.Properties) {
        $fieldName = $prop.Name
        if ($null -eq $prop.Value) { continue }
        foreach ($hop in $prop.Value) {
            # Strict-mode-safe property reads: a hop may omit kind/value/note
            # (e.g. a minimal synthetic table) without crashing the triage.
            $kindProp = $hop.PSObject.Properties['kind']
            $kind = if ($null -ne $kindProp) { [string] $kindProp.Value } else { '' }
            if ($kind -ne 'rootRva' -and $kind -ne 'vftableScan') { continue }
            $valueProp = $hop.PSObject.Properties['value']
            $value = if ($null -ne $valueProp) { $valueProp.Value } else { $null }
            $noteProp = $hop.PSObject.Properties['note']
            $note = if ($null -ne $noteProp) { [string] $noteProp.Value } else { '' }
            $results += [pscustomobject]@{
                field = $fieldName
                kind  = $kind
                value = $value
                note  = $note
            }
        }
    }
    return $results
}

# ---- locate executable ----
$exePath = $null
$exeVersion = ''
$exeHash = ''
if ($GameExePath) {
    if (-not (Test-Path -LiteralPath $GameExePath)) {
        Exit-With 2 "Executable not found: $GameExePath (exit 2 = executable not found; supply a valid -GameExePath or omit it)"
    }
    $exePath = $GameExePath
}
else {
    $exePath = Find-WotBlitzExe
}

try {
if ($exePath) {
    $exeVersion = [string] (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
    $exeSha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---- read tables ----
if (-not $OffsetDir) { $OffsetDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'memory-offsets' }
$tableRows = @()
$newestVersion = [version] '0.0.0.0'
$newestRow = $null
foreach ($jsonFile in @(Get-ChildItem -LiteralPath $OffsetDir -Filter '*.json' | Sort-Object Name)) {
    if ($jsonFile.Name -eq 'schema.json') { continue }
    $filePath = $jsonFile.FullName
    $table = $null
    $readError = ''
    try {
        $table = Read-OffsetTable -Path $filePath
    }
    catch {
        $readError = $_.Exception.Message
    }
    if ($null -eq $table) {
        $tableRows += [pscustomobject]@{
            file             = $jsonFile.Name
            gameVersion      = ''
            executableSha256 = ''
            matchesInstalled = $null
            fields           = @()
            anchors          = @()
            readError        = $readError
        }
        continue
    }
    $chainsProp = $table.PSObject.Properties['chains']
    $fields = @()
    $anchors = @()
    if ($null -ne $chainsProp -and $null -ne $chainsProp.Value) {
        $chains = $chainsProp.Value
        $fields = @($chains.PSObject.Properties.Name)
        $anchors = @(Get-AnchorHops -Chains $chains)
    }
    $recordedHash = [string] $table.executableSha256
    $match = $null
    if ($exeSha256) {
        $match = ($recordedHash.ToLowerInvariant() -eq $exeSha256)
    }
    $row = [pscustomobject]@{
        file             = $jsonFile.Name
        gameVersion      = [string] $table.gameVersion
        executableSha256 = $recordedHash
        matchesInstalled = $match
        fields           = $fields
        anchors          = $anchors
        readError        = $readError
    }
    $tableRows += $row
    $rowVersion = Get-SafeVersion -Value $row.gameVersion
    if ($rowVersion -gt $newestVersion) {
        $newestVersion = $rowVersion
        $newestRow = $row
    }
}

# ---- verdict ----
# Fail-closed: drift (exit 1) means the tables are readable and the installed
# hash matches none of them. Anything that prevents a trustworthy comparison
# (unreadable table, no tables at all) is a failure (exit 3), not a verdict.
$verdict = ''
$exit = 3
$hadReadError = @($tableRows | Where-Object { $_.readError })
if (-not $exePath) {
    $verdict = 'exe-not-found'
    $exit = 2
}
elseif ($hadReadError.Count -gt 0) {
    $verdict = 'read-error'
    $exit = 3
}
elseif ($null -eq $newestRow) {
    $verdict = 'no-readable-table'
    $exit = 3
}
elseif ($newestRow.matchesInstalled -eq $true) {
    $verdict = 'same-build'
    $exit = 0
}
else {
    $verdict = 'drifted'
    $exit = 1
}

# ---- report ----
$report = [pscustomobject]@{
    tool          = 'RECOVERY/invoke-build-drift-triage.ps1'
    comparedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    verdict       = $verdict
    exitCode      = $exit
    exe           = if ($exePath) { [pscustomobject]@{ path = $exePath; productVersion = $exeVersion; sha256 = $exeSha256 } } else { $null }
    newestTable   = if ($newestRow) { $newestRow.gameVersion } else { '' }
    tables        = $tableRows
    playbook      = $playbook
}

if (-not $ReportPath) {
    $reportsDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path '.build\reports'
    $ReportPath = Join-Path $reportsDir ("build-drift-" + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '.json')
}
$reportDir = Split-Path -Parent $ReportPath
if (-not (Test-Path -LiteralPath $reportDir)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
catch {
    Exit-With 3 "Failure: $($_.Exception.Message) (exit 3 = failure)"
}

# ---- console summary ----
if (-not $Quiet) {
    Write-Host '=== WoT Blitz build-drift triage ===' -ForegroundColor Cyan
    if ($exePath) {
        Write-Host "Exe      : $exePath"
        Write-Host "Product  : $exeVersion"
        Write-Host "SHA-256  : $exeSha256"
    }
    else {
        Write-Host 'Exe      : NOT FOUND (use -GameExePath)'
    }
    Write-Host ''
    foreach ($row in $tableRows) {
        $matchLabel = 'n/a'
        if ($row.matchesInstalled -eq $true) { $matchLabel = 'MATCH' }
        elseif ($row.matchesInstalled -eq $false) { $matchLabel = 'DRIFT' }
        $anchorCount = @($row.anchors).Count
        $hash8 = ''
        if ($row.executableSha256) {
            $hash8 = $row.executableSha256.Substring(0, [math]::Min(8, $row.executableSha256.Length))
        }
        Write-Host ("{0,-18} fields={1,-2} anchors={2,-2} exe={3}  {4}" -f $row.gameVersion, @($row.fields).Count, $anchorCount, $hash8, $matchLabel)
    }
    Write-Host ''
    Write-Host "Verdict  : $verdict (exit $exit)" -ForegroundColor Yellow
    Write-Host "Report   : $ReportPath"
    Write-Host "Playbook : $playbook"
    if ($verdict -eq 'drifted') {
        Write-Host ''
        Write-Host 'Build drift detected. Follow RECOVERY/build-drift-recovery.md' -ForegroundColor Red
        Write-Host 'before any live session; every published chain is hash-bound' -ForegroundColor Red
        Write-Host 'to the recorded executable and must be re-verified.' -ForegroundColor Red
    }
}

Exit-With $exit